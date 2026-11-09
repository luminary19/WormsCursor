using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentsCursor.App;
using AgentsCursor.App.ControlPanel;
using AgentsCursor.App.Services;
using AgentsCursor.Core;

var tests = new (string Name, Action Run)[]
{
    ("Codex lifecycle payloads", Lifecycle),
    ("Sweeping during a hook refreshes the waiting logos", SweepDuringHook),
    ("Async questions persist until their individual replies", AsyncQuestions),
    ("Async questions and approvals clear independently", OverlappingAttention),
    ("Claude events map from their payloads alone", Claude),
    ("Blocked-on-you prompts clear only on their own answer", BlockedAttribution),
    ("A parallel call finishing keeps another call's approval", ParallelCalls),
    ("Waiting sessions report finished vs blocked", FinishedVersusBlocked),
    ("The token shows a blocked session first, badged, with the rest counted", TokenFaceSelection),
    ("The token draws a yellow \"?\" cloud when blocked and a green check cloud when done, clear of the count; bare icons get none", BadgeDrawing),
    ("Codex registration merges, refreshes, and unregisters", Registration),
    ("Codex's Windows hook command parses in PowerShell", CodexWindowsCommandShell),
    ("Malformed configs are preserved", MalformedConfigs),
    ("CODEX_HOME is respected", CodexHome),
    ("A sibling subagent's tool use keeps another agent's approval", SubagentApprovals),
    ("Claude registration keeps user commands and the original backup", ClaudeRegistration),
    ("Hooks WormsCursor.exe wrote read as outdated and are replaced in place", LegacyHooks),
    ("Unknown tool ids are rejected", UnknownTool),
    ("Renderers on two threads draw concurrently without sharing GDI+ objects", ConcurrentRendering),
    ("Each settings edit changes only its own fields, clamped and saved", LiveSettingsEdits),
    ("Release notes parse headings, bullets, soft-wrapped paragraphs and bold", ReleaseNotesParsing),
    ("Corrupt and unwritable settings files are never destroyed", SettingsPersistence),
    ("Autostart only counts a Run entry that launches this exe", AutostartTarget),
    ("Autostart moves WormsCursor's Run entry over once, only if it was on", AutostartMigration),
    ("Settings load from WormsCursor's file, read-only, while there is none of ours", SettingsLegacyLoad),
    ("The panel theme defaults to System and round-trips through settings.json", ThemeSetting),
    ("Suppressed program names normalize and match case-insensitively", SuppressionMatching),
    ("The suppressed-program list round-trips through settings.json", SuppressionSettingsJson),
    ("Settings copies never share a mutable program list", SuppressionCopies),
    ("The foreground watcher and window listing resolve real exe names", ForegroundSmoke),
    ("The pipe serves a persistent plugin and 20 hooks at once, tagged and in order", PipeConcurrentClients),
    ("A closed plugin connection releases exactly its own sessions", PipeLeaseRelease),
    ("An oversized line drops only its connection; malformed lines are skipped", PipeOversizedLine),
    ("Releasing an owner drops its sessions with one change, and an unknown owner is a no-op", ActivityRelease),
    ("Pi registration merges, refreshes outdated entries in place, and unregisters", PiRegistration),
    ("PI_CODING_AGENT_DIR is respected, except under the test home", PiHome),
    ("OpenCode JSONC edits keep comments, commas and line endings byte-identical", OpenCodeRegistration),
    ("OpenCode's config is written in place, so a hard link to it sees the change", OpenCodeHardLink),
    ("Startup refresh updates only outdated connections and leaves the rest untouched", RefreshConnectedOnly),
    ("A plugin folder is ours only when it holds AgentsCursor's plugin, never by its name alone", ForeignPluginFolders),
    ("Paths compare as spelled on disk, whatever case the exe was launched with", OnDiskPaths),
    ("Every agent's original icon renders, fitted, with the badge on its top-right", BuiltInIcons),
    ("Placement \"Cursor\" migrates to BelowPointer; numbers, unknowns and v2 files load right", AppearanceTests.PlacementMigration),
    ("Icon choices normalize, round-trip, copy safely and reset", AppearanceTests.IconSettings),
    ("The SVG sanitiser strips active and external content and rejects what it can't trust", AppearanceTests.Sanitizer),
    ("Added icons are content-addressed, immutable, limited, and removal falls back to the original", AppearanceTests.CustomStore),
    ("A missing or broken added icon draws the original, logs once, and renders on two threads", AppearanceTests.CustomFallback),
    ("Every built-in icon is non-empty, solid, within its aspect limits and small", AppearanceTests.BuiltInMascots),
    ("At any swing, every icon with its badge and +9 stays inside the canvas", AppearanceTests.CanvasExtent),
    ("Every pointer placement settles at rest, stays on its string, hops in and flips at edges", AppearanceTests.Tether),
    ("Bouncy, Calm and Still keep their motion bounds; animations off means Still", AppearanceTests.MotionPresets),
    ("Mascot loops pick frames deterministically, start at rest, rest most of the time and wrap", MascotTests.FrameSelection),
    ("Mascots animate only when on and the token isn't still; no clock is the rest frame; originals never move", MascotTests.AnimateSetting),
    ("Every mascot part is in its SVG, rests where the file draws it, and never leaves its viewBox", MascotTests.MascotArt),
    ("Every mascot frame keeps one fit and badge anchor and stays in its box and the token canvas", MascotTests.FramesInBox),
    ("Animated frames stay within the cache budget and an evicted frame is rebuilt", MascotTests.FrameBudget),
    ("Mascot frames render on two threads at once, the same clock giving the same picture", MascotTests.ConcurrentFrames),
    ("A chime plays once per new waiting session, only while the token shows, one per burst", SoundAndMeetingTests.SoundDecisions),
    ("Sound settings default off, round-trip by name and clamp", SoundAndMeetingTests.SoundSettings),
    ("Volume scales the samples, never anything else", SoundAndMeetingTests.ChimeVolume),
    ("The built-in chimes are short, distinct, unclipped and start and end silent", SoundAndMeetingTests.BuiltInChimes),
    ("Meetings: presenting, sharing, camera and a settled microphone hide the token; stale entries don't", SoundAndMeetingTests.MeetingDecisions),
    ("The meeting watcher checks only while watching and treats a failing check as no meeting", SoundAndMeetingTests.MeetingWatching),
    ("The live meeting signals read without throwing", SoundAndMeetingTests.MeetingSignalsSmoke),
    ("Each wait ends once, answered, resumed, expired or ended, timed from when it began", AnalyticsTests.ActivityWaits),
    ("The history round-trips one compact line per wait, a file per month, appending", AnalyticsTests.HistoryRoundTrip),
    ("A torn or corrupt history line costs only itself and is logged once", AnalyticsTests.HistoryCorruption),
    ("History keeps the current month and the 12 before it, and only touches its own files", AnalyticsTests.HistoryRetention),
    ("Streaks count local days in a row, today not breaking them until it is over", AnalyticsTests.Streaks),
    ("Average response is the mean answered needs-you wait of the last 30 days, formatted compactly", AnalyticsTests.AverageResponse),
    ("Agents are counted over 30 days in their fixed order; shares add to 100; heat levels by quartile", AnalyticsTests.AgentCounts),
    ("Days are local calendar days across daylight saving, and the calendar starts on a week boundary", AnalyticsTests.DaylightSaving),
    ("Activity to history to summary keeps no project, path or session id", AnalyticsTests.EndToEnd),
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
    activity.Report(message.Tool, message.SessionId, message.Project, kind, now, message.InputIds, message.AgentId, message.CallIds);
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

// Claude payloads, end to end through the bridge into AgentActivity, for one session "s". Each
// (agent, tool) pair stands for one call: the same tool_input everywhere, and a tool_use_id on every
// event but PermissionRequest, which has none.
static void BlockedAttribution()
{
    var now = DateTime.UtcNow;
    AgentEventMessage Ev(string name, string? agent = null, string tool = "Bash", string? server = null, string? id = null)
        => Parse(new
        {
            hook_event_name = name, session_id = "s", tool_name = tool, agent_id = agent,
            tool_input = new { command = "run " + tool },
            tool_use_id = name == "PermissionRequest" ? null : $"{agent}:{tool}",
            mcp_server_name = server, elicitation_id = id,
        }, "claude-code");
    void Run(AgentActivity a, params AgentEventMessage[] events) { foreach (var e in events) Apply(a, e, now); }

    // A subagent asks, a sibling and the main agent keep working: still blocked, never swept.
    foreach (var (ask, answer) in new (Func<AgentEventMessage>, Func<AgentEventMessage>)[]
             {
                 (() => Ev("PermissionRequest", "a"), () => Ev("PostToolUse", "a")),
                 (() => Ev("PreToolUse", "a", "AskUserQuestion"), () => Ev("PostToolUse", "a", "AskUserQuestion")),
             })
    {
        var activity = new AgentActivity(TimeSpan.FromSeconds(10));
        Run(activity, ask(), Ev("PreToolUse", "b"), Ev("PostToolUse", "b"), Ev("PostToolUse"));
        activity.Sweep(now.AddHours(1));
        Equal("claude-code:Blocked", States(activity));
        Run(activity, answer()); // a answered: its call ran
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

// The same agent runs calls in parallel (Claude batches read-only calls): PostToolUse fires per call,
// in any order, so one call finishing says nothing about another's pending approval.
static void ParallelCalls()
{
    var now = DateTime.UtcNow;
    AgentEventMessage Ev(string name, string tool, object input, string? id = null, string? agent = null)
        => Parse(new { hook_event_name = name, session_id = "s", tool_name = tool, tool_input = input, tool_use_id = id, agent_id = agent }, "claude-code");
    var outside = new { file_path = @"C:\elsewhere\notes.txt" };
    var grep = new { pattern = "TODO" };
    void Run(AgentActivity a, params AgentEventMessage[] events) { foreach (var e in events) Apply(a, e, now); }

    // The bridge names the call each prompt blocks, and every identity of a call that finished.
    var asked = Ev("PermissionRequest", "Read", outside);
    Equal(true, asked.CallIds!.Single().StartsWith("input:Read:"));
    var ran = Ev("PostToolUse", "Read", new { file_path = @"C:\elsewhere\notes.txt" }, "toolu_read");
    Equal("call:toolu_read," + asked.CallIds!.Single(), string.Join(',', ran.CallIds!));
    Equal("call:toolu_q", Ev("PreToolUse", "AskUserQuestion", new { questions = Array.Empty<object>() }, "toolu_q").CallIds!.Single());
    Equal<string[]?>(null, Ev("PreToolUse", "Read", outside, "toolu_read").CallIds);
    var batch = TryParse(new { hook_event_name = "PostToolBatch", session_id = "s", tool_calls = Array.Empty<object>() })!;
    Equal("tools_resolved", batch.Event);

    // Approved: the Grep finishing first keeps the token up; the Read itself running clears it.
    var approved = new AgentActivity(TimeSpan.FromSeconds(10));
    Run(approved, Ev("PreToolUse", "Read", outside, "toolu_read"), Ev("PreToolUse", "Grep", grep, "toolu_grep"),
        asked, Ev("PostToolUse", "Grep", grep, "toolu_grep"));
    approved.Sweep(now.AddHours(1));
    Equal("claude-code:Blocked", States(approved));
    Run(approved, ran);
    Equal("", States(approved));

    // Denied: the Read never runs, so it has no PostToolUse; the batch resolving answers it. So does
    // an approval that rewrote the input, whose PostToolUse no longer matches the request.
    foreach (var settle in new[] { Array.Empty<AgentEventMessage>(), new[] { Ev("PostToolUse", "Read", new { file_path = @"C:\other.txt" }, "toolu_read") } })
    {
        var denied = new AgentActivity(TimeSpan.FromSeconds(10));
        Run(denied, Ev("PreToolUse", "Read", outside, "toolu_read"), Ev("PreToolUse", "Grep", grep, "toolu_grep"),
            asked, Ev("PostToolUse", "Grep", grep, "toolu_grep"));
        Run(denied, settle);
        Equal("claude-code:Blocked", States(denied));
        // Another agent's batch is not this agent's.
        Run(denied, TryParse(new { hook_event_name = "PostToolBatch", session_id = "s", agent_id = "sub" })!);
        Equal("claude-code:Blocked", States(denied));
        Run(denied, batch);
        Equal("", States(denied));
    }

    // Codex: a question is keyed by its tool_use_id, so another call finishing keeps it up.
    var codex = new AgentActivity(TimeSpan.FromSeconds(10));
    AgentEventMessage Cx(string name, string tool, string id) => Parse(new
    {
        hook_event_name = name, session_id = "c", tool_name = tool, tool_use_id = id, tool_input = new { q = tool },
    });
    Run(codex, Cx("PreToolUse", "request_user_input", "call-q"), Cx("PostToolUse", "shell", "call-sh"));
    Equal("codex:Blocked", States(codex));
    Run(codex, Cx("PostToolUse", "request_user_input", "call-q"));
    Equal("", States(codex));
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

// Pixels, not just logic: the status cloud sits at the icon's top-right, yellow for a blocked face and
// green for a finished one, for every built-in icon (originals and mascots) and token size, and the "+N"
// pill beside it never covers it.
static void BadgeDrawing()
{
    var settings = new CursorSettings();
    using var renderer = new NotifierRenderer();
    foreach (var icon in IconCatalog.BuiltIns)
        foreach (int side in new[] { 48, 96, 192 })
            foreach (int others in new[] { 0, 3, 12 })
            {
                var logo = IconBounds(icon.Tool, icon.Id, side);
                var (center, r) = StatusBadge.Place(logo, side);
                int dim = side * 3, mid = dim / 2;
                // A spot on the cloud's lower-left puff, clear of the "?" and the check.
                int px = mid + (int)MathF.Round(center.X - r * 0.70f), py = mid + (int)MathF.Round(center.Y + r * 0.22f);
                foreach (bool blocked in new[] { false, true })
                {
                    using var canvas = new Bitmap(dim, dim);
                    using (var g = Graphics.FromImage(canvas))
                        renderer.DrawToken(g, settings, new TokenFace(icon.Tool, blocked, others), icon.Id, mid, mid, side, 0f, 1f);
                    var c = canvas.GetPixel(px, py);
                    // At 48 px the spot is about a pixel inside the puff's edge, so allow a little edge blend.
                    bool yellow = c.A > 240 && c.R > 215 && c.G > 165 && c.B < 120;
                    bool green = c.A > 240 && c.G > 150 && c.G > c.R + 40 && c.G > c.B + 40;
                    if (blocked ? !yellow : !green)
                        throw new InvalidOperationException($"{icon.Id} {side}px +{others} blocked={blocked}: badge pixel was {c}.");
                }
                // A bare icon (the panel's cards and tiles) carries no status at all.
                using (var bare = new Bitmap(dim, dim))
                {
                    using (var g = Graphics.FromImage(bare))
                        renderer.DrawIcon(g, icon.Tool, icon.Id, mid, mid, side, 1f);
                    var c = bare.GetPixel(px, py);
                    if (c.A > 240 && (c.R > 230 && c.G > 170 && c.B < 120 || c.G > 150 && c.G > c.R + 40 && c.G > c.B + 40))
                        throw new InvalidOperationException($"{icon.Id} {side}px: the bare icon has a badge pixel {c}.");
                }
            }
}

// Where an icon sits in a token of side <side> centred on the origin (as NotifierRenderer draws it).
static RectangleF IconBounds(string tool, string iconId, int side)
{
    using var sprites = new TokenSprites();
    var sprite = sprites.Get(tool, iconId, side, NotifierRenderer.RimWidth(side, 1f))!;
    var v = sprite.Visual;
    return new RectangleF(-side / 2f + v.X, -side / 2f + v.Y, v.Width, v.Height);
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
    string home = Path.Combine(Path.GetTempPath(), "AgentsCursor.HookTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(home);
    AgentHookRegistrar.HomeOverride = home;
    try { test(home); }
    finally
    {
        AgentHookRegistrar.HomeOverride = null;
        foreach (string file in Directory.GetFiles(home, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal); // a read-only test file (or its backup)
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
    Equal(original, File.ReadAllText(path + ".agentscursor.bak"));
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
                 @"C:\Users\me\AppData\Local\AgentsCursor\current\AgentsCursor.exe",
                 @"C:\Program Files\Agents Cursor\AgentsCursor.exe",
                 @"C:\Users\O'Brien\$HOME `n\AgentsCursor.exe",
             })
        Equal($"op:Ampersand\n{exe}\nhook\n--tool\ncodex", Shape(AgentHookRegistrar.CodexWindowsCommand(exe)));

    // The old form is what Codex's PowerShell rejected: a quoted path followed by bare arguments.
    Equal(true, Shape("\"C:\\Program Files\\Agents Cursor\\AgentsCursor.exe\" hook --tool codex").StartsWith("error:UnexpectedToken"));
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
        // An unreadable (locked) config is a conflict too, never an exception that reaches the panel.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Equal(HookState.ConfigConflict, AgentHookRegistrar.GetState(tool));
            bool rejected = false;
            try { AgentHookRegistrar.Register(tool); }
            catch (InvalidOperationException) { rejected = true; }
            Equal(true, rejected);
        }
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
        // Each agent's one call: Claude matches a PermissionRequest to the call's Post event by tool name
        // and input; Codex's PermissionRequest names no call, so the agent's next tool event answers it.
        var activity = new AgentActivity(TimeSpan.FromSeconds(10));
        AgentEventMessage Event(string name, string? agent) => Parse(new
        {
            hook_event_name = name, session_id = "parent", tool_name = "Bash", agent_id = agent,
            tool_input = new { command = "build " + agent },
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
        Apply(activity, Event("PostToolUse", null), now);
        Equal(1, activity.WaitingCount);
        Apply(activity, Event("PostToolUse", "agent-b"), now);
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
    // Exec form: the exe and its arguments, with no shell to parse a quoted path (without Git Bash,
    // Claude Code runs a shell-form hook in PowerShell, which rejects one). PostToolBatch takes no matcher.
    var handler = root["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!;
    Equal(Environment.ProcessPath!, handler["command"]!.GetValue<string>());
    Equal("hook,--tool,claude-code", string.Join(',', handler["args"]!.AsArray().Select(a => a!.GetValue<string>())));
    Equal(false, root["hooks"]!["PostToolBatch"]![0]!.AsObject().ContainsKey("matcher"));
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
    // Several writes later, the backup is still the user's original, pre-AgentsCursor file.
    Equal(original, File.ReadAllText(path + ".agentscursor.bak"));

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
    Equal(false, current.Contains("hook --tool claude-code")); // the shell-form commands are replaced
    Equal(13, JsonNode.Parse(current)!["hooks"]!.AsObject().Count);
    Equal(false, current.Contains("elicitation_dialog"));
    var hooks = JsonNode.Parse(current)!["hooks"]!;
    foreach (string ev in new[] { "PostToolUseFailure", "Elicitation", "ElicitationResult" })
        Equal("*", hooks[ev]![0]!["matcher"]!.GetValue<string>());
    Equal(1, hooks["Notification"]!.AsArray().Count);
    Equal("idle_prompt", hooks["Notification"]![0]!["matcher"]!.GetValue<string>());

    // A hook pointing at another exe (an old install or dev build) also needs a refresh.
    File.WriteAllText(path, current.Replace(JsonSerializer.Serialize(exe)[1..^1], @"C:\\old\\AgentsCursor.exe"));
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("claude-code"));
});

// Configs the app changed under its old name: the hooks carry the same `hook --tool <id>` marker
// with WormsCursor.exe's path, so they are ours, outdated, and replaced in place, never duplicated;
// and the old-named backup still holds the user's original, so no new backup is taken.
static void LegacyHooks() => WithHome(home =>
{
    const string oldExe = @"C:\Users\me\AppData\Local\WormsCursor\current\WormsCursor.exe";
    string Escaped(string path) => JsonSerializer.Serialize(path)[1..^1];
    string exe = Environment.ProcessPath!;
    const string userHook = "{\"type\":\"command\",\"command\":\"my-notify.ps1\"}";

    // Claude, exec form (the last WormsCursor): today's shape with the old exe.
    string claude = AgentHookRegistrar.ClaudePath;
    AgentHookRegistrar.Register("claude-code");
    string current = File.ReadAllText(claude);
    File.Delete(claude + ".agentscursor.bak");
    var execForm = JsonNode.Parse(current.Replace(Escaped(exe), Escaped(oldExe)))!;
    execForm["hooks"]!["Stop"]![0]!["hooks"]!.AsArray().Add(JsonNode.Parse(userHook));
    File.WriteAllText(claude, execForm.ToJsonString());
    File.WriteAllText(claude + ".wormscursor.bak", "{\"original\":true}");
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("claude-code"));
    AgentHookRegistrar.Register("claude-code");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("claude-code"));
    string refreshed = File.ReadAllText(claude);
    Equal(false, refreshed.Contains("WormsCursor.exe"));
    Equal(true, refreshed.Contains("my-notify.ps1"));
    Equal(Handlers(current) + 1, Handlers(refreshed)); // each event still has exactly one of ours
    Equal(false, File.Exists(claude + ".agentscursor.bak"));
    Equal("{\"original\":true}", File.ReadAllText(claude + ".wormscursor.bak"));

    // Claude, shell form (earlier WormsCursor versions): the quoted old exe and the marker in one string.
    var shellForm = JsonNode.Parse(current)!;
    foreach (var ev in shellForm["hooks"]!.AsObject())
        foreach (var group in ev.Value!.AsArray())
            group!["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command", ["command"] = $"\"{oldExe}\" hook --tool claude-code",
            });
    File.WriteAllText(claude, shellForm.ToJsonString());
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("claude-code"));
    AgentHookRegistrar.Register("claude-code");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("claude-code"));
    Equal(current, File.ReadAllText(claude));
    AgentHookRegistrar.Unregister("claude-code");
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("claude-code"));

    // Codex: both command forms pointing at the old exe, beside another tool's hook in the same group.
    string codex = AgentHookRegistrar.CodexPath;
    AgentHookRegistrar.Register("codex");
    string codexCurrent = File.ReadAllText(codex);
    string oldCodexText = codexCurrent.Replace(Escaped(exe), Escaped(oldExe));
    Equal(true, oldCodexText.Contains("WormsCursor.exe' hook --tool codex")); // commandWindows
    Equal(true, oldCodexText.Contains(@"WormsCursor.exe\"" hook --tool codex")); // command, JSON-escaped
    var oldCodex = JsonNode.Parse(oldCodexText)!;
    oldCodex["hooks"]!["Stop"]![0]!["hooks"]!.AsArray().Insert(0, JsonNode.Parse(userHook));
    File.WriteAllText(codex, oldCodex.ToJsonString());
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("codex"));
    AgentHookRegistrar.Register("codex");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("codex"));
    string codexRefreshed = File.ReadAllText(codex);
    Equal(false, codexRefreshed.Contains("WormsCursor.exe"));
    Equal(Handlers(codexCurrent) + 1, Handlers(codexRefreshed));
    var stop = JsonNode.Parse(codexRefreshed)!["hooks"]!["Stop"]!.AsArray();
    Equal(1, stop.Count); // refreshed inside the shared group, not added as a second one
    Equal("my-notify.ps1", stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
});

