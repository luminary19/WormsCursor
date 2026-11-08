using System.Text.Json.Nodes;

namespace AgentsCursor.App.Services;

// Claude Code's settings.json and Codex's hooks.json: both carry AgentsCursor's `hook` bridge as
// lifecycle hook commands in the same shape, hooks: { <Event>: [ { matcher?, hooks: [ {...} ] } ] }.
public static partial class AgentHookRegistrar
{
    // Claude Code reads its user settings from %CLAUDE_CONFIG_DIR%\settings.json when that env var is
    // set (the config dir *is* the .claude folder, so no nested ".claude"), and falls back to
    // ~/.claude/settings.json otherwise. Honour the override or our hooks land in a file Claude never
    // reads. The test seam (HomeOverride) always wins so merge tests stay hermetic.
    public static string ClaudePath
        => ConfigDirFromEnvironment("CLAUDE_CONFIG_DIR") is { } dir
            ? Path.Combine(dir, "settings.json")
            : Path.Combine(Home, ".claude", "settings.json");

    public static string CodexPath
        => Path.Combine(ConfigDirFromEnvironment("CODEX_HOME") ?? Path.Combine(Home, ".codex"), "hooks.json");

    // The events we register with Claude Code, each with its matcher (null: the event takes none).
    // Pre/PostToolUse(+Failure) are registered so the token clears the instant an approved call runs —
    // approving a permission prompt fires no UserPromptSubmit — and PreToolUse of AskUserQuestion is a
    // question. PostToolBatch answers what is left once all of an agent's parallel calls have resolved
    // (a denied call has no Post event). The cost is one hook process per tool call during an active
    // turn (fail-silent, sub-½ s). The other events: UserPromptSubmit + SessionStart clear when you come
    // back with a prompt; Stop / StopFailure end the turn; PermissionRequest is an approval scoped to the
    // asking (sub)agent and its call; Elicitation / ElicitationResult open and answer an MCP server's
    // request for input; SessionEnd drops the session on exit. Notification is registered only for the
    // idle_prompt reminder: it carries no agent_id, so it is never used for a "blocked on you" state.
    static readonly (string Event, string? Matcher)[] ClaudeEvents =
    {
        ("UserPromptSubmit", "*"), ("PreToolUse", "*"), ("PostToolUse", "*"), ("PostToolUseFailure", "*"),
        ("PostToolBatch", null), ("PermissionRequest", "*"), ("Elicitation", "*"), ("ElicitationResult", "*"),
        ("Notification", "idle_prompt"), ("Stop", "*"), ("StopFailure", "*"), ("SessionStart", "*"), ("SessionEnd", "*"),
    };

