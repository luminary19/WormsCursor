using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WormsCursor.App.Services;

/// <summary>Whether a tool's config currently carries our hook.</summary>
public enum HookState
{
    /// <summary>No WormsCursor hook present.</summary>
    NotRegistered,
    /// <summary>Our hook is registered for the full current event set.</summary>
    Registered,
    /// <summary>Our hook is present, but only for some of the events we now register — an older
    /// version registered a smaller set (e.g. before <c>SessionEnd</c> was added, so /clear and /exit
    /// no longer clear the indicator). Re-registering refreshes it to the full set.</summary>
    Outdated,
    /// <summary>The tool already has a conflicting custom hook we won't overwrite (Codex's single
    /// <c>notify</c>). The user must clear it before we can register.</summary>
    ConfigConflict,
}

/// <summary>A supported agent tool and where its hook config lives.</summary>
public sealed record HookTool(string Id, string DisplayName, string ConfigPath, string ConfigHint);

/// <summary>
/// Registers / unregisters / inspects WormsCursor's <c>hook</c> bridge in each AI agent tool's
/// config, so the tools forward lifecycle events to the running tray app. Every write backs the
/// original file up to <c>&lt;file&gt;.wormscursor.bak</c> first and merges rather than overwrites,
/// and a malformed config is reported (never clobbered). Our entries are tagged by the
/// <c>hook --tool &lt;id&gt;</c> command marker, so they can be found again across reinstalls even
/// if the exe path moves.
/// </summary>
public static class AgentHookRegistrar
{
    // The events we register with Claude Code. We deliberately skip the high-frequency
    // Pre/PostToolUse (they'd spawn a hook process per tool call). UserPromptSubmit + SessionStart
    // clear the "waiting" state when you come back; SessionEnd drops the session when you exit, so
    // its logo doesn't linger until the TTL.
    static readonly string[] ClaudeEvents =
        { "UserPromptSubmit", "Notification", "Stop", "StopFailure", "SessionStart", "SessionEnd" };

    // Relaxed encoder so the exe-path quotes serialise as \" (not ") and the file stays
    // readable — it's a config a human may open. Still valid JSON.
    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Test seam: when set, config paths resolve under this directory instead of the real
    /// user profile, so the merge logic can be exercised without touching real tool configs.</summary>
    public static string? HomeOverride;

    static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WormsCursor.exe");
    static string Home => HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ClaudePath => Path.Combine(Home, ".claude", "settings.json");
    public static string CodexPath => Path.Combine(Home, ".codex", "config.toml");

    // Codex is hidden for now (untested end-to-end). Its Register/Unregister/State plumbing stays
    // below, ready to re-list here once we can verify it. Only Claude Code is surfaced in the UI.
    public static IReadOnlyList<HookTool> Tools { get; } = new[]
    {
        new HookTool("claude-code", "Claude Code", ClaudePath, "~/.claude/settings.json"),
    };

    public static HookState GetState(string toolId) => toolId switch
    {
        "claude-code" => ClaudeState(),
        "codex" => CodexState(),
        _ => HookState.NotRegistered,
    };

    public static void Register(string toolId)
    {
        switch (toolId)
        {
            case "claude-code": RegisterClaude(); break;
            case "codex": RegisterCodex(); break;
        }
    }

    public static void Unregister(string toolId)
    {
        switch (toolId)
        {
            case "claude-code": UnregisterClaude(); break;
            case "codex": UnregisterCodex(); break;
        }
    }

    // ---------- Claude Code (JSON merge) ----------

    static string ClaudeMarker => "hook --tool claude-code";
    static string ClaudeCommand() => $"\"{Exe}\" hook --tool claude-code";

    static HookState ClaudeState()
    {
        var hooks = ReadJsonObject(ClaudePath, throwOnBad: false)?["hooks"] as JsonObject;
        if (hooks is null) return HookState.NotRegistered;
        // Count how many of the events we *now* register already carry our hook. None → not us;
        // all → fully registered; some → an older, partial registration that needs a refresh.
        int present = 0;
        foreach (var ev in ClaudeEvents)
            if (hooks[ev] is JsonArray arr && HasOurHook(arr)) present++;
        if (present == 0) return HookState.NotRegistered;
        return present == ClaudeEvents.Length ? HookState.Registered : HookState.Outdated;
    }