// Every hook handler in a config, ours and anyone else's.
static int Handlers(string json)
    => JsonNode.Parse(json)!["hooks"]!.AsObject().Sum(ev => ev.Value!.AsArray().Sum(g => g!["hooks"]!.AsArray().Count));

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
                var tool = IconCatalog.Tools[frames % IconCatalog.Tools.Count];
                renderer.DrawToken(g, settings, new TokenFace(tool, Blocked: true, Others: 1), 128f, 128f, 96f, 0f, 1f);
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

    // Resetting size and position leaves the switch, the timeout, the program list and the icons alone.
    live.Change(s => { s.Placement = NotifierPlacement.Corner; s.Corner = ScreenCorner.TopLeft; s.SuppressedPrograms = new[] { "cs2.exe" }; });
    live.Change(s => s.Icons = IconChoices.With(s.Icons, "pi", "pi.shades"));
    Equal(false, live.Live.HasDefaultSizeAndPosition());
    live.Change(s => s.ResetSizeAndPosition());
    Equal(true, live.Live.HasDefaultSizeAndPosition());
    Equal(NotifierPlacement.BelowPointer, live.Live.Placement);
    Equal("pi.shades", live.Live.IconFor("pi"));
    Equal(false, live.Live.AgentNotifierEnabled);
    Equal(CursorSettings.MinTimeoutSeconds, live.Live.AgentNotifierTimeoutSeconds);
    Equal("cs2.exe", Joined(live.Live.SuppressedPrograms));

    // A method, so it never lands in settings.json.
    Equal(false, JsonSerializer.Serialize(live.Live).Contains("Appearance"));
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
    const string exe = "C:\\Users\\me\\AppData\\Local\\AgentsCursor\\current\\AgentsCursor.exe";
    Equal(true, RegistryAutostart.LaunchesExe("\"" + exe + "\"", exe));
    Equal(true, RegistryAutostart.LaunchesExe(exe.ToUpperInvariant(), exe));
    Equal(false, RegistryAutostart.LaunchesExe("\"C:\\src\\AgentsCursor\\bin\\Release\\AgentsCursor.exe\"", exe));
    Equal(false, RegistryAutostart.LaunchesExe("\"\"", exe));
    Equal(false, RegistryAutostart.LaunchesExe("\"" + exe + "\"", null));
}

