using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using WormsCursor.App;
using WormsCursor.App.Services;
using WormsCursor.Core;
using OpenCodeCursorOverlay;

var tests = new (string Name, Action Run)[]
{
    ("Codex lifecycle payloads", Lifecycle),
    ("Sweeping during a hook refreshes the waiting logos", SweepDuringHook),
    ("Async questions persist until their individual replies", AsyncQuestions),
    ("Async questions and approvals clear independently", OverlappingAttention),
    ("Claude behavior remains unchanged", Claude),
    ("Codex registration merges, refreshes, and unregisters", Registration),
    ("Malformed configs are preserved", MalformedConfigs),
    ("CODEX_HOME is respected", CodexHome),
    ("A sibling subagent's tool use keeps another agent's approval", SubagentApprovals),
    ("Claude registration keeps user commands and the original backup", ClaudeRegistration),
    ("Unknown tool ids are rejected", UnknownTool),
    ("Renderers on two threads draw concurrently without sharing GDI+ objects", ConcurrentRendering),
    ("Applying Preferences keeps the agent-notifier settings", PreferencesKeepAgentSettings),
    ("Corrupt and unwritable settings files are never destroyed", SettingsPersistence),
    ("Autostart only counts a Run entry that launches this exe", AutostartTarget),
    ("Suppressed program names normalize and match case-insensitively", SuppressionMatching),
    ("The suppressed-program list round-trips through settings.json", SuppressionSettingsJson),
    ("The OpenCode helper follows settings saves and survives a locked read", SuppressionListReload),
    ("Settings copies never share a mutable program list", SuppressionCopies),
    ("The foreground watcher and window listing resolve real exe names", ForegroundSmoke),
};
foreach (var (name, run) in tests)
{
    run();
    Console.WriteLine("PASS " + name);
}
Console.WriteLine($"Passed {tests.Length} regression groups.");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static AgentEventMessage Parse(object payload, string tool = "codex", string? ev = null)
    => AgentHookBridge.Parse(tool, JsonSerializer.Serialize(payload), ev)
        ?? throw new InvalidOperationException("Expected a normalized event.");

static void Apply(AgentActivity activity, AgentEventMessage message, DateTime now)
{
    Equal(true, message.TryGetKind(out var kind));
    activity.Report(message.Tool, message.SessionId, message.Project, kind, now, message.InputIds, message.AgentId);
}

static void Lifecycle()
{
    var now = DateTime.UtcNow;
    var activity = new AgentActivity(TimeSpan.FromSeconds(10));
    AgentEventMessage Event(string name, string? tool = null) => Parse(new
    {
        hook_event_name = name, session_id = "root", cwd = "C:\\project", tool_name = tool,
    });
    Apply(activity, Event("Stop"), now);
    Equal(1, activity.WaitingCount);
    Equal("codex", activity.WaitingTools[0]);
    activity.Sweep(now.AddSeconds(11));
    Equal(0, activity.WaitingCount);
    Apply(activity, Event("PermissionRequest"), now);
    activity.Sweep(now.AddHours(1));
    Equal(1, activity.WaitingCount);
    Apply(activity, Event("PostToolUse", "Bash"), now.AddHours(1));
    Equal(0, activity.WaitingCount);
    foreach (string tool in new[] { "request_user_input", "request_permissions" })
    {
        Apply(activity, Event("PreToolUse", tool), now);
        Equal(1, activity.WaitingCount);
        Apply(activity, Event("PostToolUse", tool), now);
        Equal(0, activity.WaitingCount);
    }
    Apply(activity, Event("PermissionRequest"), now);
    Apply(activity, Event("Interrupt"), now);
    Equal(0, activity.WaitingCount);
    Apply(activity, Event("Stop"), now);
    Apply(activity, Event("SessionStart"), now);
    Equal(0, activity.WaitingCount);
    Apply(activity, Event("Stop"), now);
    Apply(activity, Event("SessionEnd"), now);
    Equal(0, activity.WaitingCount);
    Equal<AgentEventMessage?>(null, AgentHookBridge.Parse("codex", "{\"hook_event_name\":\"SubagentStop\"}"));
    Equal<AgentEventMessage?>(null, AgentHookBridge.Parse("codex", "{\"type\":\"agent-turn-complete\"}"));
    Equal<AgentEventMessage?>(null, AgentHookBridge.Parse("codex", "malformed"));
}

