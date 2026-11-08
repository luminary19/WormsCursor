using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using WormsCursor.App;
using WormsCursor.App.ControlPanel;
using WormsCursor.App.Services;
using WormsCursor.Core;
using OpenCodeCursorOverlay;

var tests = new (string Name, Action Run)[]
{
    ("Codex lifecycle payloads", Lifecycle),
    ("Sweeping during a hook refreshes the waiting logos", SweepDuringHook),
    ("Async questions persist until their individual replies", AsyncQuestions),
    ("Async questions and approvals clear independently", OverlappingAttention),
    ("Claude events map from their payloads alone", Claude),
    ("Blocked-on-you prompts clear only on their own answer", BlockedAttribution),
    ("Waiting sessions report finished vs blocked", FinishedVersusBlocked),
    ("The token shows a blocked session first, badged, with the rest counted", TokenFaceSelection),
    ("The token draws the \"?\" badge only when blocked, clear of the count", BadgeDrawing),
    ("Codex registration merges, refreshes, and unregisters", Registration),
    ("Codex's Windows hook command parses in PowerShell", CodexWindowsCommandShell),
    ("Malformed configs are preserved", MalformedConfigs),
    ("CODEX_HOME is respected", CodexHome),
    ("A sibling subagent's tool use keeps another agent's approval", SubagentApprovals),
    ("Claude registration keeps user commands and the original backup", ClaudeRegistration),
    ("Unknown tool ids are rejected", UnknownTool),
    ("Renderers on two threads draw concurrently without sharing GDI+ objects", ConcurrentRendering),
    ("Each settings edit changes only its own fields, clamped and saved", LiveSettingsEdits),
    ("The control panel's status reads at a glance", PanelStatusText),
    ("Release notes parse headings, bullets, soft-wrapped paragraphs and bold", ReleaseNotesParsing),
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

static AgentEventMessage Parse(object payload, string tool = "codex")
    => AgentHookBridge.Parse(tool, JsonSerializer.Serialize(payload))
        ?? throw new InvalidOperationException("Expected a normalized event.");

static AgentEventMessage? TryParse(object payload, string tool = "claude-code")
    => AgentHookBridge.Parse(tool, JsonSerializer.Serialize(payload));

static string States(AgentActivity activity)
    => string.Join(',', activity.Waiting.Select(w => $"{w.Tool}:{w.State}"));

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
    Equal("codex:Finished", States(activity));
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
    Equal("codex:Finished", States(activity));
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
    Equal("turn_complete", TryParse(new { hook_event_name = "Stop" })?.Event);
    Equal<AgentEventMessage?>(null, AgentHookBridge.Parse("claude-code", "{\"hook_event_name\":\"Stop\",\"background_tasks\":[{\"type\":\"subagent\"}]}"));
    Equal("error", TryParse(new { hook_event_name = "StopFailure" })?.Event);

    // Notification carries no agent_id, so it is never a blocked state; only the idle reminder counts,
    // as a finished nudge. A legacy "--event awaiting_user" argument can no longer turn it into one.
    Equal("turn_complete", TryParse(new { hook_event_name = "Notification", notification_type = "idle_prompt" })?.Event);
    foreach (string? type in new[] { "permission_prompt", "elicitation_dialog", null })
        Equal<AgentEventMessage?>(null, TryParse(new { hook_event_name = "Notification", notification_type = type }));

    // AskUserQuestion is a tool: its PreToolUse is the question, owned by the asking agent.
    var asked = TryParse(new { hook_event_name = "PreToolUse", tool_name = "AskUserQuestion", agent_id = "agent-a" })!;
    Equal("awaiting_user", asked.Event);
    Equal("agent-a", asked.AgentId);
    Equal("awaiting_user", TryParse(new { hook_event_name = "PermissionRequest", tool_name = "Bash" })?.Event);
    foreach (string name in new[] { "PreToolUse", "PostToolUse", "PostToolUseFailure" })
        Equal("tool_use", TryParse(new { hook_event_name = name, tool_name = "Bash" })?.Event);
    Equal("tool_use", TryParse(new { hook_event_name = "PostToolUse", tool_name = "AskUserQuestion" })?.Event);

    // An MCP elicitation names itself (server + optional id) on both sides.
    var elicit = TryParse(new { hook_event_name = "Elicitation", mcp_server_name = "github", message = "Token?", mode = "form" })!;
    var result = TryParse(new { hook_event_name = "ElicitationResult", mcp_server_name = "github", action = "accept", mode = "form" })!;
    Equal("awaiting_user", elicit.Event);
    Equal("input_answered", result.Event);
    Equal(elicit.InputIds!.Single(), result.InputIds!.Single());
    var other = TryParse(new { hook_event_name = "ElicitationResult", mcp_server_name = "github", elicitation_id = "e-2", action = "cancel" })!;
    Equal(false, other.InputIds!.Single() == elicit.InputIds!.Single());

    Equal<AgentEventMessage?>(null, AgentHookBridge.Parse("claude-code", ""));
    Equal<AgentEventMessage?>(null, AgentHookBridge.Parse("claude-code", "not json"));
}