static void AutostartMigration()
{
    const string exe = @"C:\Users\me\AppData\Local\AgentsCursor\current\AgentsCursor.exe";
    const string oldCommand = "\"C:\\Users\\me\\AppData\\Local\\WormsCursor\\current\\WormsCursor.exe\"";
    byte[] Flag(byte state) => new byte[] { state, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    // On: our entry replaces it, launching the running exe; a second run finds nothing to do.
    var on = new FakeStartupEntries();
    on.Commands["WormsCursor"] = oldCommand;
    var autostart = new RegistryAutostart(on, exe);
    Equal(false, autostart.IsEnabled);
    Equal(true, autostart.MigrateLegacy());
    Equal("WormsCursor:-|AgentsCursor:\"" + exe + "\"", on.Show("WormsCursor", "AgentsCursor"));
    Equal(true, autostart.IsEnabled);
    Equal(false, autostart.MigrateLegacy());

    // Explicitly approved in Task Manager counts as on too, and its flag goes with it.
    var approved = new FakeStartupEntries();
    approved.Commands["WormsCursor"] = oldCommand;
    approved.Approvals["WormsCursor"] = Flag(2);
    Equal(true, new RegistryAutostart(approved, exe).MigrateLegacy());
    Equal(true, new RegistryAutostart(approved, exe).IsEnabled);
    Equal(0, approved.Approvals.Count);

    // Switched off in Task Manager: removed, flag and all, and not carried over.
    var off = new FakeStartupEntries();
    off.Commands["WormsCursor"] = oldCommand;
    off.Approvals["WormsCursor"] = Flag(3);
    Equal(true, new RegistryAutostart(off, exe).MigrateLegacy());
    Equal(0, off.Commands.Count);
    Equal(0, off.Approvals.Count);

    // No WormsCursor entry: nothing changes, not even our own entry the user switched off.
    var none = new FakeStartupEntries();
    none.Commands["AgentsCursor"] = "\"" + exe + "\"";
    none.Approvals["AgentsCursor"] = Flag(3);
    var mine = new RegistryAutostart(none, exe);
    Equal(false, mine.MigrateLegacy());
    Equal(false, mine.IsEnabled);
    Equal(1, none.Approvals.Count);

    // Enable writes the quoted exe and clears a "disabled" flag; Disable removes the entry.
    mine.Enable();
    Equal(true, mine.IsEnabled);
    Equal(0, none.Approvals.Count);
    mine.Disable();
    Equal(false, mine.IsEnabled);
    Equal(0, none.Commands.Count);
}

static void SettingsLegacyLoad() => WithHome(home =>
{
    string target = Path.Combine(home, "AgentsCursor", "settings.json");
    string legacy = Path.Combine(home, "WormsCursor", "settings.json");
    Equal("AgentsCursor", Path.GetFileName(Path.GetDirectoryName(SettingsStore.DefaultPath)));
    Equal("WormsCursor", Path.GetFileName(Path.GetDirectoryName(SettingsStore.LegacyPath)));

    // Neither: the defaults, and nothing is written.
    Equal(96, SettingsStore.Load(target, legacy).Size);
    Equal(false, File.Exists(target));

    // Only the old one: its values are read, and it is left exactly as it was (nothing is copied).
    Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
    string old = "{\"Size\": 150, \"SuppressedPrograms\": [\"cs2.exe\"]}";
    File.WriteAllText(legacy, old);
    var loaded = SettingsStore.Load(target, legacy);
    Equal(150, loaded.Size);
    Equal("cs2.exe", Joined(loaded.SuppressedPrograms));
    Equal(false, File.Exists(target));
    Equal(old, File.ReadAllText(legacy));

    // The first save writes the new file, and from then on that is what loads.
    loaded.Size = 160;
    SettingsStore.Save(loaded, target);
    Equal(160, SettingsStore.Load(target, legacy).Size);
    Equal(old, File.ReadAllText(legacy));

    // Both: the new one wins.
    Equal(160, SettingsStore.Load(target, legacy).Size);

    // A corrupt new file never brings the old settings back: the defaults load.
    File.WriteAllText(target, "{ not json");
    Equal(96, SettingsStore.Load(target, legacy).Size);

    // A caller naming its own path doesn't fall back to WormsCursor's real file.
    Equal(96, SettingsStore.Load(Path.Combine(home, "elsewhere.json")).Size);
});

static void ThemeSetting() => WithHome(home =>
{
    string path = Path.Combine(home, "settings.json");
    Equal(ThemePreference.System, new CursorSettings().Theme);
    File.WriteAllText(path, "{\"Size\": 120}"); // a file from before the setting
    Equal(ThemePreference.System, SettingsStore.Load(path).Theme);

    SettingsStore.Save(new CursorSettings { Theme = ThemePreference.Dark }, path);
    Equal(true, File.ReadAllText(path).Contains("\"Theme\": \"Dark\""));
    Equal(ThemePreference.Dark, SettingsStore.Load(path).Theme);
    File.WriteAllText(path, "{\"Theme\": 1}");
    Equal(ThemePreference.Light, SettingsStore.Load(path).Theme);
    File.WriteAllText(path, "{\"Theme\": 7}"); // out of range: back to following Windows
    Equal(ThemePreference.System, SettingsStore.Load(path).Theme);

    // Resetting the token's look leaves the panel's theme alone.
    var settings = new CursorSettings { Theme = ThemePreference.Light, Size = 150 };
    settings.ResetSizeAndPosition();
    Equal(ThemePreference.Light, settings.Theme);
    Equal(true, settings.HasDefaultSizeAndPosition());
});

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

    settings.SuppressedPrograms = new[] { "dota2.exe", "cs2.exe" };
    SettingsStore.Save(settings, path);
    Equal("dota2.exe|cs2.exe", Joined(SettingsStore.Load(path).SuppressedPrograms));

    // Hand edits load sanely: null means none; paths, missing ".exe" and duplicates normalize.
    File.WriteAllText(path, "{\"SuppressedPrograms\": null}");
    Equal(0, SettingsStore.Load(path).SuppressedPrograms.Count);
    File.WriteAllText(path, """{"SuppressedPrograms": ["C:\\Games\\Dota2", "dota2.exe", ""]}""");
    Equal("Dota2.exe", Joined(SettingsStore.Load(path).SuppressedPrograms));
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

// ---------- the pipe: hooks and persistent plugin connections ----------

static string PrivatePipe() => "AgentsCursorTest." + Guid.NewGuid().ToString("N");

static string Line(string tool, string ev, string? session = null, params string[] items)
    => new AgentEventMessage { Tool = tool, Event = ev, SessionId = session, InputIds = items.Length > 0 ? items : null }.ToJsonLine();

static NamedPipeClientStream Connect(string pipe)
{
    var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
    client.Connect(2_000);
    return client;
}

static void WaitUntil(Func<bool> condition, string what)
{
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (!condition())
    {
        if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Timed out waiting for " + what + ".");
        Thread.Sleep(10);
    }
}

static void PipeConcurrentClients()
{
    string pipe = PrivatePipe();
    var received = new ConcurrentQueue<(AgentEventMessage Message, long? Lease)>();
    var released = new ConcurrentQueue<long>();
    using var server = new AgentPipeServer((m, lease) => received.Enqueue((m, lease)), released.Enqueue, pipe);
    server.Start();

    // A plugin host holds its connection open for its whole life...
    using var plugin = Connect(pipe);
    using var writer = new StreamWriter(plugin) { AutoFlush = true };
    writer.WriteLine(Line("pi", AgentEventMessage.AttachEvent));
    writer.WriteLine(Line("pi", "awaiting_user", "p", "ui:1"));

    // ...while 20 throwaway hooks connect at once, each well inside the bridge's 500 ms budget.
    var hooks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
    {
        var clock = Stopwatch.StartNew();
        using var hook = Connect(pipe);
        long connectMs = clock.ElapsedMilliseconds;
        using var w = new StreamWriter(hook) { AutoFlush = true };
        w.WriteLine(Line("claude-code", "turn_complete", "hook-" + i));
        return connectMs;
    })).ToArray();
    Task.WaitAll(hooks);
    foreach (var hook in hooks)
        if (hook.Result >= 500) throw new InvalidOperationException($"A hook took {hook.Result} ms to connect.");

    for (int i = 2; i <= 6; i++) writer.WriteLine(Line("pi", "input_answered", "p", "ui:" + i));
    WaitUntil(() => received.Count == 26, "every message");

    var fromPlugin = received.Where(r => r.Message.Tool == "pi").ToArray();
    Equal(1, fromPlugin.Select(r => r.Lease).Distinct().Count());
    Equal(true, fromPlugin[0].Lease is long);
    Equal("ui:1,ui:2,ui:3,ui:4,ui:5,ui:6", string.Join(',', fromPlugin.Select(r => r.Message.InputIds![0])));
    Equal(20, received.Count(r => r.Message.Tool == "claude-code" && r.Lease is null));
    Equal(false, received.Any(r => r.Message.Event == AgentEventMessage.AttachEvent)); // consumed by the server
    Equal(0, released.Count); // hooks hold no lease, and the plugin is still connected
}