static void SweepDuringHook()
{
    var now = DateTime.UtcNow;
    var activity = new AgentActivity(TimeSpan.FromSeconds(10));
    var updates = new List<int>();
    activity.WaitingChanged += updates.Add;
    activity.Report("codex", "root", null, AgentEventKind.TurnComplete, now);
    activity.Report("codex", "root", null, AgentEventKind.ToolUse, now.AddSeconds(11));
    Equal("1,0", string.Join(',', updates));
    activity.Report("claude-code", "claude", null, AgentEventKind.TurnComplete, now.AddSeconds(12));
    activity.Report("codex", "root", null, AgentEventKind.TurnComplete, now.AddSeconds(23));
    Equal("1,0,1,1", string.Join(',', updates));
    Equal("codex", activity.WaitingTools.Single());
}

static void AsyncQuestions()
{
    var now = DateTime.UtcNow;
    var activity = new AgentActivity(TimeSpan.FromSeconds(10));
    var asked = Parse(new
    {
        hook_event_name = "PostToolUse", session_id = "root", tool_name = "request_user_input_async",
        tool_use_id = "call-123", tool_input = new { questions = new[] { new { title = "One?" }, new { title = "Two?" } } },
        tool_response = "{\"accepted\":true}",
    });
    Equal("input_requested", asked.Event);
    Equal(2, asked.InputIds!.Length);
    Apply(activity, asked, now);
    Apply(activity, AgentEventMessage.FromJson(asked.ToJsonLine())!, now); // replay must be idempotent
    Equal(1, activity.WaitingCount);
    foreach (string name in new[] { "PreToolUse", "PostToolUse", "Stop", "SessionStart" })
        Apply(activity, Parse(new { hook_event_name = name, session_id = "root", tool_name = "Bash" }), now);
    activity.Sweep(now.AddDays(1));
    Equal(1, activity.WaitingCount);

    AgentEventMessage Reply(int index) => Parse(new
    {
        hook_event_name = "UserPromptSubmit", session_id = "root",
        prompt = "<send_user_message_question_reply>" + JsonSerializer.Serialize(new
        {
            questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", "call-123", index }),
            question = "Question", answer = "Yes",
        }) + "</send_user_message_question_reply>",
    });
    Apply(activity, Reply(0), now);
    Equal(1, activity.WaitingCount);
    Apply(activity, Reply(0), now);
    Equal(1, activity.WaitingCount);
    Apply(activity, Reply(1), now);
    Equal(0, activity.WaitingCount);
    Apply(activity, asked, now);
    var batch = "<send_user_message_question_reply>" + JsonSerializer.Serialize(new[] { new
    {
        questionItemId = asked.InputIds[0], question = "One?", answer = "Yes",
    } }) + "</send_user_message_question_reply>";
    Apply(activity, Parse(new { hook_event_name = "UserPromptSubmit", session_id = "root", prompt = batch }), now);
    Equal(1, activity.WaitingCount); // answering a batch subset must preserve the unanswered question
    Apply(activity, Reply(1), now);
    Equal(0, activity.WaitingCount);
    Apply(activity, asked, now);
    string ideBatch = "# Context from my IDE setup:\nfile.cs\n## My request for Codex:\n" + batch;
    Apply(activity, Parse(new { hook_event_name = "UserPromptSubmit", session_id = "root", prompt = ideBatch }), now);
    Equal(1, activity.WaitingCount);
    Apply(activity, Reply(1), now);
    Equal(0, activity.WaitingCount);
    var quoted = Parse(new { hook_event_name = "UserPromptSubmit", session_id = "root", prompt = "Explain this example: " + batch });
    Equal("thinking_started", quoted.Event);
    Apply(activity, asked, now);
    Apply(activity, Parse(new { hook_event_name = "UserPromptSubmit", session_id = "root", prompt = "Here is my answer." }), now);
    Equal(0, activity.WaitingCount);
    Apply(activity, asked, now);
    Apply(activity, Parse(new { hook_event_name = "SessionEnd", session_id = "root" }), now);
    Equal(0, activity.WaitingCount);
    var rejected = Parse(new
    {
        hook_event_name = "PostToolUse", session_id = "root", tool_name = "request_user_input_async",
        tool_use_id = "call-123", tool_input = new { questions = new[] { new { title = "One?" } } },
        tool_response = "{\"accepted\":false}",
    });
    Equal("tool_use", rejected.Event);
}