// Claude payloads, end to end through the bridge into AgentActivity, for one session "s".
static void BlockedAttribution()
{
    var now = DateTime.UtcNow;
    AgentEventMessage Ev(string name, string? agent = null, string tool = "Bash", string? server = null, string? id = null)
        => Parse(new
        {
            hook_event_name = name, session_id = "s", tool_name = tool, agent_id = agent,
            mcp_server_name = server, elicitation_id = id,
        }, "claude-code");
    void Run(AgentActivity a, params AgentEventMessage[] events) { foreach (var e in events) Apply(a, e, now); }

    // A subagent asks, a sibling and the main agent keep working: still blocked, never swept.
    foreach (var ask in new Func<AgentEventMessage>[]
             { () => Ev("PermissionRequest", "a"), () => Ev("PreToolUse", "a", "AskUserQuestion") })
    {
        var activity = new AgentActivity(TimeSpan.FromSeconds(10));
        Run(activity, ask(), Ev("PreToolUse", "b"), Ev("PostToolUse", "b"), Ev("PostToolUse"));
        activity.Sweep(now.AddHours(1));
        Equal("claude-code:Blocked", States(activity));
        Run(activity, Ev("PostToolUse", "a", "AskUserQuestion")); // a answered
        Equal("", States(activity));
    }

    // Two subagents ask; each answer clears only its own prompt. A declined question
    // (PostToolUseFailure) is an answer too.
    var two = new AgentActivity(TimeSpan.FromSeconds(10));
    Run(two, Ev("PreToolUse", "a", "AskUserQuestion"), Ev("PermissionRequest", "b"));
    Run(two, Ev("PostToolUseFailure", "a", "AskUserQuestion"));
    Equal("claude-code:Blocked", States(two));
    Run(two, Ev("PostToolUse", "b"));
    Equal("", States(two));

    // The main agent asks while background subagents run tools: stays until the main agent answers.
    var main = new AgentActivity(TimeSpan.FromSeconds(10));
    Run(main, Ev("PreToolUse", null, "AskUserQuestion"));
    for (int i = 0; i < 3; i++) Run(main, Ev("PreToolUse", "bg-1"), Ev("PostToolUse", "bg-2"), Ev("PostToolUseFailure", "bg-1"));
    main.Sweep(now.AddHours(1));
    Equal("claude-code:Blocked", States(main));
    Run(main, Ev("PostToolUse", null, "AskUserQuestion"));
    Equal("", States(main));

    // An MCP elicitation has no agent: no agent's tool use clears it, only its own result does.
    var elicit = new AgentActivity(TimeSpan.FromSeconds(10));
    Run(elicit, Ev("Elicitation", server: "github"), Ev("Elicitation", server: "auth", id: "e-1"));
    Run(elicit, Ev("PostToolUse"), Ev("PostToolUse", "a"), Ev("ElicitationResult", server: "auth", id: "e-2"));
    elicit.Sweep(now.AddHours(1));
    Equal("claude-code:Blocked", States(elicit));
    Run(elicit, Ev("ElicitationResult", server: "github"));
    Equal("claude-code:Blocked", States(elicit)); // "auth"/"e-1" is still open
    Run(elicit, Ev("ElicitationResult", server: "auth", id: "e-1"));
    Equal("", States(elicit));

    // A finished turn ends every mid-turn prompt; a new prompt clears everything.
    Run(elicit, Ev("Elicitation", server: "github"), Ev("PermissionRequest", "a"), Ev("Stop"));
    Equal("claude-code:Finished", States(elicit));
    Run(elicit, Ev("PermissionRequest", "a"), Ev("UserPromptSubmit"));
    Equal("", States(elicit));
}