static void PipeLeaseRelease()
{
    string pipe = PrivatePipe();
    var now = DateTime.UtcNow;
    var activity = new AgentActivity(TimeSpan.FromSeconds(10));
    var released = new ConcurrentQueue<long>();
    var gate = new object();
    // As the tray does: the message's lease becomes the session's owner, a release drops its sessions.
    using var server = new AgentPipeServer(
        (m, lease) => { lock (gate) { m.TryGetKind(out var kind); activity.Report(m.Tool, m.SessionId, m.Project, kind, now, m.InputIds, owner: lease?.ToString()); } },
        lease => { lock (gate) activity.Release(lease.ToString()); released.Enqueue(lease); },
        pipe);
    server.Start();

    var pi = Connect(pipe);
    var piWriter = new StreamWriter(pi) { AutoFlush = true };
    piWriter.WriteLine(Line("pi", AgentEventMessage.AttachEvent));
    piWriter.WriteLine(Line("pi", "awaiting_user", "pi-session", "ui:1"));

    using var openCode = Connect(pipe);
    using var openCodeWriter = new StreamWriter(openCode) { AutoFlush = true };
    openCodeWriter.WriteLine(Line("opencode", AgentEventMessage.AttachEvent));
    openCodeWriter.WriteLine(Line("opencode", "turn_complete", "root"));

    using (var hook = Connect(pipe))
    using (var w = new StreamWriter(hook) { AutoFlush = true })
        w.WriteLine(Line("codex", "awaiting_user", "codex-session"));

    WaitUntil(() => activity.WaitingCount == 3, "three waiting sessions");
    Equal(0, released.Count); // the hook's disconnect means nothing

    // Pi is killed while blocked on you: its connection closes, and only its session goes.
    pi.Dispose();
    WaitUntil(() => released.Count == 1, "the release");
    Equal("codex:Blocked,opencode:Finished", string.Join(',', activity.Waiting.Select(w => $"{w.Tool}:{w.State}").Order()));
}