static void Claude()
{
    Equal("turn_complete", Parse(new { hook_event_name = "Stop" }, "claude-code").Event);
    Equal("awaiting_user", Parse(new { hook_event_name = "Notification" }, "claude-code", "awaiting_user").Event);
    Equal("turn_complete", Parse(new { hook_event_name = "Notification" }, "claude-code", "turn_complete").Event);
    Equal("tool_use", Parse(new { hook_event_name = "PostToolUse" }, "claude-code").Event);
    Equal<AgentEventMessage?>(null, AgentHookBridge.Parse("claude-code", "{\"hook_event_name\":\"Stop\",\"background_tasks\":[{\"type\":\"subagent\"}]}"));
}

static void OverlappingAttention()
{
    var now = DateTime.UtcNow;
    string[] question = { "question-1" };
    foreach (bool approvalFirst in new[] { false, true })
    {
        var activity = new AgentActivity(TimeSpan.FromSeconds(10));
        if (approvalFirst) activity.Report("codex", "root", null, AgentEventKind.AwaitingUser, now);
        activity.Report("codex", "root", null, AgentEventKind.InputRequested, now, question);
        if (!approvalFirst) activity.Report("codex", "root", null, AgentEventKind.AwaitingUser, now);
        activity.Report("codex", "root", null, AgentEventKind.InputAnswered, now.AddSeconds(1), question);
        Equal(1, activity.WaitingCount); // the approval is still pending after the last question reply
        activity.Sweep(now.AddHours(1));
        Equal(1, activity.WaitingCount); // the remaining approval must retain sticky semantics
        activity.Report("codex", "root", null, AgentEventKind.ToolUse, now.AddHours(1));
        Equal(0, activity.WaitingCount);
    }

    var approvalAnswered = new AgentActivity(TimeSpan.FromSeconds(10));
    approvalAnswered.Report("codex", "root", null, AgentEventKind.InputRequested, now, question);
    approvalAnswered.Report("codex", "root", null, AgentEventKind.AwaitingUser, now);
    approvalAnswered.Report("codex", "root", null, AgentEventKind.ToolUse, now.AddSeconds(1));
    approvalAnswered.Sweep(now.AddHours(1));
    Equal(1, approvalAnswered.WaitingCount); // resuming the approved tool does not answer the question
    approvalAnswered.Report("codex", "root", null, AgentEventKind.InputAnswered, now.AddHours(1), question);
    Equal(0, approvalAnswered.WaitingCount);

    var completed = new AgentActivity(TimeSpan.FromSeconds(10));
    completed.Report("codex", "root", null, AgentEventKind.InputRequested, now, question);
    completed.Report("codex", "root", null, AgentEventKind.TurnComplete, now);
    completed.Report("codex", "root", null, AgentEventKind.InputAnswered, now.AddSeconds(1), question);
    Equal(0, completed.WaitingCount); // a reply does acknowledge an ordinary finished-turn notice
}

static void WithHome(Action<string> test)
{
    string home = Path.Combine(Path.GetTempPath(), "WormsCursor.HookTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(home);
    AgentHookRegistrar.HomeOverride = home;
    try { test(home); }
    finally
    {
        AgentHookRegistrar.HomeOverride = null;
        Directory.Delete(home, recursive: true);
    }
}

static void Registration() => WithHome(home =>
{
    string path = AgentHookRegistrar.CodexPath;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string original = "{\"custom\":true,\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"echo other\",\"timeout\":7}]}]}}";
    File.WriteAllText(path, original);
    string toml = Path.Combine(Path.GetDirectoryName(path)!, "config.toml");
    File.WriteAllText(toml, "notify = ['another-program']\n");
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("codex"));
    AgentHookRegistrar.Register("codex");
    Equal(original, File.ReadAllText(path + ".wormscursor.bak"));
    Equal(HookState.Registered, AgentHookRegistrar.GetState("codex"));
    string first = File.ReadAllText(path);
    AgentHookRegistrar.Register("codex");
    Equal(first, File.ReadAllText(path));
    Equal("notify = ['another-program']\n", File.ReadAllText(toml));
    var root = JsonNode.Parse(first)!;
    Equal(8, root["hooks"]!.AsObject().Count);
    Equal("echo other", root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    root["hooks"]!.AsObject().Remove("Interrupt");
    // A manually combined group and a duplicate old executable must both refresh cleanly.
    root["hooks"]!["Stop"]![0]!["hooks"]!.AsArray().Add(new JsonObject
    {
        ["type"] = "command", ["command"] = "old.exe hook --tool codex",
    });
    File.WriteAllText(path, root.ToJsonString());
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("codex"));
    AgentHookRegistrar.Register("codex");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("codex"));
    var refreshed = JsonNode.Parse(File.ReadAllText(path))!;
    Equal(1, refreshed["hooks"]!["Stop"]!.AsArray().Count);
    Equal(2, refreshed["hooks"]!["Stop"]![0]!["hooks"]!.AsArray().Count);
    AgentHookRegistrar.Unregister("codex");
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("codex"));
    Equal(true, JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(File.ReadAllText(path))));
});