    // These lifecycle hooks are shared by Codex desktop and CLI. Keep them synchronous so a late
    // notification cannot arrive after the tool-resumed event that clears it.
    static readonly string[] CodexEvents =
        { "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "Stop", "Interrupt", "SessionEnd" };

    // ---------- Claude Code (JSON merge) ----------

    const string ClaudeFile = "Claude settings.json";
    const string ClaudeMarker = "hook --tool claude-code";
    static readonly string[] ClaudeArgs = ClaudeMarker.Split(' ');

    // Exec form: Claude Code starts the exe directly with these arguments, no shell involved. A
    // shell-form command needs a shell that parses it, and on Windows without Git Bash that shell is
    // PowerShell, which rejects a quoted exe path followed by arguments — and a failing PreToolUse or
    // PermissionRequest hook there blocks every tool call.
    static JsonObject ClaudeHandler() => new()
    {
        ["type"] = "command",
        ["command"] = Exe,
        ["args"] = new JsonArray(ClaudeArgs.Select(a => (JsonNode)a).ToArray()),
    };

    static HookState ClaudeState()
    {
        try
        {
            var root = ReadJsonObject(ClaudePath);
            var hooks = root is null ? null : ValidatedHooks(root, ClaudeFile);
            if (hooks is null || !HasAnyHandler(hooks, IsClaudeHandler)) return HookState.NotRegistered;
            // Today's shape exactly: every event carries one handler of ours, in a group with the
            // expected matcher, with the current command. Anything else (a missing event, an old exe
            // path, the shell-form command, the retired per-type Notification entries) needs a refresh.
            bool current = ClaudeEvents.All(ev => hooks[ev.Event] is JsonArray groups
                && HasExactlyHandler(groups, IsClaudeHandler, g => StringValue(g["matcher"]) == ev.Matcher, ClaudeHandler()));
            return current ? HookState.Registered : HookState.Outdated;
        }
        catch (InvalidOperationException) { return HookState.ConfigConflict; }
    }

    static void RegisterClaude()
    {
        var root = ReadJsonObject(ClaudePath) ?? new JsonObject();
        var hooks = ValidatedHooks(root, ClaudeFile);
        if (hooks is null) { hooks = new JsonObject(); root["hooks"] = hooks; }

        var wanted = ClaudeEvents.Select(e => e.Event).ToHashSet(StringComparer.Ordinal);
        foreach (string ev in hooks.Select(ev => ev.Key).ToArray())
        {
            var groups = (JsonArray)hooks[ev]!;
            int before = groups.Count;
            RemoveHandlers(groups, IsClaudeHandler); // de-dupe / refresh the exe path and matchers
            if (before > 0 && groups.Count == 0 && !wanted.Contains(ev)) hooks.Remove(ev); // an event we no longer use
        }
        foreach (var (ev, matcher) in ClaudeEvents)
        {
            if (hooks[ev] is not JsonArray groups) { groups = new JsonArray(); hooks[ev] = groups; }
            var group = new JsonObject();
            if (matcher is not null) group["matcher"] = matcher;
            group["hooks"] = new JsonArray(ClaudeHandler());
            groups.Add(group);
        }
        BackupAndWrite(ClaudePath, root.ToJsonString(Indented));
    }

    // Ours in either form: today's exec form (our arguments in args), or the shell-form command
    // earlier versions wrote (the marker inside the command string), so a refresh replaces it.
    static bool IsClaudeHandler(JsonNode? node)
        => node is JsonObject hook
           && (StringValue(hook["command"])?.Contains(ClaudeMarker, StringComparison.Ordinal) == true
               || (hook["args"] is JsonArray args && args.Select(StringValue).SequenceEqual(ClaudeArgs)));

    // ---------- Codex (hooks.json merge) ----------

    const string CodexFile = "Codex hooks.json";
    const string CodexMarker = "hook --tool codex";

    // Codex runs a hook's command through the session's own shell, the one it runs its tool commands
    // in (codex-rs core/src/session/mod.rs builds the hook shell from the turn environment's shell):
    //   - on Windows it uses `commandWindows` when present, and the shell is PowerShell
    //     (`pwsh.exe`, else `powershell.exe`, as `<shell> -NoProfile -Command <commandWindows>`);
    //   - elsewhere it runs `command` with `sh -c` (or the user's POSIX shell).
    // PowerShell parses a leading quoted path as a string expression, so `"C:\...\AgentsCursor.exe" hook`
    // is a parse error; the exe must go through the call operator. A single-quoted literal keeps any
    // `$` or backtick in the path from expanding; a `'` inside it is escaped by doubling.
    internal static string CodexWindowsCommand(string exe) => $"& '{exe.Replace("'", "''")}' {CodexMarker}";
    static string CodexPosixCommand(string exe) => $"\"{exe}\" {CodexMarker}";

    static JsonObject CodexHandler() => new()
    {
        ["type"] = "command",
        ["command"] = CodexPosixCommand(Exe),
        ["commandWindows"] = CodexWindowsCommand(Exe),
        ["timeout"] = 2,
    };

    static bool IsCodexHandler(JsonNode? node)
        => node is JsonObject hook
           && (StringValue(hook["command"])?.Contains(CodexMarker, StringComparison.Ordinal) == true
               || StringValue(hook["commandWindows"])?.Contains(CodexMarker, StringComparison.Ordinal) == true);

    static bool MatchesAll(JsonObject group)
        => group["matcher"] is null || StringValue(group["matcher"]) is "" or "*";

    static HookState CodexState()
    {
        try
        {
            var root = ReadJsonObject(CodexPath);
            var hooks = root is null ? null : ValidatedHooks(root, CodexFile);
            if (hooks is null || !HasAnyHandler(hooks, IsCodexHandler)) return HookState.NotRegistered;
            // A handler that differs in any field (e.g. the old quoted-path commandWindows PowerShell
            // rejects) reads as outdated, so Register rewrites it in place.
            bool current = CodexEvents.All(ev => hooks[ev] is JsonArray groups
                && HasExactlyHandler(groups, IsCodexHandler, MatchesAll, CodexHandler()));
            return current ? HookState.Registered : HookState.Outdated;
        }
        catch (InvalidOperationException) { return HookState.ConfigConflict; }
    }

    static void RegisterCodex()
    {
        var root = ReadJsonObject(CodexPath) ?? new JsonObject();
        var hooks = ValidatedHooks(root, CodexFile);
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
            RemoveHandlers(groups, IsCodexHandler, keep: handler);
        }
        BackupAndWrite(CodexPath, root.ToJsonString(Indented));
    }