static void PipeOversizedLine()
{
    string pipe = PrivatePipe();
    var received = new ConcurrentQueue<AgentEventMessage>();
    var released = new ConcurrentQueue<long>();
    using var server = new AgentPipeServer((m, _) => received.Enqueue(m), released.Enqueue, pipe);
    server.Start();

    using var steady = Connect(pipe);
    using var steadyWriter = new StreamWriter(steady) { AutoFlush = true };
    steadyWriter.WriteLine(Line("opencode", AgentEventMessage.AttachEvent));
    steadyWriter.WriteLine("{ not json");
    steadyWriter.WriteLine(Line("opencode", "turn_complete", "a"));

    using var flood = Connect(pipe);
    var floodWriter = new StreamWriter(flood) { AutoFlush = true };
    floodWriter.WriteLine(Line("pi", AgentEventMessage.AttachEvent));
    bool cut = false;
    try { floodWriter.WriteLine(new string('x', AgentPipeServer.MaxLineBytes + 10)); floodWriter.WriteLine(Line("pi", "turn_complete", "b")); }
    catch (IOException) { cut = true; } // the server may hang up before the whole line is written
    WaitUntil(() => released.Count == 1, "the oversized connection to end");

    steadyWriter.WriteLine(Line("opencode", "turn_complete", "c"));
    WaitUntil(() => received.Count == 2, "the steady connection's messages");
    Equal("a,c", string.Join(',', received.Select(m => m.SessionId)));
    Equal(false, received.Any(m => m.Tool == "pi"));
    _ = cut;
}