static void MalformedConfigs() => WithHome(home =>
{
    foreach (var (tool, path) in new[] { ("codex", AgentHookRegistrar.CodexPath), ("claude-code", AgentHookRegistrar.ClaudePath) })
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (string raw in new[] { "[1]", "null", "{", "{\"hooks\":[]}", "{\"hooks\":{\"Stop\":{}}}", "{\"hooks\":{\"Stop\":[{\"matcher\":\"*\"}]}}" })
        {
            File.WriteAllText(path, raw);
            Equal(HookState.ConfigConflict, AgentHookRegistrar.GetState(tool));
            foreach (Action action in new Action[] { () => AgentHookRegistrar.Register(tool), () => AgentHookRegistrar.Unregister(tool) })
            {
                bool rejected = false;
                try { action(); }
                catch (InvalidOperationException) { rejected = true; }
                Equal(true, rejected);
                Equal(raw, File.ReadAllText(path));
            }
        }
        // A non-string command is not ours, and must not make the state check throw.
        File.WriteAllText(path, "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":5}]}]}}");
        Equal(HookState.NotRegistered, AgentHookRegistrar.GetState(tool));
    }
});

static void CodexHome()
{
    string? previous = Environment.GetEnvironmentVariable("CODEX_HOME");
    try
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", "C:\\custom-codex");
        Equal("C:\\custom-codex\\hooks.json", AgentHookRegistrar.CodexPath);
        WithHome(home => Equal(Path.Combine(home, ".codex", "hooks.json"), AgentHookRegistrar.CodexPath));
    }
    finally { Environment.SetEnvironmentVariable("CODEX_HOME", previous); }
}

static void SubagentApprovals()
{
    var now = DateTime.UtcNow;
    foreach (string tool in new[] { "claude-code", "codex" })
    {
        var activity = new AgentActivity(TimeSpan.FromSeconds(10));
        AgentEventMessage Event(string name, string? agent) => Parse(new
        {
            hook_event_name = name, session_id = "parent", tool_name = "Bash", agent_id = agent,
        }, tool);
        var asked = Event("PermissionRequest", "agent-a");
        Equal("awaiting_user", asked.Event);
        Equal("agent-a", asked.AgentId);
        Apply(activity, asked, now);
        Apply(activity, Event("PostToolUse", "agent-b"), now); // a sibling keeps working
        Equal(1, activity.WaitingCount);
        Apply(activity, Event("PostToolUse", null), now);      // so does the main agent
        Equal(1, activity.WaitingCount);
        activity.Sweep(now.AddHours(1));
        Equal(1, activity.WaitingCount);                       // still blocked on you: never swept
        Apply(activity, Event("PostToolUse", "agent-a"), now.AddHours(1)); // agent A was approved
        Equal(0, activity.WaitingCount);

        // Two agents blocked at once: the token stays until both are answered.
        Apply(activity, Event("PermissionRequest", null), now);
        Apply(activity, Event("PermissionRequest", "agent-b"), now);
        Apply(activity, Event("PreToolUse", null), now);
        Equal(1, activity.WaitingCount);
        Apply(activity, Event("PreToolUse", "agent-b"), now);
        Equal(0, activity.WaitingCount);

        // A finished turn means nothing is still blocked: the notice replaces the approvals and expires.
        Apply(activity, Event("PermissionRequest", "agent-a"), now);
        Apply(activity, Event("Stop", null), now);
        Equal(1, activity.WaitingCount);
        activity.Sweep(now.AddSeconds(11));
        Equal(0, activity.WaitingCount);
    }
}

