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
public sealed record HookTool(string Id, string DisplayName, string ConfigHint);

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
    // The events we register with Claude Code. Pre/PostToolUse ARE registered so the token clears the
    // instant the agent resumes work after you respond — approving a permission prompt fires no
    // UserPromptSubmit, so without a tool-use signal the token would otherwise linger until the TTL.
    // The cost is one hook process per tool call during an active turn (fail-silent, sub-½ s). The
    // other events: UserPromptSubmit + SessionStart clear when you come back with a prompt; Stop /
    // StopFailure / Notification raise it; SessionEnd drops the session on exit so it doesn't linger.
    static readonly string[] ClaudeEvents =
        { "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "StopFailure", "SessionStart", "SessionEnd" };

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

    // Claude Code reads its user settings from %CLAUDE_CONFIG_DIR%\settings.json when that env var is
    // set (the config dir *is* the .claude folder, so no nested ".claude"), and falls back to
    // ~/.claude/settings.json otherwise. Honour the override or our hooks land in a file Claude never
    // reads. The test seam (HomeOverride) always wins so merge tests stay hermetic.
    public static string ClaudePath
    {
        get
        {
            if (HomeOverride is null)
            {
                var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (!string.IsNullOrWhiteSpace(configDir))
                    return Path.Combine(configDir, "settings.json");
            }
            return Path.Combine(Home, ".claude", "settings.json");
        }
    }
    public static string CodexPath => Path.Combine(Home, ".codex", "config.toml");

    // Codex is hidden for now (untested end-to-end). Its Register/Unregister/State plumbing stays
    // below, ready to re-list here once we can verify it. Only Claude Code is surfaced in the UI.
    public static IReadOnlyList<HookTool> Tools { get; } = new[]
    {
        new HookTool("claude-code", "Claude Code", "~/.claude/settings.json"),
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
    static string ClaudeCommand(string? evt = null)
        => evt is null ? $"\"{Exe}\" hook --tool claude-code"
                       : $"\"{Exe}\" hook --tool claude-code --event {evt}";

    // Most events register one "*"-matcher hook the bridge normalises by name. Notification is special:
    // its sub-type says whether the agent is *blocked on your decision* (a persistent token) or just
    // *idle after a finished turn* (a timed nudge, like Stop). Claude filters Notification hooks by
    // notification_type via the matcher, so we register one entry per type carrying an explicit --event
    // the bridge uses verbatim — no dependence on whatever the stdin payload happens to include.
    //   permission_prompt / elicitation_dialog → awaiting_user (sticky: needs your input to continue)
    //   idle_prompt                            → turn_complete (timed: "still waiting", auto-clears)
    static readonly (string Matcher, string Event)[] ClaudeNotifications =
    {
        ("permission_prompt",  "awaiting_user"),
        ("elicitation_dialog", "awaiting_user"),
        ("idle_prompt",        "turn_complete"),
    };

    static JsonObject MakeEntry(string matcher, string command) => new()
    {
        ["matcher"] = matcher,
        ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
    };

    static HookState ClaudeState()
    {
        var hooks = ReadJsonObject(ClaudePath, throwOnBad: false)?["hooks"] as JsonObject;
        if (hooks is null) return HookState.NotRegistered;
        // None of our hooks → not us; every event present *and* matching today's shape → registered;
        // anything in between (an older or partial registration) → needs a refresh. Notification also
        // has to carry the per-type --event routing, so a pre-routing single-matcher entry reads as outdated.
        bool anyOurs = false, allCurrent = true;
        foreach (var ev in ClaudeEvents)
        {
            var arr = hooks[ev] as JsonArray;
            bool present = arr is not null && HasOurHook(arr);
            anyOurs |= present;
            bool current = ev == "Notification"
                ? arr is not null && ClaudeNotifications.All(n => HasCommandContaining(arr, "--event " + n.Event))
                : present;
            allCurrent &= current;
        }
        if (!anyOurs) return HookState.NotRegistered;
        return allCurrent ? HookState.Registered : HookState.Outdated;
    }

    static void RegisterClaude()
    {
        var root = ReadJsonObject(ClaudePath, throwOnBad: true) ?? new JsonObject();
        if (root["hooks"] is not JsonObject hooks) { hooks = new JsonObject(); root["hooks"] = hooks; }

        foreach (var ev in ClaudeEvents)
        {
            if (hooks[ev] is not JsonArray arr) { arr = new JsonArray(); hooks[ev] = arr; }
            RemoveOurHooks(arr); // de-dupe / refresh the exe path
            if (ev == "Notification")
                foreach (var (matcher, evt) in ClaudeNotifications)
                    arr.Add(MakeEntry(matcher, ClaudeCommand(evt)));
            else
                arr.Add(MakeEntry("*", ClaudeCommand()));
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

    // True when a hook-entry's inner command list carries a command containing <needle>.
    static bool EntryCommandContains(JsonNode? entry, string needle)
        => entry is JsonObject eo && eo["hooks"] is JsonArray inner
           && inner.Any(h => h is JsonObject ho && (ho["command"]?.GetValue<string>() ?? "").Contains(needle));

    static bool IsOurEntry(JsonNode? entry) => EntryCommandContains(entry, ClaudeMarker);
    static bool HasOurHook(JsonArray entries) => entries.Any(IsOurEntry);
    static bool HasCommandContaining(JsonArray entries, string needle) => entries.Any(e => EntryCommandContains(e, needle));

    static void RemoveOurHooks(JsonArray entries)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
            if (IsOurEntry(entries[i])) entries.RemoveAt(i);
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