    static void RegisterClaude()
    {
        var root = ReadJsonObject(ClaudePath, throwOnBad: true) ?? new JsonObject();
        if (root["hooks"] is not JsonObject hooks) { hooks = new JsonObject(); root["hooks"] = hooks; }

        string cmd = ClaudeCommand();
        foreach (var ev in ClaudeEvents)
        {
            if (hooks[ev] is not JsonArray arr) { arr = new JsonArray(); hooks[ev] = arr; }
            RemoveOurHooks(arr); // de-dupe / refresh the exe path
            arr.Add(new JsonObject
            {
                ["matcher"] = ev == "Notification" ? "permission_prompt|idle_prompt" : "*",
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = cmd }),
            });
        }
        BackupAndWrite(ClaudePath, root.ToJsonString(Indented));
    }

    static void UnregisterClaude()
    {
        var root = ReadJsonObject(ClaudePath, throwOnBad: true);
        if (root?["hooks"] is not JsonObject hooks) return;

        var emptied = new List<string>();
        foreach (var ev in hooks)
            if (ev.Value is JsonArray arr) { RemoveOurHooks(arr); if (arr.Count == 0) emptied.Add(ev.Key); }
        foreach (var k in emptied) hooks.Remove(k);
        if (hooks.Count == 0) root.Remove("hooks");
        BackupAndWrite(ClaudePath, root.ToJsonString(Indented));
    }

    static bool HasOurHook(JsonArray entries)
    {
        foreach (var entry in entries)
            if (entry is JsonObject eo && eo["hooks"] is JsonArray inner)
                foreach (var h in inner)
                    if (h is JsonObject ho && (ho["command"]?.GetValue<string>() ?? "").Contains(ClaudeMarker))
                        return true;
        return false;
    }

    static void RemoveOurHooks(JsonArray entries)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
            if (entries[i] is JsonObject eo && eo["hooks"] is JsonArray inner)
            {
                bool ours = false;
                foreach (var h in inner)
                    if (h is JsonObject ho && (ho["command"]?.GetValue<string>() ?? "").Contains(ClaudeMarker)) { ours = true; break; }
                if (ours) entries.RemoveAt(i);
            }
    }

    // ---------- Codex (TOML `notify`) ----------

    // TOML literal (single-quoted) strings don't process escapes — ideal for a Windows path.
    static string CodexNotifyLine() => $"notify = ['{Exe}', 'hook', '--tool', 'codex']";
    static readonly Regex NotifyKey = new(@"^\s*notify\s*=", RegexOptions.Compiled);
    static bool IsOurNotify(string line) => line.Contains("--tool") && line.Contains("codex") && line.Contains("hook");

    static HookState CodexState()
    {
        if (!File.Exists(CodexPath)) return HookState.NotRegistered;
        foreach (var raw in File.ReadAllLines(CodexPath))
        {
            string line = raw.Trim();
            if (line.StartsWith('#')) continue;
            if (NotifyKey.IsMatch(line)) return IsOurNotify(line) ? HookState.Registered : HookState.ConfigConflict;
        }
        return HookState.NotRegistered;
    }

    static void RegisterCodex()
    {
        var lines = (File.Exists(CodexPath) ? File.ReadAllText(CodexPath) : "")
            .Replace("\r\n", "\n").Split('\n').ToList();

        int idx = lines.FindIndex(l => NotifyKey.IsMatch(l));
        if (idx >= 0 && !IsOurNotify(lines[idx]))
            throw new InvalidOperationException(
                "Codex already has a custom `notify` configured. Remove it from ~/.codex/config.toml first, then register.");

        if (idx >= 0) lines[idx] = CodexNotifyLine();   // refresh ours
        else lines.Insert(0, CodexNotifyLine());        // a root key must precede any [table]
        BackupAndWrite(CodexPath, string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine);
    }

    static void UnregisterCodex()
    {
        if (!File.Exists(CodexPath)) return;
        var lines = File.ReadAllText(CodexPath).Replace("\r\n", "\n").Split('\n').ToList();
        lines.RemoveAll(l => NotifyKey.IsMatch(l) && IsOurNotify(l));
        BackupAndWrite(CodexPath, string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine);
    }

    // ---------- shared file IO ----------

    static JsonObject? ReadJsonObject(string path, bool throwOnBad)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch (JsonException)
        {
            if (throwOnBad)
                throw new InvalidOperationException($"{Path.GetFileName(path)} isn't valid JSON — fix or remove it, then retry.");
            return null;
        }
    }

    static void BackupAndWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Copy(path, path + ".wormscursor.bak", overwrite: true);
        File.WriteAllText(path, content);
    }
}