static void FinishedVersusBlocked()
{
    var now = DateTime.UtcNow;
    var activity = new AgentActivity(TimeSpan.FromSeconds(10));
    var updates = new List<int>();
    activity.WaitingChanged += updates.Add;
    Apply(activity, Parse(new { hook_event_name = "Stop", session_id = "one" }, "claude-code"), now);
    Apply(activity, Parse(new { hook_event_name = "PermissionRequest", session_id = "two", tool_name = "Bash" }, "claude-code"), now);
    Apply(activity, Parse(new { hook_event_name = "Notification", session_id = "three", notification_type = "idle_prompt" }, "claude-code"), now);
    Equal("claude-code:Finished,claude-code:Blocked,claude-code:Finished", States(activity));
    Equal(3, activity.WaitingCount);

    // Moving between finished and blocked at the same count still refreshes the display.
    activity.Report("codex", "four", null, AgentEventKind.TurnComplete, now);
    activity.Report("codex", "four", null, AgentEventKind.InputRequested, now, new[] { "q-1" });
    Equal("1,2,3,4,4", string.Join(',', updates));
    Equal("codex:Blocked", activity.Waiting[3].Tool + ":" + activity.Waiting[3].State);

    // The idle timeout sweeps only the finished ones.
    activity.Sweep(now.AddSeconds(11));
    Equal("claude-code:Blocked,codex:Blocked", States(activity));
    activity.Report("codex", "four", null, AgentEventKind.InputAnswered, now.AddSeconds(12), new[] { "q-1" });
    Equal("claude-code:Blocked", States(activity));
}

static void TokenFaceSelection()
{
    WaitingAgent Finished(string tool) => new(tool, WaitingState.Finished);
    WaitingAgent Blocked(string tool) => new(tool, WaitingState.Blocked);

    Equal(null, TokenFace.For(null));
    Equal(null, TokenFace.For(Array.Empty<WaitingAgent>()));
    Equal(new TokenFace("codex", false, 0), TokenFace.For(new[] { Finished("codex") }));
    Equal(new TokenFace("codex", true, 0), TokenFace.For(new[] { Blocked("codex") }));
    // Blocked beats finished wherever it sits; the others are counted whatever their state.
    Equal(new TokenFace("codex", true, 2), TokenFace.For(new[] { Finished("claude-code"), Blocked("codex"), Finished("claude-code") }));
    Equal(new TokenFace("claude-code", false, 1), TokenFace.For(new[] { Finished("claude-code"), Finished("codex") }));
    // Several blocked: the first one shows.
    Equal(new TokenFace("claude-code", true, 2), TokenFace.For(new[] { Finished("codex"), Blocked("claude-code"), Blocked("codex") }));
    // Entries without a tool id are neither shown nor counted.
    Equal(new TokenFace("codex", true, 0), TokenFace.For(new[] { Blocked(""), Blocked("codex"), Finished("") }));
    Equal(null, TokenFace.For(new[] { Finished("") }));

    // End to end from hook payloads: one Claude session finished, one Codex session asks for approval.
    var now = DateTime.UtcNow;
    var activity = new AgentActivity(TimeSpan.FromSeconds(10));
    Apply(activity, Parse(new { hook_event_name = "Stop", session_id = "one" }, "claude-code"), now);
    Equal(new TokenFace("claude-code", false, 0), TokenFace.For(activity.Waiting));
    Apply(activity, Parse(new { hook_event_name = "PermissionRequest", session_id = "two", tool_name = "shell" }), now);
    Equal(new TokenFace("codex", true, 1), TokenFace.For(activity.Waiting));
    // Approving it (the agent's next tool use) drops the badge and hands the token back.
    Apply(activity, Parse(new { hook_event_name = "PostToolUse", session_id = "two", tool_name = "shell" }), now);
    Equal(new TokenFace("claude-code", false, 0), TokenFace.For(activity.Waiting));
}