    // ---------- shared hooks shape (Claude settings.json and Codex hooks.json) ----------

    // Both tools use the same shape: hooks: { <Event>: [ { matcher?, hooks: [ { command, … } ] } ] }.
    // Validate before any edits so a malformed event/handler never gets replaced with a fresh array —
    // the caller maps the InvalidOperationException to HookState.ConfigConflict ("Manual setup needed").
    static JsonObject? ValidatedHooks(JsonObject root, string file)
    {
        if (!root.ContainsKey("hooks")) return null;
        if (root["hooks"] is not JsonObject hooks) throw new InvalidOperationException($"{file}: hooks must be an object.");
        foreach (var ev in hooks)
        {
            if (ev.Value is not JsonArray groups) throw new InvalidOperationException($"{file}: {ev.Key} must be an array.");
            foreach (var group in groups)
                if (group is not JsonObject obj || obj["hooks"] is not JsonArray handlers || handlers.Any(h => h is not JsonObject))
                    throw new InvalidOperationException($"{file}: {ev.Key} contains an invalid hook group.");
        }
        return hooks;
    }

    static bool HasAnyHandler(JsonObject hooks, Func<JsonNode?, bool> isOurs)
        => hooks.Any(ev => ((JsonArray)ev.Value!).Any(g => ((JsonArray)g!["hooks"]!).Any(isOurs)));

    // True when the event's groups hold exactly one handler of ours, equal to <expected>, in a group
    // that passes <groupOk>.
    static bool HasExactlyHandler(JsonArray groups, Func<JsonNode?, bool> isOurs, Func<JsonObject, bool> groupOk, JsonObject expected)
        => groups.Sum(g => ((JsonArray)g!["hooks"]!).Count(isOurs)) == 1
           && groups.OfType<JsonObject>().Any(g => groupOk(g) && ((JsonArray)g["hooks"]!).Any(h => JsonNode.DeepEquals(h, expected)));

    // Removes only our handlers from each (validated) group, dropping a group only when that leaves
    // it empty — a user's own command sharing a group with ours survives. <keep> is spared.
    static void RemoveHandlers(JsonArray groups, Func<JsonNode?, bool> isOurs, JsonNode? keep = null)
    {
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            var handlers = (JsonArray)groups[i]!["hooks"]!;
            bool removed = false;
            for (int j = handlers.Count - 1; j >= 0; j--)
                if (!ReferenceEquals(handlers[j], keep) && isOurs(handlers[j]))
                { handlers.RemoveAt(j); removed = true; }
            if (removed && handlers.Count == 0) groups.RemoveAt(i);
        }
    }

    static void UnregisterFrom(string path, string file, Func<JsonNode?, bool> isOurs)
    {
        var root = ReadJsonObject(path);
        var hooks = root is null ? null : ValidatedHooks(root, file);
        if (hooks is null) return;
        foreach (string ev in hooks.Select(ev => ev.Key).ToArray())
        {
            var groups = (JsonArray)hooks[ev]!;
            RemoveHandlers(groups, isOurs);
            if (groups.Count == 0) hooks.Remove(ev);
        }
        if (hooks.Count == 0) root!.Remove("hooks");
        BackupAndWrite(path, root!.ToJsonString(Indented));
    }
}