static void ActivityRelease()
{
    var now = DateTime.UtcNow;
    var activity = new AgentActivity(TimeSpan.FromSeconds(10));
    var changes = new List<int>();
    activity.WaitingChanged += changes.Add;
    activity.Report("pi", "a", null, AgentEventKind.AwaitingUser, now, new[] { "ui:1" }, owner: "1");
    activity.Report("pi", "b", null, AgentEventKind.TurnComplete, now, owner: "1");
    activity.Report("opencode", "c", null, AgentEventKind.TurnComplete, now, owner: "2");
    activity.Report("claude-code", "d", null, AgentEventKind.TurnComplete, now);
    changes.Clear();

    activity.Release("unknown");
    Equal(0, changes.Count);
    activity.Release("1");
    Equal("2", string.Join(',', changes)); // once, for both of its sessions
    Equal("claude-code,opencode", string.Join(',', activity.Waiting.Select(w => w.Tool).Order()));
    activity.Release("1");
    Equal(1, changes.Count);
}

// ---------- Pi and OpenCode registration ----------

static void PiRegistration() => WithHome(home =>
{
    string path = AgentHookRegistrar.PiPath;
    Equal(Path.Combine(home, ".pi", "agent", "settings.json"), path);
    string plugin = AgentHookRegistrar.PluginDir("pi");
    Equal(true, File.Exists(Path.Combine(plugin, "index.ts"))); // shipped next to the exe
    string[] Extensions() => JsonNode.Parse(File.ReadAllText(path))!["extensions"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray();

    // No settings yet: created with just our entry, and nothing to back up.
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("pi"));
    AgentHookRegistrar.Register("pi");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("pi"));
    Equal(plugin, string.Join('|', Extensions()));
    Equal(false, File.Exists(path + ".agentscursor.bak"));

    // An older copy's entry is replaced where it was; the user's entries, order and settings stay.
    string original = JsonSerializer.Serialize(new
    {
        theme = "powerline-aura",
        extensions = new[] { "-builtin:mcp", OldCopy(home, "WormsCursor", "pi"), "./extensions/mine" },
        packages = new[] { "npm:pi-web-access" },
    });
    File.WriteAllText(path, original);
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("pi"));
    AgentHookRegistrar.Register("pi");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("pi"));
    Equal($"-builtin:mcp|{plugin}|./extensions/mine", string.Join('|', Extensions()));
    var root = JsonNode.Parse(File.ReadAllText(path))!;
    Equal("powerline-aura", root["theme"]!.GetValue<string>());
    Equal("npm:pi-web-access", root["packages"]![0]!.GetValue<string>());
    Equal(original, File.ReadAllText(path + ".agentscursor.bak"));
    string once = File.ReadAllText(path);
    AgentHookRegistrar.Register("pi");
    Equal(once, File.ReadAllText(path));
    Equal(original, File.ReadAllText(path + ".agentscursor.bak")); // the backup is taken once

    // Other forms of our entry are outdated and become today's single entry, in place.
    foreach (var entries in new[]
             {
                 new[] { "a", "+" + plugin, "b" },
                 new[] { "a", Path.Combine(plugin, "index.ts"), "b" },
                 new[] { "a", plugin.Replace('\\', '/') + "/", "b" },
                 new[] { "a", plugin, "b", plugin.ToUpperInvariant() },
             })
    {
        File.WriteAllText(path, JsonSerializer.Serialize(new { extensions = entries }));
        Equal(HookState.Outdated, AgentHookRegistrar.GetState("pi"));
        AgentHookRegistrar.Register("pi");
        Equal($"a|{plugin}|b", string.Join('|', Extensions()));
    }

    // An exclusion naming a plugin folder is the user's, not ours.
    File.WriteAllText(path, JsonSerializer.Serialize(new { extensions = new[] { "-" + plugin } }));
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("pi"));

    // Unregister removes only ours, and the list when nothing is left.
    File.WriteAllText(path, JsonSerializer.Serialize(new { extensions = new[] { "-builtin:mcp", plugin } }));
    AgentHookRegistrar.Unregister("pi");
    Equal("-builtin:mcp", string.Join('|', Extensions()));
    File.WriteAllText(path, JsonSerializer.Serialize(new { quietStartup = true, extensions = new[] { plugin } }));
    AgentHookRegistrar.Unregister("pi");
    Equal(false, JsonNode.Parse(File.ReadAllText(path))!.AsObject().ContainsKey("extensions"));
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("pi"));

    // A list that isn't a list of paths can't be merged safely: it's reported and left alone.
    foreach (string raw in new[] { "{\"extensions\": \"x\"}", "{\"extensions\": [1]}", "{", "[]" })
    {
        File.WriteAllText(path, raw);
        Equal(HookState.ConfigConflict, AgentHookRegistrar.GetState("pi"));
        foreach (Action action in new Action[] { () => AgentHookRegistrar.Register("pi"), () => AgentHookRegistrar.Unregister("pi") })
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidOperationException) { rejected = true; }
            Equal(true, rejected);
            Equal(raw, File.ReadAllText(path));
        }
    }
});