// Pixels, not just logic: the yellow disc appears only for a blocked face, at the logo's top-right,
// for every tool and token size, and the "+N" pill beside it never covers it.
static void BadgeDrawing()
{
    var settings = new CursorSettings();
    using var renderer = new NotifierRenderer();
    using var logos = new AgentLogos();
    foreach (var tool in new[] { "claude-code", "codex" })
        foreach (int side in new[] { 48, 96, 192 })
            foreach (int others in new[] { 0, 3, 12 })
            {
                var logo = logos.Fit(tool, new RectangleF(-side / 2f, -side / 2f, side, side));
                var (center, r) = AttentionBadge.Place(logo, side);
                int dim = side * 3, mid = dim / 2;
                // A spot on the yellow face, left of the "?" glyph.
                int px = mid + (int)MathF.Round(center.X - r * 0.62f), py = mid + (int)MathF.Round(center.Y + r * 0.1f);
                foreach (bool blocked in new[] { false, true })
                {
                    using var canvas = new Bitmap(dim, dim);
                    using (var g = Graphics.FromImage(canvas))
                        renderer.DrawToken(g, settings, new TokenFace(tool, blocked, others), mid, mid, side, 0f);
                    var c = canvas.GetPixel(px, py);
                    bool yellow = c.A > 240 && c.R > 230 && c.G > 170 && c.B < 90;
                    if (yellow != blocked)
                        throw new InvalidOperationException($"{tool} {side}px +{others} blocked={blocked}: badge pixel was {c}.");
                }
            }
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

    // The pre-PowerShell shape ("<exe>" hook --tool codex in commandWindows, which PowerShell rejects)
    // reads as outdated, and Register rewrites it in place rather than adding a second handler.
    var broken = JsonNode.Parse(File.ReadAllText(path))!;
    foreach (var ev in broken["hooks"]!.AsObject())
        foreach (var group in ev.Value!.AsArray())
            foreach (var hook in group!["hooks"]!.AsArray())
                if (hook!["commandWindows"] is JsonNode windows && windows.GetValue<string>().Contains("hook --tool codex"))
                    hook["commandWindows"] = hook["command"]!.GetValue<string>();
    File.WriteAllText(path, broken.ToJsonString());
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("codex"));
    AgentHookRegistrar.Register("codex");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("codex"));
    var fixedUp = JsonNode.Parse(File.ReadAllText(path))!;
    var ours = fixedUp["hooks"]!["Stop"]![0]!["hooks"]![1]!;
    Equal(AgentHookRegistrar.CodexWindowsCommand(Environment.ProcessPath!), ours["commandWindows"]!.GetValue<string>());
    Equal(2, fixedUp["hooks"]!["Stop"]![0]!["hooks"]!.AsArray().Count);

    AgentHookRegistrar.Unregister("codex");
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("codex"));
    Equal(true, JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(File.ReadAllText(path))));
});

// Codex runs commandWindows as `powershell.exe -NoProfile -Command <commandWindows>` (pwsh when
// installed). Parse the emitted command with PowerShell's own parser: it must be a call-operator
// invocation of exactly the exe and our arguments, even for paths with spaces, `$`, backticks and `'`.
static void CodexWindowsCommandShell()
{
    const string script = """
        $e = $null; $t = $null
        $a = [System.Management.Automation.Language.Parser]::ParseInput($env:WC_HOOK_CMD, [ref]$t, [ref]$e)
        $c = $a.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)
        $parts = @($e | ForEach-Object { 'error:' + $_.ErrorId })
        if ($c) {
            $parts += 'op:' + $c.InvocationOperator
            $parts += @($c.CommandElements | ForEach-Object {
                if ($_ -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $_.Value } else { '<expr>' } })
        }
        [Console]::Out.Write(($parts -join "`n"))
        """;
    string Shape(string command)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                         "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + script)) })
            psi.ArgumentList.Add(arg);
        psi.Environment["WC_HOOK_CMD"] = command;
        using var ps = System.Diagnostics.Process.Start(psi)!;
        string output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return output.Replace("\r", "");
    }

    foreach (string exe in new[]
             {
                 @"C:\Users\me\AppData\Local\WormsCursor\current\WormsCursor.exe",
                 @"C:\Program Files\Worms Cursor\WormsCursor.exe",
                 @"C:\Users\O'Brien\$HOME `n\WormsCursor.exe",
             })
        Equal($"op:Ampersand\n{exe}\nhook\n--tool\ncodex", Shape(AgentHookRegistrar.CodexWindowsCommand(exe)));

    // The old form is what Codex's PowerShell rejected: a quoted path followed by bare arguments.
    Equal(true, Shape("\"C:\\Program Files\\Worms Cursor\\WormsCursor.exe\" hook --tool codex").StartsWith("error:UnexpectedToken"));
}

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
    string once = File.ReadAllText(path);
    AgentHookRegistrar.Register("claude-code"); // a refresh rewrites nothing
    Equal(once, File.ReadAllText(path));
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

    // So does the previous version's shape: Notification routed per type with --event (its
    // elicitation_dialog entry filed subagent prompts under the main agent), no PostToolUseFailure,
    // no Elicitation / ElicitationResult. Register replaces it with today's events.
    string exe = Environment.ProcessPath!;
    JsonObject Group(string matcher, string command) => new()
    {
        ["matcher"] = matcher,
        ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
    };
    var previous = new JsonObject();
    foreach (string ev in new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop", "StopFailure", "SessionStart", "SessionEnd" })
        previous[ev] = new JsonArray(Group("*", $"\"{exe}\" hook --tool claude-code"));
    previous["Notification"] = new JsonArray(
        Group("elicitation_dialog", $"\"{exe}\" hook --tool claude-code --event awaiting_user"),
        Group("idle_prompt", $"\"{exe}\" hook --tool claude-code --event turn_complete"));
    File.WriteAllText(path, new JsonObject { ["hooks"] = previous }.ToJsonString());
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("claude-code"));
    AgentHookRegistrar.Register("claude-code");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("claude-code"));
    string current = File.ReadAllText(path);
    Equal(false, current.Contains("--event"));
    Equal(false, current.Contains("elicitation_dialog"));
    var hooks = JsonNode.Parse(current)!["hooks"]!;
    foreach (string ev in new[] { "PostToolUseFailure", "Elicitation", "ElicitationResult" })
        Equal("*", hooks[ev]![0]!["matcher"]!.GetValue<string>());
    Equal(1, hooks["Notification"]!.AsArray().Count);
    Equal("idle_prompt", hooks["Notification"]![0]!["matcher"]!.GetValue<string>());

    // A hook pointing at another exe (an old install or dev build) also needs a refresh.
    File.WriteAllText(path, current.Replace(JsonSerializer.Serialize(exe)[1..^1], @"C:\\old\\WormsCursor.exe"));
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
    var face = new TokenFace("claude-code", Blocked: true, Others: 1);
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
                renderer.DrawToken(g, settings, face, 128f, 128f, 96f, 0f);
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

