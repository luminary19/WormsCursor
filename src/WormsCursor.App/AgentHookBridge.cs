using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WormsCursor.Core;

namespace WormsCursor.App;

/// <summary>
/// The <c>WormsCursor.exe hook --tool &lt;t&gt;</c> entry path: a throwaway process
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
            AppLog.Write("hook error: " + ex.Message);
        }
        return 0; // ALWAYS succeed — never block or fail the agent
    }

    static AgentEventMessage? Build(string[] args)
    {
        string tool = ArgValue(args, "--tool") ?? "unknown";
        string raw = Console.IsInputRedirected ? Console.In.ReadToEnd() : "";
        return Parse(tool, raw);
    }

    internal static AgentEventMessage? Parse(string tool, string raw)
        => tool == "codex" ? FromCodex(raw) : FromClaude(tool, raw);

    // Claude Code: the hook payload arrives on stdin as JSON and everything is derived from it.
    static AgentEventMessage? FromClaude(string tool, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? rawEvent = Str(root, "hook_event_name");
            var (norm, itemIds) = NormalizeClaude(root, rawEvent);
            if (norm is null) return null; // not an event we surface
            string[]? callIds = rawEvent switch
            {
                "PermissionRequest" or "PreToolUse" when norm == "awaiting_user" => new[] { OpenedCall(root) },
                "PostToolUse" or "PostToolUseFailure" => FinishedCall(root),
                _ => null,
            };

            return new AgentEventMessage
            {
                Tool = tool,
                Event = norm,
                RawEvent = rawEvent,
                SessionId = Str(root, "session_id"),
                AgentId = Str(root, "agent_id"), // set only inside a subagent
                Project = Str(root, "cwd"),
                Message = Str(root, "message"),
                InputIds = itemIds,
                CallIds = callIds,
                Ts = DateTime.UtcNow.ToString("o"),
            };
        }
        catch (JsonException) { return null; }
    }

    // Codex desktop and CLI share hooks.json and the same stdin lifecycle payloads.
    // Subagent completions use SubagentStop and must not ask the human to take over.
    // A question tool's PreToolUse and its PostToolUse share the tool_use_id, so that prompt is keyed
    // by its call. Codex's PermissionRequest carries no tool_use_id, and its tool_input is the approval
    // request (e.g. the command plus a justification), not the call's own input, so nothing in it
    // matches the call that later runs: that prompt names no call, and the agent's next tool event
    // answers it (Codex sends no per-batch event that could do better).
    static AgentEventMessage? FromCodex(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? rawEvent = Str(root, "hook_event_name");
            string? toolName = Str(root, "tool_name");
            string[]? inputIds = null;
            string? norm = rawEvent switch
            {
                "UserPromptSubmit" => "thinking_started",
                // Resuming a session clears a finish notice, but is not an answer to a question.
                "SessionStart" => "tool_use",
                "SessionEnd" or "Interrupt" => "session_end",
                "PermissionRequest" => "awaiting_user",
                "PreToolUse" when toolName is "request_user_input" or "request_permissions" => "awaiting_user",
                "PreToolUse" or "PostToolUse" => "tool_use",
                "Stop" => "turn_complete",
                _ => null,
            };
            if (norm is null) return null;

            // The async tool finishes immediately; PostToolUse means the questions were displayed,
            // not answered. Track their actual app IDs until corresponding user-input hooks arrive.
            if (rawEvent == "PostToolUse" && toolName == "request_user_input_async")
            {
                inputIds = AsyncQuestionIds(root);
                if (inputIds.Length > 0) norm = "input_requested";
            }
            else if (rawEvent == "UserPromptSubmit")
            {
                inputIds = AnsweredQuestionIds(Str(root, "prompt"));
                if (inputIds is not null) norm = "input_answered";
            }

            return new AgentEventMessage
            {
                Tool = "codex",
                Event = norm,
                RawEvent = rawEvent,
                SessionId = Str(root, "session_id"),
                AgentId = Str(root, "agent_id"), // set only inside a subagent
                Project = Str(root, "cwd"),
                Message = Str(root, "last_assistant_message"),
                InputIds = inputIds is { Length: > 0 } ? inputIds : null,
                CallIds = norm switch
                {
                    "awaiting_user" when rawEvent == "PreToolUse" => new[] { OpenedCall(root) },
                    "tool_use" when rawEvent == "PostToolUse" => FinishedCall(root),
                    _ => null,
                },
                Ts = DateTime.UtcNow.ToString("o"),
            };
        }
        catch (JsonException) { return null; }
    }

    static string[] AsyncQuestionIds(JsonElement root)
    {
        string? callId = Str(root, "tool_use_id");
        if (string.IsNullOrEmpty(callId)
            || !root.TryGetProperty("tool_input", out var input)
            || input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("tool_response", out var response)) return Array.Empty<string>();

        // FunctionToolOutput serializes its body as a string containing {"accepted":true}.
        if (response.ValueKind != JsonValueKind.String) return Array.Empty<string>();
        using var accepted = JsonDocument.Parse(response.GetString()!);
        if (accepted.RootElement.ValueKind != JsonValueKind.Object
            || !accepted.RootElement.TryGetProperty("accepted", out var ok)
            || ok.ValueKind != JsonValueKind.True) return Array.Empty<string>();

        return Enumerable.Range(0, questions.GetArrayLength())
            .Select(index => JsonSerializer.Serialize(new object[] { "request_user_input_async", callId, index }))
            .ToArray();
    }

    static string[]? AnsweredQuestionIds(string? prompt)
    {
        if (prompt is null) return null;
        string text = prompt.Trim();
        const string idePrefix = "# Context from my IDE setup:\n", requestDelimiter = "\n## My request for Codex:\n";
        if (text.StartsWith(idePrefix, StringComparison.Ordinal))
        {
            int start = text.LastIndexOf(requestDelimiter, StringComparison.Ordinal);
            if (start < 0) return null;
            text = text[(start + requestDelimiter.Length)..].Trim();
        }
        const string open = "<send_user_message_question_reply>", close = "</send_user_message_question_reply>";
        if (!text.StartsWith(open, StringComparison.Ordinal) || !text.EndsWith(close, StringComparison.Ordinal)) return null;
        try
        {
            using var reply = JsonDocument.Parse(text[open.Length..^close.Length]);
            var root = reply.RootElement;
            var replies = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : new[] { root };
            if (replies.Length == 0) return null;
            var ids = new List<string>();
            foreach (var item in replies)
            {
                string? id = Str(item, "questionItemId");
                if (id is null || Str(item, "question") is null || Str(item, "answer") is null) return null;
                using var parsed = JsonDocument.Parse(id);
                var parts = parsed.RootElement;
                if (parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() == 3
                    && parts[0].ValueKind == JsonValueKind.String && parts[0].GetString() == "request_user_input_async"
                    && parts[1].ValueKind == JsonValueKind.String && parts[2].ValueKind == JsonValueKind.Number
                    && parts[2].TryGetInt32(out int index) && index >= 0)
                    ids.Add(JsonSerializer.Serialize(new object[] { "request_user_input_async", parts[1].GetString()!, index }));
            }
            return ids.ToArray();
        }
        catch (JsonException) { return null; }
    }

    // Every "blocked on you" state comes from an event that names its owner and has a matching
    // "resolved" event, so it can only ever be cleared by its own answer:
    //   - PermissionRequest and AskUserQuestion's PreToolUse run in the asking agent's context, so their
    //     payload carries that subagent's agent_id, and they name the tool call they block (see
    //     OpenedCall). That call's PostToolUse / PostToolUseFailure answers the prompt; another call
    //     finishing does not, even the same agent's parallel one. A denied call never runs, so it has
    //     no Post event: PostToolBatch, which fires once every call of the agent's batch has resolved,
    //     answers whatever is left (it also covers an approval that rewrote the call's input).
    //   - Elicitation (an MCP server asking for input) runs outside any agent's context, so it carries
    //     no agent_id. It is keyed by its own identity instead, which ElicitationResult repeats.
    // Notification is deliberately NOT a blocked signal: it carries no agent_id, so a subagent's prompt
    // would be filed under the main agent and never cleared by that subagent. Its only use is the
    // idle_prompt reminder (Claude never sends it while a dialog is open), a "finished" nudge.
    static (string? Event, string[]? ItemIds) NormalizeClaude(JsonElement root, string? hookEvent) => hookEvent switch
    {
        // You submitted a prompt, or (re)opened the session → you're back, the agent isn't waiting.
        "UserPromptSubmit" or "SessionStart" => ("thinking_started", null),
        // The session ended (/exit, Ctrl+C, /clear, logout) → drop it so its logo doesn't linger.
        "SessionEnd" => ("session_end", null),
        "PermissionRequest" => ("awaiting_user", null),
        "PreToolUse" when Str(root, "tool_name") == "AskUserQuestion" => ("awaiting_user", null),
        "PreToolUse" or "PostToolUse" or "PostToolUseFailure" => ("tool_use", null),
        "PostToolBatch" => ("tools_resolved", null),
        "Elicitation" => ("awaiting_user", new[] { ElicitationId(root) }),
        "ElicitationResult" => ("input_answered", new[] { ElicitationId(root) }),
        "Notification" when Str(root, "notification_type") == "idle_prompt" => ("turn_complete", null),
        // Spawning background subagents ends the turn at once (Stop fires right after SubagentStart),
        // but Claude resumes on its own when they report back — so it isn't your move yet. Stop's
        // payload lists in-flight work in `background_tasks`; only a Stop with no subagent there is a
        // real finish.
        "Stop" => (HasRunningSubagent(root) ? null : "turn_complete", null),
        "StopFailure" => ("error", null),
        _ => (null, null),
    };

    // Elicitation and ElicitationResult both carry the MCP server name; elicitation_id is present only
    // for URL-mode requests. The pair names one prompt on both sides.
    static string ElicitationId(JsonElement root)
        => JsonSerializer.Serialize(new[] { "elicitation", Str(root, "mcp_server_name") ?? "", Str(root, "elicitation_id") ?? "" });

    // True when Claude's Stop payload lists a background subagent or workflow, e.g.
    // "background_tasks": [{ "type": "subagent", "status": "running", ... }]. Claude only lists in-flight
    // (running/pending) work, so any entry counts. Background shells are deliberately ignored: a dev
    // server may never exit, so it doesn't mean Claude will come back on its own.
    static bool HasRunningSubagent(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("background_tasks", out var tasks)
           && tasks.ValueKind == JsonValueKind.Array
           && tasks.EnumerateArray().Any(t => Str(t, "type") is "subagent" or "workflow");

    // The identity of the call a prompt blocks: its tool_use_id when the payload has one (a question
    // tool's PreToolUse), else its tool name and input (PermissionRequest has no tool_use_id).
    static string OpenedCall(JsonElement root)
        => Str(root, "tool_use_id") is { Length: > 0 } id ? "call:" + id : InputFingerprint(root);

    // Every identity of a call that just finished, so it matches the prompt about it either way.
    static string[] FinishedCall(JsonElement root)
        => Str(root, "tool_use_id") is { Length: > 0 } id
            ? new[] { "call:" + id, InputFingerprint(root) }
            : new[] { InputFingerprint(root) };

    // The tool name plus a hash of the compact tool_input JSON (the input can be a whole file, so the
    // pipe line carries only the hash). Both sides of a call serialize the same input the same way.
    static string InputFingerprint(JsonElement root)
    {
        string input = root.TryGetProperty("tool_input", out var value)
            ? JsonNode.Parse(value.GetRawText())?.ToJsonString() ?? "null"
            : "";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)), 0, 12);
        return $"input:{Str(root, "tool_name")}:{hash}";
    }

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
}
