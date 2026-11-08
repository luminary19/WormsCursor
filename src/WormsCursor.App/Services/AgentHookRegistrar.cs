using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// <summary>The config is malformed and cannot be merged without discarding user data.</summary>
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

    // These lifecycle hooks are shared by Codex desktop and CLI. Keep them synchronous so a late
    // notification cannot arrive after the tool-resumed event that clears it.
    static readonly string[] CodexEvents =
        { "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "Stop", "Interrupt", "SessionEnd" };

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
    public static string CodexPath
    {
        get
        {
            string? configDir = HomeOverride is null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null;
            return Path.Combine(string.IsNullOrWhiteSpace(configDir) ? Path.Combine(Home, ".codex") : configDir, "hooks.json");
        }
    }

    public static IReadOnlyList<HookTool> Tools { get; } = new[]
    {
        new HookTool("claude-code", "Claude Code", "~/.claude/settings.json"),
        new HookTool("codex", "Codex (desktop + CLI)", "CODEX_HOME/hooks.json"),
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

    // ---------- Codex (hooks.json merge) ----------

    const string CodexMarker = "hook --tool codex";
    static string CodexCommand => $"\"{Exe}\" {CodexMarker}";

    static JsonObject CodexHandler() => new()
    {
        ["type"] = "command",
        ["command"] = CodexCommand,
        ["commandWindows"] = CodexCommand,
        ["timeout"] = 2,
    };

    static string? StringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool IsCodexHandler(JsonNode? node)
        => node is JsonObject hook
           && (StringValue(hook["command"])?.Contains(CodexMarker, StringComparison.Ordinal) == true
               || StringValue(hook["commandWindows"])?.Contains(CodexMarker, StringComparison.Ordinal) == true);

    static bool MatchesAll(JsonObject group)
        => group["matcher"] is null || StringValue(group["matcher"]) is "" or "*";

    // Validate before any edits so a malformed event/handler never gets replaced with a fresh array.
    static JsonObject? CodexHooks(JsonObject root)
    {
        if (!root.ContainsKey("hooks")) return null;
        if (root["hooks"] is not JsonObject hooks) throw new InvalidOperationException("Codex hooks.json: hooks must be an object.");
        foreach (var ev in hooks)
        {
            if (ev.Value is not JsonArray groups) throw new InvalidOperationException($"Codex hooks.json: {ev.Key} must be an array.");
            foreach (var group in groups)
                if (group is not JsonObject obj || obj["hooks"] is not JsonArray handlers || handlers.Any(h => h is not JsonObject))
                    throw new InvalidOperationException($"Codex hooks.json: {ev.Key} contains an invalid hook group.");
        }
        return hooks;
    }

    static HookState CodexState()
    {
        try
        {
            var root = ReadJsonObject(CodexPath, throwOnBad: true);
            var hooks = root is null ? null : CodexHooks(root);
            if (hooks is null) return HookState.NotRegistered;
            bool anyOurs = hooks.Any(ev => ((JsonArray)ev.Value!).Any(g => ((JsonArray)g!["hooks"]!).Any(IsCodexHandler)));
            if (!anyOurs) return HookState.NotRegistered;
            bool current = CodexEvents.All(ev => hooks[ev] is JsonArray groups
                && groups.Sum(g => ((JsonArray)g!["hooks"]!).Count(IsCodexHandler)) == 1
                && groups.Any(g => g is JsonObject group
                    && MatchesAll(group)
                    && ((JsonArray)group["hooks"]!).Any(h => JsonNode.DeepEquals(h, CodexHandler()))));
            return current ? HookState.Registered : HookState.Outdated;
        }
        catch (InvalidOperationException) { return HookState.ConfigConflict; }
    }

    static void RegisterCodex()
    {
        var root = ReadJsonObject(CodexPath, throwOnBad: true) ?? new JsonObject();
        var hooks = CodexHooks(root);
        if (hooks is null) { hooks = new JsonObject(); root["hooks"] = hooks; }
        foreach (string ev in CodexEvents)
        {
            if (hooks[ev] is not JsonArray groups) { groups = new JsonArray(); hooks[ev] = groups; }
            // Refresh in place, including a hook sharing a group with another tool. Only removing
            // stale duplicates can shift later indexes; Codex will review any changed trust keys.
            var group = groups.OfType<JsonObject>().FirstOrDefault(g => MatchesAll(g)
                && ((JsonArray)g["hooks"]!).Any(IsCodexHandler));
            var handler = CodexHandler();
            if (group is not null)
            {
                var handlers = (JsonArray)group["hooks"]!;
                int index = handlers.Select((h, i) => (h, i)).First(pair => IsCodexHandler(pair.h)).i;
                handlers[index] = handler;
            }
            else groups.Add(new JsonObject { ["hooks"] = new JsonArray(handler) });
            RemoveCodexHandlers(groups, handler);
        }
        BackupAndWrite(CodexPath, root.ToJsonString(Indented));
    }

    static void UnregisterCodex()
    {
        var root = ReadJsonObject(CodexPath, throwOnBad: true);
        var hooks = root is null ? null : CodexHooks(root);
        if (hooks is null) return;
        foreach (string ev in hooks.Select(ev => ev.Key).ToArray())
        {
            var groups = (JsonArray)hooks[ev]!;
            RemoveCodexHandlers(groups);
            if (groups.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) root!.Remove("hooks");
        BackupAndWrite(CodexPath, root!.ToJsonString(Indented));
    }

    static void RemoveCodexHandlers(JsonArray groups, JsonNode? keep = null)
    {
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            var handlers = (JsonArray)groups[i]!["hooks"]!;
            bool removed = false;
            for (int j = handlers.Count - 1; j >= 0; j--)
                if (!ReferenceEquals(handlers[j], keep) && IsCodexHandler(handlers[j]))
                { handlers.RemoveAt(j); removed = true; }
            if (removed && handlers.Count == 0) groups.RemoveAt(i);
        }
    }

    // ---------- shared file IO ----------

    static JsonObject? ReadJsonObject(string path, bool throwOnBad)
    {
        if (!File.Exists(path)) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("Expected an object."); }
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