static void ClaudeRegistration() => WithHome(home =>
{
    string path = AgentHookRegistrar.ClaudePath;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "{\"model\":\"opus\"}");
    string original = File.ReadAllText(path);
    AgentHookRegistrar.Register("claude-code");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("claude-code"));
    var root = JsonNode.Parse(File.ReadAllText(path))!;
    Equal(true, root["hooks"]!["PermissionRequest"] is JsonArray);
    // The approval prompt comes from PermissionRequest (it carries agent_id), not Notification.
    Equal(false, File.ReadAllText(path).Contains("permission_prompt"));

    // A user's own command hand-placed in our matcher group survives a refresh and an unregister.
    root["hooks"]!["Stop"]![0]!["hooks"]!.AsArray().Add(new JsonObject
    {
        ["type"] = "command", ["command"] = "my-notify.ps1",
    });
    File.WriteAllText(path, root.ToJsonString());
    AgentHookRegistrar.Register("claude-code");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("claude-code"));
    Equal(true, File.ReadAllText(path).Contains("my-notify.ps1"));
    AgentHookRegistrar.Unregister("claude-code");
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("claude-code"));
    var after = JsonNode.Parse(File.ReadAllText(path))!;
    Equal("my-notify.ps1", after["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    Equal(1, after["hooks"]!.AsObject().Count);
    // Several writes later, the backup is still the user's original, pre-WormsCursor file.
    Equal(original, File.ReadAllText(path + ".wormscursor.bak"));

    // A registration from before PermissionRequest was added reads as needing a refresh.
    AgentHookRegistrar.Register("claude-code");
    var old = JsonNode.Parse(File.ReadAllText(path))!;
    old["hooks"]!.AsObject().Remove("PermissionRequest");
    File.WriteAllText(path, old.ToJsonString());
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("claude-code"));
});

static void UnknownTool() => WithHome(home =>
{
    foreach (Action action in new Action[] { () => AgentHookRegistrar.Register("cluade"), () => AgentHookRegistrar.Unregister("cluade") })
    {
        bool rejected = false;
        try { action(); }
        catch (ArgumentException) { rejected = true; }
        Equal(true, rejected);
    }
});

// The overlay's animation thread and the Preferences preview draw the same logo at the same size.
// Each owns its renderer, so no GDI+ object is shared ("Object is currently in use elsewhere").
static void ConcurrentRendering()
{
    var settings = new CursorSettings();
    var tools = new[] { "claude-code", "codex" };
    int failures = 0, frames = 0;
    var until = DateTime.UtcNow.AddSeconds(1.5);
    var threads = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
    {
        using var renderer = new NotifierRenderer();
        using var canvas = new Bitmap(256, 256);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var g = Graphics.FromImage(canvas);
                renderer.DrawToken(g, settings, tools, 128f, 128f, 96f, 0f);
                Interlocked.Increment(ref frames);
            }
            catch (InvalidOperationException) { Interlocked.Increment(ref failures); }
        }
    })).ToArray();
    foreach (var t in threads) t.Start();
    foreach (var t in threads) t.Join();
    Equal(0, failures);
    Equal(true, frames > 100);
}

static void PreferencesKeepAgentSettings()
{
    var live = new CursorSettings();
    var working = live.Clone(); // Preferences opens on a snapshot…
    live.AgentNotifierEnabled = false;       // …then Agent settings… changes the notifier
    live.AgentNotifierTimeoutSeconds = 120;
    working.Size = 150;
    working.Placement = NotifierPlacement.Corner;
    live.CopyPreferencesFrom(working);        // OK / Apply in Preferences
    Equal(150, live.Size);
    Equal(NotifierPlacement.Corner, live.Placement);
    Equal(false, live.AgentNotifierEnabled);
    Equal(120, live.AgentNotifierTimeoutSeconds);
}

