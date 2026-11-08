using System.Text.Json;
using System.Text.Json.Nodes;
using WormsCursor.App;
using WormsCursor.App.Services;
using WormsCursor.Core;

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
    activity.Report(message.Tool, message.SessionId, message.Project, kind, now, message.InputIds);
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
    string path = AgentHookRegistrar.CodexPath;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    foreach (string raw in new[] { "[1]", "null", "{", "{\"hooks\":[]}", "{\"hooks\":{\"Stop\":{}}}" })
    {
        File.WriteAllText(path, raw);
        Equal(HookState.ConfigConflict, AgentHookRegistrar.GetState("codex"));
        bool rejected = false;
        try { AgentHookRegistrar.Register("codex"); }
        catch (InvalidOperationException) { rejected = true; }
        Equal(true, rejected);
        Equal(raw, File.ReadAllText(path));
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