static void LiveSettingsEdits()
{
    var saves = new List<CursorSettings>();
    var live = new LiveSettings(new CursorSettings(), s => saves.Add(s.Clone()));
    int changed = 0;
    live.Changed += () => changed++;

    var snapshot = live.Current;                       // the panel reads a copy...
    live.Change(s => s.AgentNotifierEnabled = false);  // ...the tray switches the token off...
    live.Change(s => s.Size = 150);                    // ...then the panel changes the size
    Equal(false, live.Live.AgentNotifierEnabled);      // the size edit didn't bring the switch back
    Equal(150, live.Live.Size);
    Equal(true, snapshot.AgentNotifierEnabled);        // and the copy never moved
    Equal(96, snapshot.Size);
    Equal(2, changed);
    Equal(2, saves.Count);
    Equal(false, saves[^1].AgentNotifierEnabled);
    Equal(150, saves[^1].Size);

    // Out-of-range values are clamped before they are saved or seen.
    live.Change(s => { s.Size = 999; s.AgentNotifierTimeoutSeconds = 1; });
    Equal(CursorSettings.MaxSize, saves[^1].Size);
    Equal(CursorSettings.MinTimeoutSeconds, live.Live.AgentNotifierTimeoutSeconds);

    // Resetting the look leaves the switch, the timeout and the program list alone.
    live.Change(s => { s.Placement = NotifierPlacement.Corner; s.Corner = ScreenCorner.TopLeft; s.SuppressedPrograms = new[] { "cs2.exe" }; });
    Equal(false, live.Live.HasDefaultAppearance());
    live.Change(s => s.ResetAppearance());
    Equal(true, live.Live.HasDefaultAppearance());
    Equal(NotifierPlacement.Cursor, live.Live.Placement);
    Equal(false, live.Live.AgentNotifierEnabled);
    Equal(CursorSettings.MinTimeoutSeconds, live.Live.AgentNotifierTimeoutSeconds);
    Equal("cs2.exe", Joined(live.Live.SuppressedPrograms));

    // A method, so it never lands in settings.json.
    Equal(false, JsonSerializer.Serialize(live.Live).Contains("Appearance"));
}