static void SettingsPersistence() => WithHome(home =>
{
    string path = Path.Combine(home, "settings.json");
    // Hand-edit slips the reader forgives.
    File.WriteAllText(path, "{\n  // bigger token\n  \"Size\": 120,\n}");
    Equal(120, SettingsStore.Load(path).Size);

    // An unparseable file is moved aside, so saving the defaults can't destroy it.
    string corrupt = "{\"Size\": 120, \"Corner\": \"Bottom\"}";
    File.WriteAllText(path, corrupt);
    var loaded = SettingsStore.Load(path);
    Equal(96, loaded.Size);
    string[] kept = Directory.GetFiles(home, "settings.json.corrupt-*");
    Equal(1, kept.Length);
    Equal(corrupt, File.ReadAllText(kept[0]));
    SettingsStore.Save(loaded, path);
    Equal(corrupt, File.ReadAllText(kept[0]));

    // A read-only target throws (the app decides how to handle it) and leaves no orphaned tmp.
    File.SetAttributes(path, FileAttributes.ReadOnly);
    try
    {
        bool threw = false;
        try { SettingsStore.Save(loaded, path); }
        catch (UnauthorizedAccessException) { threw = true; }
        Equal(true, threw);
        Equal(false, File.Exists(path + ".tmp"));
    }
    finally { File.SetAttributes(path, FileAttributes.Normal); }
});

static void AutostartTarget()
{
    const string exe = "C:\\Users\\me\\AppData\\Local\\WormsCursor\\current\\WormsCursor.exe";
    Equal(true, RegistryAutostart.LaunchesExe("\"" + exe + "\"", exe));
    Equal(true, RegistryAutostart.LaunchesExe(exe.ToUpperInvariant(), exe));
    Equal(false, RegistryAutostart.LaunchesExe("\"C:\\src\\WormsCursor\\bin\\Release\\WormsCursor.exe\"", exe));
    Equal(false, RegistryAutostart.LaunchesExe("\"\"", exe));
    Equal(false, RegistryAutostart.LaunchesExe("\"" + exe + "\"", null));
}

static string Joined(IEnumerable<string> names) => string.Join('|', names);