static void PiHome()
{
    string? previous = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
    try
    {
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", @"C:\custom-pi");
        Equal(@"C:\custom-pi\settings.json", AgentHookRegistrar.PiPath);
        WithHome(home => Equal(Path.Combine(home, ".pi", "agent", "settings.json"), AgentHookRegistrar.PiPath));
    }
    finally { Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", previous); }
}

static void OpenCodeRegistration() => WithHome(home =>
{
    string dir = Path.Combine(home, ".config", "opencode");
    string path = Path.Combine(dir, "opencode.jsonc");
    Equal(path, AgentHookRegistrar.OpenCodePath);
    string entry = new Uri(AgentHookRegistrar.PluginDir("opencode")).AbsoluteUri;
    string quoted = "\"" + entry + "\"";

    // No config yet: one is created with OpenCode's schema and our entry.
    AgentHookRegistrar.Register("opencode");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("opencode"));
    Equal("{\n  \"plugins\": [" + quoted + "],\n  \"$schema\": \"https://opencode.ai/config.json\"\n}\n", File.ReadAllText(path));
    File.Delete(path);

    // The old repo checkout's plugin (an object carrying the retired plugin's options) is ours but
    // outdated: it becomes the bare entry at the same position, and nothing else changes by a byte,
    // CRLF line ends, comments and trailing commas included.
    string oldPlugin = "{\r\n      \"package\": \"" + new Uri(OldCopy(home, "checkout", "opencode")).AbsoluteUri + "\",\r\n      \"options\": { \"enabled\": true, \"tokenSize\": 64 } // tuned\r\n    }";
    string Config(string plugins) => "{\r\n  // my config\r\n  \"$schema\": \"https://opencode.ai/config.json\",\r\n  \"plugins\": [\r\n    \"opencode-foo@1.0\", // pinned\r\n"
        + plugins + "    \"./plugins/mine\",\r\n  ],\r\n  /* block */ \"model\": \"anthropic/x\", // ünïcode\r\n}\r\n";
    string original = Config("    " + oldPlugin + ",\r\n");
    Directory.CreateDirectory(dir);
    File.WriteAllText(path, original);
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("opencode"));
    AgentHookRegistrar.Register("opencode");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("opencode"));
    Equal(Config("    " + quoted + ",\r\n"), File.ReadAllText(path));
    Equal(original, File.ReadAllText(path + ".agentscursor.bak"));

    // Unregister takes the element and its line out; Register then adds it last, in the list's own
    // style (one per line, trailing comma), and Unregister restores the text exactly.
    AgentHookRegistrar.Unregister("opencode");
    string without = Config("");
    Equal(without, File.ReadAllText(path));
    AgentHookRegistrar.Register("opencode");
    Equal(without.Replace("    \"./plugins/mine\",\r\n", "    \"./plugins/mine\",\r\n    " + quoted + ",\r\n"), File.ReadAllText(path));
    AgentHookRegistrar.Unregister("opencode");
    Equal(without, File.ReadAllText(path));
    Equal(original, File.ReadAllText(path + ".agentscursor.bak")); // still the first original
    File.Delete(path + ".agentscursor.bak");

    // Layouts: no plugins key (removing the only entry restores the original), an empty list, an
    // inline list, a multi-line list without a trailing comma.
    foreach (var (before, after) in new[]
             {
                 ("{\n  \"$schema\": \"x\"\n}\n", "{\n  \"plugins\": [" + quoted + "],\n  \"$schema\": \"x\"\n}\n"),
                 ("{ \"theme\": \"y\" }", "{ \"plugins\": [" + quoted + "], \"theme\": \"y\" }"),
                 ("{\n  \"plugins\": []\n}", "{\n  \"plugins\": [" + quoted + "]\n}"),
                 ("{\n  \"plugins\": [\"a\"]\n}", "{\n  \"plugins\": [\"a\", " + quoted + "]\n}"),
                 ("{\n\t\"plugins\": [\n\t\t\"a\" // first\n\t]\n}", "{\n\t\"plugins\": [\n\t\t\"a\", // first\n\t\t" + quoted + "\n\t]\n}"),
             })
    {
        File.WriteAllText(path, before);
        AgentHookRegistrar.Register("opencode");
        Equal(after, File.ReadAllText(path));
        Equal(HookState.Registered, AgentHookRegistrar.GetState("opencode"));
        AgentHookRegistrar.Unregister("opencode");
        Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("opencode"));
        if (!before.Contains("[]")) Equal(before, File.ReadAllText(path)); // an emptied list goes with the key
    }

    // Duplicates and other copies collapse into one entry, where the first one was.
    string movedCopy = new Uri(OldCopy(home, "Apps", "opencode")).AbsoluteUri + "/";
    string oldIndex = JsonSerializer.Serialize(Path.Combine(OldCopy(home, "old", "opencode"), "index.ts"));
    File.WriteAllText(path, "{\"plugins\": [\"x\", \"" + movedCopy + "\", \"y\", " + quoted + ", " + oldIndex + "]}");
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("opencode"));
    AgentHookRegistrar.Register("opencode");
    Equal("{\"plugins\": [\"x\", " + quoted + ", \"y\"]}", File.ReadAllText(path));

    // Package names and unrelated folders are never ours.
    string others = "{\"plugins\": [\"opencode-integrations-opencode\", \"file:///C:/x/integrations/opencode-extra\", {\"package\": \"@scope/opencode\"}]}";
    File.WriteAllText(path, others);
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("opencode"));
    AgentHookRegistrar.Unregister("opencode");
    Equal(others, File.ReadAllText(path));

    // A config that can't be edited safely is reported and left alone.
    foreach (string raw in new[] { "{", "[1]", "{\"plugins\": {}}", "{\"plugins\": \"x\"}", "{\"plugins\": null, \"plugins\": []}", "{\"plugins\": [], \"plugins\": []}" })
    {
        File.WriteAllText(path, raw);
        Equal(HookState.ConfigConflict, AgentHookRegistrar.GetState("opencode"));
        foreach (Action action in new Action[] { () => AgentHookRegistrar.Register("opencode"), () => AgentHookRegistrar.Unregister("opencode") })
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidOperationException) { rejected = true; }
            Equal(true, rejected);
            Equal(raw, File.ReadAllText(path));
        }
    }

    // With only opencode.json, that is the file used (and no .jsonc appears).
    File.Delete(path);
    string json = Path.Combine(dir, "opencode.json");
    File.WriteAllText(json, "{\"plugins\": []}");
    Equal(json, AgentHookRegistrar.OpenCodePath);
    AgentHookRegistrar.Register("opencode");
    Equal("{\"plugins\": [" + quoted + "]}", File.ReadAllText(json));
    Equal(false, File.Exists(path));

    // A UTF-8 byte order mark is kept.
    File.WriteAllText(json, "{\"plugins\": []}", new System.Text.UTF8Encoding(true));
    AgentHookRegistrar.Register("opencode");
    Equal(true, File.ReadAllBytes(json).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
});

static void OpenCodeHardLink() => WithHome(home =>
{
    // Orca's OpenCode overlay is a hard link to the user's config: a write must go through the link.
    string path = AgentHookRegistrar.OpenCodePath;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "{\n  // mine\n  \"plugins\": []\n}\n");
    string overlay = Path.Combine(home, "orca", "opencode-config-overlays", "hash", "opencode.jsonc");
    Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
    if (!HardLinks.Create(overlay, path)) throw new InvalidOperationException("Couldn't create a hard link: " + Marshal.GetLastWin32Error());
    AgentHookRegistrar.Register("opencode");
    string written = File.ReadAllText(path);
    Equal(true, written.Contains(new Uri(AgentHookRegistrar.PluginDir("opencode")).AbsoluteUri));
    Equal(written, File.ReadAllText(overlay));
    AgentHookRegistrar.Unregister("opencode");
    Equal(File.ReadAllText(path), File.ReadAllText(overlay));
});

static void RefreshConnectedOnly() => WithHome(home =>
{
    // Claude: connected and current. Codex: connected from an old exe path. OpenCode: the old repo
    // checkout's plugin. Pi: not connected.
    AgentHookRegistrar.Register("claude-code");
    string claude = AgentHookRegistrar.ClaudePath;
    File.Delete(claude + ".agentscursor.bak");
    var claudeTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(claude, claudeTime);
    string claudeBytes = File.ReadAllText(claude);

    AgentHookRegistrar.Register("codex");
    string codex = AgentHookRegistrar.CodexPath;
    string exe = JsonSerializer.Serialize(AgentHookRegistrar.OnDisk(Environment.ProcessPath!))[1..^1];
    File.WriteAllText(codex, File.ReadAllText(codex).Replace(exe, @"C:\\Users\\me\\AppData\\Local\\WormsCursor\\current\\WormsCursor.exe"));
    File.Delete(codex + ".agentscursor.bak");
    Equal(HookState.Outdated, AgentHookRegistrar.GetState("codex"));

    string openCode = AgentHookRegistrar.OpenCodePath;
    Directory.CreateDirectory(Path.GetDirectoryName(openCode)!);
    File.WriteAllText(openCode, "{\"plugins\": [\"" + new Uri(OldCopy(home, "src", "opencode")).AbsoluteUri + "\"]}");

    string pi = AgentHookRegistrar.PiPath;
    Directory.CreateDirectory(Path.GetDirectoryName(pi)!);
    string piText = "{\"extensions\": [\"-builtin:mcp\"]}";
    File.WriteAllText(pi, piText);

    var results = AgentHookRegistrar.RefreshConnected();
    Equal("codex:,opencode:", string.Join(',', results.Select(r => $"{r.Tool.Id}:{r.Error}")));
    foreach (var tool in AgentHookRegistrar.Tools)
        Equal(tool.Id == "pi" ? HookState.NotRegistered : HookState.Registered, AgentHookRegistrar.GetState(tool.Id));
    // Untouched: not a byte, not the write time, no backup.
    Equal(claudeBytes, File.ReadAllText(claude));
    Equal(claudeTime, File.GetLastWriteTimeUtc(claude));
    Equal(false, File.Exists(claude + ".agentscursor.bak"));
    Equal(piText, File.ReadAllText(pi));
    Equal(false, File.Exists(pi + ".agentscursor.bak"));
    // A second start finds nothing to do.
    Equal(0, AgentHookRegistrar.RefreshConnected().Count);

    // An unreadable config stays as it is; a write that fails is reported for that tool only.
    File.WriteAllText(claude, "{ broken");
    File.WriteAllText(pi, JsonSerializer.Serialize(new { extensions = new[] { OldCopy(home, "old", "pi") } }));
    File.SetAttributes(pi, FileAttributes.ReadOnly);
    try
    {
        var failed = AgentHookRegistrar.RefreshConnected();
        Equal(1, failed.Count);
        Equal("pi", failed[0].Tool.Id);
        Equal(true, failed[0].Error is { Length: > 0 });
        Equal(HookState.Outdated, AgentHookRegistrar.GetState("pi"));
        Equal("{ broken", File.ReadAllText(claude));
        Equal(HookState.ConfigConflict, AgentHookRegistrar.GetState("claude-code"));
    }
    finally { File.SetAttributes(pi, FileAttributes.Normal); }
});

