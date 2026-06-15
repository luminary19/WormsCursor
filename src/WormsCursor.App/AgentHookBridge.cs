using System.IO.Pipes;
using System.Text.Json;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// The <c>WormsCursor.exe hook --tool &lt;t&gt; [--event &lt;e&gt;]</c> entry path: a throwaway process
/// an AI agent's hook fires. It normalises the tool's native payload to an
/// <see cref="AgentEventMessage"/>, writes one JSON line to the running tray app's pipe, and exits 0.
///
/// FAIL-SILENT above all — it must never hang or fail the agent. The pipe connect is capped under
/// half a second, every error is swallowed to <c>bridge.log</c>, and we always return 0.
/// </summary>
static class AgentHookBridge
{
    /// <summary>True when argv selects the hook verb, so Main can short-circuit before any UI.</summary>
    public static bool IsHookInvocation(string[] args) => args.Length > 0 && args[0] == "hook";

    public static int Run(string[] args)
    {
        try
        {
            var msg = Build(args);
            if (msg is not null) Send(msg);
        }
        catch (Exception ex)
        {
            Log("hook error: " + ex.Message);
        }
        return 0; // ALWAYS succeed — never block or fail the agent
    }

    static AgentEventMessage? Build(string[] args)
    {
        string tool = ArgValue(args, "--tool") ?? "unknown";
        string? evOverride = ArgValue(args, "--event"); // Cursor/Kiro register an explicit normalised event

        // Codex hands the event as a single JSON argv (the last arg); everyone else uses stdin.
        return tool == "codex" ? FromCodex(args, tool) : FromStdin(tool, evOverride);
    }

    // Claude Code / Cursor: payload arrives on stdin as JSON. We map Claude's hook_event_name
    // (+ notification_type) ourselves; Cursor passes the normalised event via --event.
    static AgentEventMessage? FromStdin(string tool, string? evOverride)
    {
        string raw = Console.IsInputRedirected ? Console.In.ReadToEnd() : "";
        string? rawEvent = null, notifType = null, sessionId = null, project = null, message = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                rawEvent = Str(root, "hook_event_name");
                notifType = Str(root, "notification_type");
                sessionId = Str(root, "session_id");
                project = Str(root, "cwd");
                message = Str(root, "message");
            }
            catch (JsonException) { /* leave fields null; an --event override may still carry us */ }
        }

        string? norm = evOverride ?? NormalizeClaude(rawEvent, notifType);
        if (norm is null) return null; // not an event we surface

        return new AgentEventMessage
        {
            Tool = tool,
            Event = norm,
            RawEvent = rawEvent ?? evOverride,
            SessionId = sessionId,
            Project = project,
            Message = message,
            Ts = DateTime.UtcNow.ToString("o"),
        };
    }

    // Codex: notify = ["<abs>", "hook", "--tool", "codex"] and Codex appends the event JSON as the
    // final argv. Only agent-turn-complete exists today — no native "awaiting", so we don't fake it.
    static AgentEventMessage? FromCodex(string[] args, string tool)
    {
        string? json = args.Length > 0 && args[^1].StartsWith('{') ? args[^1] : null;
        if (json is null) return null;

        string? type = null, session = null, project = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            type = Str(root, "type");
            session = Str(root, "thread-id");
            project = Str(root, "cwd");
            message = Str(root, "last-assistant-message");
        }
        catch (JsonException) { return null; }

        if (type != "agent-turn-complete") return null;
        return new AgentEventMessage
        {
            Tool = tool,
            Event = "turn_complete",
            RawEvent = type,
            SessionId = session,
            Project = project,
            Message = message,
            Ts = DateTime.UtcNow.ToString("o"),
        };
    }

    static string? NormalizeClaude(string? hookEvent, string? notificationType) => hookEvent switch
    {
        // You submitted a prompt, or (re)opened the session → you're back, the agent isn't waiting.
        "UserPromptSubmit" or "SessionStart" => "thinking_started",
        // The session ended (/exit, Ctrl+C, /clear, logout) → drop it so its logo doesn't linger.
        "SessionEnd" => "session_end",
        // Only the notifications that mean "the agent is blocked on you" become awaiting_user.
        "Notification" => notificationType is "permission_prompt" or "idle_prompt" or "elicitation_dialog"
            ? "awaiting_user" : null,
        "Stop" => "turn_complete",
        "StopFailure" => "error",
        "PreToolUse" or "PostToolUse" => "tool_use",
        _ => null,
    };

    static void Send(AgentEventMessage msg)
    {
        using var client = new NamedPipeClientStream(".", AgentPipeServer.PipeName, PipeDirection.Out);
        client.Connect(400); // < 500 ms; throws if no tray app is listening (swallowed by Run)
        using var w = new StreamWriter(client) { AutoFlush = true };
        w.WriteLine(msg.ToJsonLine());
    }

    static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    static string? Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static void Log(string text)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WormsCursor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "bridge.log"), $"{DateTime.Now:s} {text}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