static void SuppressionMatching()
{
    Equal("dota2.exe", ProgramSuppression.NormalizeName(
        @"  C:\Program Files (x86)\Steam\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe "));
    Equal("Dota2.exe", ProgramSuppression.NormalizeName("Dota2"));
    Equal("Code.EXE", ProgramSuppression.NormalizeName("\"Code.EXE\""));
    Equal("game.exe", ProgramSuppression.NormalizeName("D:/Games/game.exe"));
    Equal<string?>(null, ProgramSuppression.NormalizeName(null));
    Equal<string?>(null, ProgramSuppression.NormalizeName("   "));
    Equal<string?>(null, ProgramSuppression.NormalizeName(@"C:\Games\"));
    Equal<string?>(null, ProgramSuppression.NormalizeName("bad*name"));

    var list = ProgramSuppression.Normalize(new[] { "dota2.exe", "DOTA2", " ", null, @"C:\x\Game.exe", "game" });
    Equal("dota2.exe|Game.exe", Joined(list));
    Equal(true, ProgramSuppression.Matches("DOTA2.EXE", list));
    Equal(true, ProgramSuppression.Matches(@"C:\Steam\dota 2 beta\dota2.exe", list));
    Equal(false, ProgramSuppression.Matches("dota.exe", list));
    Equal(false, ProgramSuppression.Matches(null, list));
    Equal(false, ProgramSuppression.Matches("dota2.exe", Array.Empty<string>()));
}

static void SuppressionSettingsJson() => WithHome(home =>
{
    string path = Path.Combine(home, "settings.json");
    Equal("dota2.exe", Joined(new CursorSettings().SuppressedPrograms));

    // A settings file from before the feature gets the default list.
    File.WriteAllText(path, "{\"Size\": 120}");
    Equal("dota2.exe", Joined(SettingsStore.Load(path).SuppressedPrograms));

    // A user who removed Dota keeps an empty list; it must not come back as the default.
    var settings = new CursorSettings { SuppressedPrograms = Array.Empty<string>() };
    SettingsStore.Save(settings, path);
    Equal(0, SettingsStore.Load(path).SuppressedPrograms.Count);
    Equal(0, SettingsStore.TryRead(path)!.SuppressedPrograms.Count);

    settings.SuppressedPrograms = new[] { "dota2.exe", "cs2.exe" };
    SettingsStore.Save(settings, path);
    Equal("dota2.exe|cs2.exe", Joined(SettingsStore.Load(path).SuppressedPrograms));

    // Hand edits load sanely: null means none; paths, missing ".exe" and duplicates normalize.
    File.WriteAllText(path, "{\"SuppressedPrograms\": null}");
    Equal(0, SettingsStore.Load(path).SuppressedPrograms.Count);
    File.WriteAllText(path, """{"SuppressedPrograms": ["C:\\Games\\Dota2", "dota2.exe", ""]}""");
    Equal("Dota2.exe", Joined(SettingsStore.Load(path).SuppressedPrograms));

    // The read-only path (the OpenCode helper) reports a corrupt file as unreadable, not as the
    // defaults, and never moves it; a missing file does mean the defaults.
    File.WriteAllText(path, "{ not json");
    Equal<CursorSettings?>(null, SettingsStore.TryRead(path));
    Equal(true, File.Exists(path));
    Equal(0, Directory.GetFiles(home, "settings.json.corrupt-*").Length);
    File.Delete(path);
    Equal("dota2.exe", Joined(SettingsStore.TryRead(path)!.SuppressedPrograms));
});

// The OpenCode helper's view of the list: a save is announced whatever has focus, and a read that
// fails (the file locked mid-save) keeps the last good list rather than reverting to the defaults.
static void SuppressionListReload() => WithHome(home =>
{
    string path = Path.Combine(home, "WormsCursor", "settings.json");
    using var list = new SuppressionListFile(path);
    using var saved = new ManualResetEventSlim(false);
    list.Changed += saved.Set;
    Equal("dota2.exe", Joined(list.Current())); // nothing saved yet

    SettingsStore.Save(new CursorSettings { SuppressedPrograms = Array.Empty<string>() }, path);
    Equal(true, saved.Wait(TimeSpan.FromSeconds(5)));
    Equal(0, list.Current().Count);

    saved.Reset();
    SettingsStore.Save(new CursorSettings { SuppressedPrograms = new[] { "cs2.exe" } }, path); // a replace
    Equal(true, saved.Wait(TimeSpan.FromSeconds(5)));
    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        Equal(0, list.Current().Count); // locked: the last good list, not ["dota2.exe"]
    Equal("cs2.exe", Joined(list.Current())); // retried once readable
});

static void SuppressionCopies()
{
    var live = new CursorSettings();
    var working = live.Clone();

    // The list itself can't be changed in place, so a clone sharing it is still independent.
    bool threw = false;
    try { ((IList<string>)working.SuppressedPrograms).Add("cs2.exe"); }
    catch (NotSupportedException) { threw = true; }
    Equal(true, threw);
    Equal(1, live.SuppressedPrograms.Count);

    working.SuppressedPrograms = working.SuppressedPrograms.Append("cs2.exe").ToArray();
    Equal("dota2.exe", Joined(live.SuppressedPrograms));

    // Applying Preferences carries the list; editing the dialog's copy afterwards doesn't leak.
    live.CopyPreferencesFrom(working);
    Equal("dota2.exe|cs2.exe", Joined(live.SuppressedPrograms));
    working.SuppressedPrograms = Array.Empty<string>();
    Equal("dota2.exe|cs2.exe", Joined(live.SuppressedPrograms));

    // An array handed to the setter is copied, so changing that array later changes nothing.
    var source = new[] { "a.exe" };
    live.SuppressedPrograms = source;
    source[0] = "b.exe";
    Equal("a.exe", Joined(live.SuppressedPrograms));
}

// Integration smoke against the real desktop: the resolvers must produce exe file names, and the
// watcher must start, report a sane foreground program, and shut down promptly.
static void ForegroundSmoke()
{
    string? self = WindowPrograms.ProgramForProcess((uint)Environment.ProcessId);
    Equal(Path.GetFileName(Environment.ProcessPath), self);
    Equal<string?>(null, WindowPrograms.ProgramForWindow(IntPtr.Zero));

    foreach (var program in WindowPrograms.ListVisible())
    {
        Equal(true, program.Program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        Equal(false, string.Equals(program.Program, self, StringComparison.OrdinalIgnoreCase));
        Equal(false, string.IsNullOrEmpty(program.Title));
    }

    var started = DateTime.UtcNow;
    using (var watcher = new ForegroundWatcher())
    {
        string? foreground = watcher.ForegroundProgram;
        Equal(true, foreground is null || foreground.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }
    Equal(true, DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
}