// A copy of AgentsCursor's plugin for <tool> in <home>\<name>\integrations\<tool>, as an older or
// moved install leaves it: the folder with our package.json.
static string OldCopy(string home, string name, string tool)
{
    string dir = Path.Combine(home, name, "integrations", tool);
    Directory.CreateDirectory(dir);
    File.Copy(Path.Combine(AgentHookRegistrar.PluginDir(tool), "package.json"), Path.Combine(dir, "package.json"), overwrite: true);
    return dir;
}

static void ForeignPluginFolders() => WithHome(home =>
{
    // Someone else's plugins that happen to live in folders named integrations\pi and
    // integrations\opencode, one with its own package.json and one with none, plus a folder that is
    // gone: none is ours, so nothing reads as connected and the startup refresh writes nothing.
    string theirs = Path.Combine(home, "work", "acme", "integrations");
    Directory.CreateDirectory(Path.Combine(theirs, "pi"));
    File.WriteAllText(Path.Combine(theirs, "pi", "package.json"), "{\"name\": \"acme-pi\"}");
    Directory.CreateDirectory(Path.Combine(theirs, "opencode"));
    File.WriteAllText(Path.Combine(theirs, "opencode", "index.ts"), "export default {}");
    string gone = Path.Combine(home, "gone", "integrations");

    string pi = AgentHookRegistrar.PiPath, openCode = AgentHookRegistrar.OpenCodePath;
    Directory.CreateDirectory(Path.GetDirectoryName(pi)!);
    Directory.CreateDirectory(Path.GetDirectoryName(openCode)!);
    string piText = JsonSerializer.Serialize(new { extensions = new[] { Path.Combine(theirs, "pi"), Path.Combine(gone, "pi") } });
    string openCodeText = "{\"plugins\": [\"" + new Uri(Path.Combine(theirs, "opencode")).AbsoluteUri + "\", \""
        + new Uri(Path.Combine(gone, "opencode")).AbsoluteUri + "\"]}";
    File.WriteAllText(pi, piText);
    File.WriteAllText(openCode, openCodeText);

    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("pi"));
    Equal(HookState.NotRegistered, AgentHookRegistrar.GetState("opencode"));
    Equal(0, AgentHookRegistrar.RefreshConnected().Count);
    Equal(piText, File.ReadAllText(pi));
    Equal(openCodeText, File.ReadAllText(openCode));
    Equal(false, File.Exists(pi + ".agentscursor.bak"));

    // Connecting adds ours beside theirs; disconnecting takes only ours away again.
    AgentHookRegistrar.Register("pi");
    AgentHookRegistrar.Register("opencode");
    Equal(HookState.Registered, AgentHookRegistrar.GetState("pi"));
    Equal(HookState.Registered, AgentHookRegistrar.GetState("opencode"));
    Equal(3, JsonNode.Parse(File.ReadAllText(pi))!["extensions"]!.AsArray().Count);
    AgentHookRegistrar.Unregister("pi");
    AgentHookRegistrar.Unregister("opencode");
    Equal(JsonNode.Parse(piText)!["extensions"]!.ToJsonString(), JsonNode.Parse(File.ReadAllText(pi))!["extensions"]!.ToJsonString());
    Equal(openCodeText, File.ReadAllText(openCode));
});

static void OnDiskPaths() => WithHome(home =>
{
    string dir = Path.Combine(home, "MixedCase", "Sub Folder");
    Directory.CreateDirectory(dir);
    string file = Path.Combine(dir, "AgentsCursor.exe");
    File.WriteAllText(file, "");
    string typed = char.ToLowerInvariant(file[0]) + file[1..].ToLowerInvariant();
    Equal(file, AgentHookRegistrar.OnDisk(typed));
    Equal(file, AgentHookRegistrar.OnDisk(file.Replace("Sub Folder", "sub folder\\.\\")));
    // From the first part that doesn't exist, the rest is kept as given.
    Equal(Path.Combine(dir, "Not", "There.exe"), AgentHookRegistrar.OnDisk(Path.Combine(dir.ToUpperInvariant(), "Not", "There.exe")));
});

// ---------- icons ----------

static void BuiltInIcons()
{
    Equal("claude-code,codex,opencode,pi", string.Join(',', IconCatalog.Tools));
    using var sprites = new TokenSprites();
    foreach (string tool in IconCatalog.Tools)
    {
        var icon = IconCatalog.Original(tool)!;
        Equal($"{tool}.original", icon.Id);
        foreach (int side in new[] { 48, 96 })
        {
            var sprite = sprites.Get(tool, icon.Id, side, NotifierRenderer.RimWidth(side, 1f)) ?? throw new InvalidOperationException($"{tool} has no sprite.");
            var v = sprite.Visual;
            // Fitted by its visible pixels: it fills the box along its longer side, and stays inside.
            Equal(true, v.X >= -0.01f && v.Y >= -0.01f && v.Right <= side + 0.01f && v.Bottom <= side + 0.01f);
            Equal(true, Math.Max(v.Width, v.Height) >= side - 0.5f);
            int opaque = 0;
            for (int y = 0; y < sprite.Image.Height; y++)
                for (int x = 0; x < sprite.Image.Width; x++)
                    if (sprite.Image.GetPixel(x, y).A > 200) opaque++;
            Equal(true, opaque > side * side / 8);
            // The badge sits on the icon's top-right.
            var (center, _) = StatusBadge.Place(new RectangleF(v.X, v.Y, v.Width, v.Height), side);
            Equal(true, center.X > v.X + v.Width / 2 && center.Y < v.Y + v.Height / 2);
        }
    }
    Equal<IconInfo?>(null, IconCatalog.Original("mystery"));
    Equal<TokenSprites.Sprite?>(null, sprites.Get("mystery", "mystery.original", 48, 3));
}

/// <summary>Creates hard links, as Orca does for its OpenCode config overlay.</summary>
static class HardLinks
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string link, string existing, IntPtr security);

    public static bool Create(string link, string existing) => CreateHardLink(link, existing, IntPtr.Zero);
}

/// <summary>In-memory Run / StartupApproved entries for the autostart tests.</summary>
sealed class FakeStartupEntries : IStartupEntries
{
    public Dictionary<string, string> Commands { get; } = new();
    public Dictionary<string, byte[]> Approvals { get; } = new();

    public string? GetCommand(string name) => Commands.GetValueOrDefault(name);
    public void SetCommand(string name, string command) => Commands[name] = command;
    public void DeleteCommand(string name) => Commands.Remove(name);
    public byte[]? GetApproval(string name) => Approvals.GetValueOrDefault(name);
    public void DeleteApproval(string name) => Approvals.Remove(name);

    /// <summary>"name:command" for each name, "-" when it has none.</summary>
    public string Show(params string[] names)
        => string.Join('|', names.Select(n => n + ":" + (Commands.GetValueOrDefault(n) ?? "-")));
}