static void PanelStatusText()
{
    Equal("Off", new PanelStatus(false, 3, 1, "dota2.exe").Text);
    Equal(StatusTone.Off, new PanelStatus(false, 3, 1, null).Tone);
    Equal("On · No agents waiting", new PanelStatus(true, 0, 0, null).Text);
    Equal(StatusTone.Idle, new PanelStatus(true, 0, 0, null).Tone);
    Equal("On · 1 agent is done", new PanelStatus(true, 1, 0, null).Text);
    Equal("On · 3 agents are done", new PanelStatus(true, 3, 0, null).Text);
    Equal("On · 1 agent needs you", new PanelStatus(true, 3, 1, null).Text);
    Equal("On · 2 agents need you", new PanelStatus(true, 2, 2, null).Text);
    Equal(StatusTone.Attention, new PanelStatus(true, 2, 2, null).Tone);
    Equal("Hidden while dota2.exe is in front", new PanelStatus(true, 2, 1, "dota2.exe").Text);
    Equal(StatusTone.Paused, new PanelStatus(true, 0, 0, "dota2.exe").Tone);
}

static void ReleaseNotesParsing()
{
    const string CrLf = "\r\n"; // release bodies can carry Windows line ends
    var blocks = ReleaseNotesFormat.Parse(string.Join(CrLf,
        "### Added", "- **The token** stays out of", "  your games.", "- Second", "",
        "A plain", "paragraph.", "## Fixed", "* Star bullet"));
    string Show(NoteBlock b) => b.Kind + ":" + string.Concat(b.Runs.Select(r =>
        r.Code ? "<" + r.Text + ">" : r.Bold ? "[" + r.Text + "]" : r.Text));
    Equal("Heading:Added|Bullet:[The token] stays out of your games.|Bullet:Second|Paragraph:A plain paragraph.|Heading:Fixed|Bullet:Star bullet",
        string.Join("|", blocks.Select(Show)));
    Equal(0, ReleaseNotesFormat.Parse("").Count);
    Equal("Bullet:[Clears on ]</clear> and <**not bold**>.", Show(ReleaseNotesFormat.Parse("- **Clears on `/clear`** and `**not bold**`.").Single()));
    Equal("Paragraph:[unclosed bold]", Show(ReleaseNotesFormat.Parse("**unclosed bold").Single()));
}

static void SettingsPersistence() => WithHome(home =>
{
    string path = Path.Combine(home, "settings.json");
    // Hand-edit slips the reader forgives.
    File.WriteAllText(path, "{\n  // bigger token\n  \"Size\": 120,\n}");
    Equal(120, SettingsStore.Load(path).Size);

    // An unparseable file loads as the defaults and is left alone; the save that replaces it keeps a
    // copy first, and later saves (of a now-valid file) make no more copies.
    string corrupt = "{\"Size\": 120, \"Corner\": \"Bottom\"}";
    File.WriteAllText(path, corrupt);
    var loaded = SettingsStore.Load(path);
    Equal(96, loaded.Size);
    Equal(corrupt, File.ReadAllText(path));
    Equal(0, Directory.GetFiles(home, "settings.json.corrupt-*").Length);
    SettingsStore.Save(loaded, path);
    string[] kept = Directory.GetFiles(home, "settings.json.corrupt-*");
    Equal(1, kept.Length);
    Equal(corrupt, File.ReadAllText(kept[0]));
    Equal(96, SettingsStore.Load(path).Size);
    SettingsStore.Save(loaded, path);
    Equal(1, Directory.GetFiles(home, "settings.json.corrupt-*").Length);

    // If that copy can't be made, nothing is written: the corrupt file survives and Save throws.
    File.Delete(kept[0]);
    File.WriteAllText(path, corrupt);
    var blocked = new List<string>();
    var stamp = DateTime.Now;
    for (int s = -1; s <= 5; s++) // occupy every name the copy could take in the next few seconds
    {
        string name = $"{path}.corrupt-{stamp.AddSeconds(s):yyyyMMdd-HHmmss}";
        Directory.CreateDirectory(name);
        blocked.Add(name);
    }
    bool refused = false;
    try { SettingsStore.Save(loaded, path); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { refused = true; }
    Equal(true, refused);
    Equal(corrupt, File.ReadAllText(path));
    Equal(false, File.Exists(path + ".tmp"));
    foreach (string name in blocked) Directory.Delete(name);
    SettingsStore.Save(loaded, path);

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

    // An edit through LiveSettings replaces the live list; a copy taken earlier keeps its own.
    var owner = new LiveSettings(live, _ => { });
    var before = owner.Current;
    owner.Change(s => s.SuppressedPrograms = s.SuppressedPrograms.Append("cs2.exe").ToArray());
    Equal("dota2.exe|cs2.exe", Joined(live.SuppressedPrograms));
    Equal("dota2.exe", Joined(before.SuppressedPrograms));

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
