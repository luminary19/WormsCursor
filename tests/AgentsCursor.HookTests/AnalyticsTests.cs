using AgentsCursor.Core;

/// <summary>The regression groups for the Analytics page: the waits the activity model reports, the
/// history store (round trip, torn and corrupt lines, retention), and the metrics (streaks, typical
/// response time, per-agent counts, day bucketing across daylight saving).</summary>
static class AnalyticsTests
{
    static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{what}{(what is null ? "" : ": ")}expected '{expected}', got '{actual}'.");
    }

    static void True(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException(what);
    }

    static readonly DateTime T0 = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AgentsCursor.HookTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string Describe(WaitRecord r) => $"{r.Tool}:{r.Kind}:{(int)(r.EndUtc - r.StartUtc).TotalSeconds}s:{r.Outcome}";

    // ---------- the waits AgentActivity reports ----------

    public static void ActivityWaits()
    {
        var activity = new AgentActivity(TimeSpan.FromSeconds(30));
        var ended = new List<WaitRecord>();
        activity.WaitEnded += ended.Add;
        string Take() { var s = string.Join(',', ended.Select(Describe)); ended.Clear(); return s; }

        // Blocked, then answered: one needs-you wait, timed from when it started blocking to the last
        // answer (two questions, each finishing as it is answered: no run time after the answer).
        activity.Report("claude-code", "a", null, AgentEventKind.AwaitingUser, T0, callIds: new[] { "c1" });
        activity.Report("claude-code", "a", null, AgentEventKind.AwaitingUser, T0.AddSeconds(5), callIds: new[] { "c2" });
        activity.Report("claude-code", "a", null, AgentEventKind.ToolUse, T0.AddSeconds(8), callIds: new[] { "c1" }, callRan: TimeSpan.Zero);
        Equal("", Take(), "still blocked on the second call: no wait ends");
        activity.Report("claude-code", "a", null, AgentEventKind.ToolUse, T0.AddSeconds(20), callIds: new[] { "c2" }, callRan: TimeSpan.Zero);
        Equal("claude-code:Blocked:20s:Answered", Take(), "both calls answered");
        // You answering yourself (a prompt, an item) is the answer moment.
        activity.Report("claude-code", "a", null, AgentEventKind.AwaitingUser, T0, callIds: new[] { "c1" });
        activity.Report("claude-code", "a", null, AgentEventKind.ThinkingStarted, T0.AddSeconds(6));
        Equal("claude-code:Blocked:6s:Answered", Take(), "a new prompt answers");
        activity.Report("claude-code", "a", null, AgentEventKind.AwaitingUser, T0, new[] { "form" });
        activity.Report("claude-code", "a", null, AgentEventKind.InputAnswered, T0.AddSeconds(9), new[] { "form" });
        Equal("claude-code:Blocked:9s:Answered", Take(), "an item answered");

        // Done, then a new prompt: a done wait that resumed. Repeated finishes keep one wait.
        activity.Report("codex", "b", null, AgentEventKind.TurnComplete, T0);
        activity.Report("codex", "b", null, AgentEventKind.TurnComplete, T0.AddSeconds(10));
        activity.Report("codex", "b", null, AgentEventKind.ThinkingStarted, T0.AddSeconds(25));
        Equal("codex:Finished:25s:Resumed", Take(), "done, then resumed");

        // Done, then swept by the linger timeout: expired.
        activity.Report("codex", "c", null, AgentEventKind.TurnComplete, T0);
        activity.Sweep(T0.AddSeconds(31));
        Equal("codex:Finished:31s:Expired", Take(), "done, then swept");

        // An approval answered at +5 s, then the approved call ran for 90 s: the wait ends when the
        // call started (its reported run time back from when it finished), not when it was done.
        activity.Report("claude-code", "h", null, AgentEventKind.AwaitingUser, T0, callIds: new[] { "c" });
        activity.Report("claude-code", "h", null, AgentEventKind.ToolUse, T0.AddSeconds(95), callIds: new[] { "c" }, callRan: TimeSpan.FromSeconds(90));
        Equal("claude-code:Blocked:5s:Answered", Take(), "answered when the approved call started");
        // A run time longer than the wait (clock skew) never ends it before it began; none: now.
        activity.Report("claude-code", "h", null, AgentEventKind.AwaitingUser, T0, callIds: new[] { "c" });
        activity.Report("claude-code", "h", null, AgentEventKind.ToolUse, T0.AddSeconds(10), callIds: new[] { "c" }, callRan: TimeSpan.FromMinutes(5));
        Equal("claude-code:Blocked:0s:Answered", Take(), "clamped to the wait's start");
        activity.Report("claude-code", "h", null, AgentEventKind.AwaitingUser, T0, callIds: new[] { "c" });
        activity.Report("claude-code", "h", null, AgentEventKind.ToolUse, T0.AddSeconds(10), callIds: new[] { "c" });
        Equal("claude-code:Blocked:10s:Proceeded", Take(), "no run time: answered at an unknown moment, untimed");
        // A prompt that named no call (Codex's approval) is answered by the agent's next tool event,
        // which says nothing of when, even when that event carries a run time of its own.
        activity.Report("codex", "h", null, AgentEventKind.AwaitingUser, T0);
        activity.Report("codex", "h", null, AgentEventKind.ToolUse, T0.AddSeconds(50), callIds: new[] { "x" }, callRan: TimeSpan.Zero);
        Equal("codex:Blocked:50s:Proceeded", Take(), "an unnamed prompt is never timed");
        // A run time on an event that doesn't end the wait changes nothing.
        activity.Report("claude-code", "h", null, AgentEventKind.AwaitingUser, T0, callIds: new[] { "c", "d" });
        activity.Report("claude-code", "h", null, AgentEventKind.ToolUse, T0.AddSeconds(10), callIds: new[] { "c" }, callRan: TimeSpan.FromSeconds(9));
        activity.Report("claude-code", "h", null, AgentEventKind.ToolsResolved, T0.AddSeconds(30));
        Equal("claude-code:Blocked:30s:Proceeded", Take(), "the batch resolving (a denial) is untimed");
        // The turn failing under a prompt is no answer.
        activity.Report("claude-code", "h", null, AgentEventKind.AwaitingUser, T0, callIds: new[] { "c" });
        activity.Report("claude-code", "h", null, AgentEventKind.Error, T0.AddSeconds(7));
        Equal("claude-code:Blocked:7s:Ended", Take(), "an error ends the needs-you wait unanswered");
        activity.Report("claude-code", "h", null, AgentEventKind.SessionEnded, T0.AddSeconds(8));
        Equal("claude-code:Finished:1s:Ended", Take(), "then the error's done wait");

        // Blocked, then the turn ends: the blocked wait was answered and a done wait begins.
        activity.Report("claude-code", "d", null, AgentEventKind.AwaitingUser, T0);
        activity.Report("claude-code", "d", null, AgentEventKind.TurnComplete, T0.AddSeconds(4));
        Equal("claude-code:Blocked:4s:Proceeded", Take(), "blocked -> finished ends the blocked wait, untimed");
        // Done, then a question: the done wait ends (resumed) and a blocked one begins.
        activity.Report("claude-code", "d", null, AgentEventKind.InputRequested, T0.AddSeconds(10), new[] { "q" });
        Equal("claude-code:Finished:6s:Resumed", Take(), "finished -> blocked ends the done wait");
        // The session ends while blocked: ended, not answered.
        activity.Report("claude-code", "d", null, AgentEventKind.SessionEnded, T0.AddSeconds(70));
        Equal("claude-code:Blocked:60s:Ended", Take(), "a session ending is not an answer");
        // A session that isn't waiting ends without a record.
        activity.Report("pi", "e", null, AgentEventKind.ThinkingStarted, T0);
        activity.Report("pi", "e", null, AgentEventKind.SessionEnded, T0.AddSeconds(1));
        Equal("", Take(), "nothing was waiting");

        // A plugin connection closing ends its own waits; the tray stopping ends the rest.
        activity.Report("opencode", "f", null, AgentEventKind.AwaitingUser, T0, owner: "lease:1");
        activity.Report("pi", "g", null, AgentEventKind.TurnComplete, T0, owner: "lease:2");
        activity.Release("lease:1", T0.AddSeconds(3));
        Equal("opencode:Blocked:3s:Ended", Take(), "released");
        activity.EndAll(T0.AddSeconds(9));
        Equal("pi:Finished:9s:Ended", Take(), "the tray stopped");
        Equal(0, activity.WaitingCount, "EndAll leaves nothing waiting");

        // A blocked session is never swept, so it never expires.
        activity.Report("codex", "h", null, AgentEventKind.AwaitingUser, T0);
        activity.Sweep(T0.AddHours(5));
        Equal("", Take(), "blocked waits don't expire");
    }

    // ---------- the store ----------

    public static void HistoryRoundTrip()
    {
        string dir = TempDir();
        try
        {
            var records = new[]
            {
                new WaitRecord("claude-code", WaitingState.Blocked, new DateTime(2026, 10, 31, 23, 59, 59, 123, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 41, 456, DateTimeKind.Utc), WaitEnd.Answered),
                new WaitRecord("codex", WaitingState.Finished, new DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 2, 8, 1, 0, DateTimeKind.Utc), WaitEnd.Expired),
                new WaitRecord("some-tool", WaitingState.Finished, new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 3, 8, 0, 1, DateTimeKind.Utc), WaitEnd.Ended),
            };
            using (var history = new WaitHistory(dir, () => T0))
            {
                int recorded = 0;
                history.Recorded += () => Interlocked.Increment(ref recorded);
                foreach (var r in records) history.Record(r);
                True(history.Flush(TimeSpan.FromSeconds(5)), "flushed");
                True(recorded > 0, "Recorded raised");
                True(records.SequenceEqual(history.Load()), "round trip, oldest first, to the millisecond");
            }
            // One file per UTC month of the start; lines hold only the five fields.
            Equal("2026-10.jsonl,2026-11.jsonl", string.Join(',', Directory.GetFiles(dir).Select(Path.GetFileName).Order()), "month files");
            string line = File.ReadAllLines(Path.Combine(dir, "2026-10.jsonl")).Single();
            Equal("{\"tool\":\"claude-code\",\"kind\":\"Blocked\",\"start\":\"2026-10-31T23:59:59.123Z\",\"end\":\"2026-11-01T00:00:41.456Z\",\"outcome\":\"Answered\"}", line, "the line format");
            // A new instance appends to what is there.
            using (var again = new WaitHistory(dir, () => T0))
            {
                again.Record(records[1]);
                True(again.Flush(TimeSpan.FromSeconds(5)), "flushed again");
                Equal(4, again.Load().Count, "appended, nothing lost");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    public static void HistoryCorruption()
    {
        string dir = TempDir();
        try
        {
            var good = new WaitRecord("pi", WaitingState.Blocked, new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 5, 9, 0, 30, DateTimeKind.Utc), WaitEnd.Answered);
            string path = Path.Combine(dir, "2026-11.jsonl");
            File.WriteAllText(path, string.Join("\n", new[]
            {
                WaitHistory.Format(good),
                "not json at all",
                "{\"tool\":\"pi\"}",                                          // fields missing
                "[1,2,3]",                                                     // not an object
                WaitHistory.Format(good).Replace("Blocked", "Sleeping"),       // unknown kind
                WaitHistory.Format(good).Replace("Answered", "Shrugged"),      // unknown outcome
                WaitHistory.Format(good with { EndUtc = good.StartUtc.AddSeconds(-5) }), // ends before it starts
                WaitHistory.Format(good with { Tool = new string('x', 65) }),  // absurd tool id
                WaitHistory.Format(good).Replace("\"tool\":\"pi\"", "\"tool\":7"), // wrong type
                "\0\0\0\0",
                WaitHistory.Format(good with { Tool = "codex" }),
                WaitHistory.Format(good with { Tool = "opencode" })[..40],     // torn last line, no newline
            }));
            var logged = new List<string>();
            using var history = new WaitHistory(dir, () => T0, logged.Add);
            Equal("codex,pi", string.Join(',', history.Load().Select(r => r.Tool).Order()), "only whole records load, the rest still does");
            Equal(1, logged.Count, "the skipped lines are logged once");
            history.Load();
            Equal(1, logged.Count, "and not again on the next load");

            // Appending after the torn line ends it first, so the new record reads whole.
            history.Record(good with { Tool = "claude-code" });
            True(history.Flush(TimeSpan.FromSeconds(5)), "flushed");
            Equal("claude-code,codex,pi", string.Join(',', history.Load().Select(r => r.Tool).Order()), "the torn line costs only itself");
        }
        finally { Directory.Delete(dir, true); }
    }

    public static void HistoryRetention()
    {
        string dir = TempDir();
        try
        {
            WaitRecord At(int year, int month) => new("codex", WaitingState.Finished,
                new DateTime(year, month, 15, 12, 0, 0, DateTimeKind.Utc), new DateTime(year, month, 15, 12, 1, 0, DateTimeKind.Utc), WaitEnd.Resumed);
            foreach (var (y, m) in new[] { (2025, 9), (2025, 10), (2025, 11), (2026, 11) })
                File.WriteAllText(Path.Combine(dir, $"{y}-{m:00}.jsonl"), WaitHistory.Format(At(y, m)) + "\n");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "mine");
            File.WriteAllText(Path.Combine(dir, "2024-13.jsonl"), "{}");

            // Now is November 2026: November 2025 (12 months back) stays, October and older go.
            var now = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
            var clock = now;
            using var history = new WaitHistory(dir, () => clock);
            Equal("2025-11,2026-11", string.Join(',', history.Load().Select(r => r.StartUtc.ToString("yyyy-MM"))), "the reader ignores expired months at once");
            history.Record(At(2026, 11));
            True(history.Flush(TimeSpan.FromSeconds(5)), "flushed");
            Equal("2024-13.jsonl,2025-11.jsonl,2026-11.jsonl,notes.txt",
                string.Join(',', Directory.GetFiles(dir).Select(Path.GetFileName).Order()), "the writer deleted only expired month files");

            // A month later, November 2025 expires too.
            clock = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
            Equal(2, history.Load().Count, "December: November 2025 is out of the window");
            history.Record(At(2026, 12));
            True(history.Flush(TimeSpan.FromSeconds(5)), "flushed");
            True(!File.Exists(Path.Combine(dir, "2025-11.jsonl")), "pruned again when the month changed");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- the metrics ----------

    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    static WaitRecord Wait(DateTime startUtc, string tool = "claude-code", WaitingState kind = WaitingState.Finished,
        double seconds = 10, WaitEnd outcome = WaitEnd.Resumed)
        => new(tool, kind, startUtc, startUtc.AddSeconds(seconds), outcome);

    static DateTime Day(int month, int day, int hour = 12, int minute = 0, int second = 0)
        => new(2026, month, day, hour, minute, second, DateTimeKind.Utc);

    public static void Streaks()
    {
        var now = Day(11, 9, 15);
        ActivitySummary Of(params WaitRecord[] r) => ActivityStats.Summarize(r, now, Utc);

        var empty = Of();
        True(empty.IsEmpty, "no records: empty");
        Equal(Streak.None, empty.CurrentStreak, "no current streak");
        Equal(Streak.None, empty.LongestStreak, "no longest streak");

        // Active today and the two days before: 3, from Nov 7.
        var s = Of(Wait(Day(11, 7)), Wait(Day(11, 8)), Wait(Day(11, 9, 9)));
        Equal(new Streak(3, new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 9)), s.CurrentStreak, "running to today");

        // Today has nothing yet: yesterday's run still counts.
        s = Of(Wait(Day(11, 6)), Wait(Day(11, 7)), Wait(Day(11, 8)));
        Equal(new Streak(3, new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 8)), s.CurrentStreak, "today not yet active");

        // A whole day with nothing breaks it.
        s = Of(Wait(Day(11, 5)), Wait(Day(11, 6)), Wait(Day(11, 7)));
        Equal(Streak.None, s.CurrentStreak, "yesterday empty: broken");
        Equal(3, s.LongestStreak.Days, "but it is still the longest");

        // Midnight: 23:59:59 and 00:00:01 are two days in a row; two at 00:00 and 23:59 one day.
        s = Of(Wait(Day(11, 7, 23, 59, 59)), Wait(Day(11, 8, 0, 0, 1)), Wait(Day(11, 8, 23, 59, 59)));
        Equal(2, s.CurrentStreak.Days, "either side of midnight");
        Equal(2, s.Daily[new DateOnly(2026, 11, 8)], "both ends of one day count to it");

        // Gaps and ties: the latest of the longest runs.
        s = Of(Wait(Day(10, 1)), Wait(Day(10, 2)), Wait(Day(10, 3)),
               Wait(Day(10, 10)),
               Wait(Day(10, 20)), Wait(Day(10, 21)), Wait(Day(10, 22)),
               Wait(Day(11, 9)));
        Equal(new Streak(3, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22)), s.LongestStreak, "latest longest on a tie");
        Equal(new Streak(1, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9)), s.CurrentStreak, "just today");

        // A start from a clock that ran ahead counts nowhere.
        s = Of(Wait(Day(11, 10)));
        True(s.IsEmpty, "future records are ignored");
    }

    public static void TypicalResponse()
    {
        var now = Day(11, 9, 15);
        var records = new[]
        {
            Wait(Day(11, 9, 9), kind: WaitingState.Blocked, seconds: 30, outcome: WaitEnd.Answered),
            Wait(Day(11, 1), kind: WaitingState.Blocked, seconds: 90, outcome: WaitEnd.Answered),
            Wait(Day(10, 11), kind: WaitingState.Blocked, seconds: 60, outcome: WaitEnd.Answered),  // 29 days back: in
            Wait(Day(10, 10), kind: WaitingState.Blocked, seconds: 9999, outcome: WaitEnd.Answered), // 30 days back: out
            Wait(Day(11, 5), kind: WaitingState.Blocked, seconds: 5000, outcome: WaitEnd.Ended),   // never answered
            Wait(Day(11, 5), kind: WaitingState.Finished, seconds: 5000, outcome: WaitEnd.Resumed), // done, not needs-you
            Wait(Day(11, 5), kind: WaitingState.Finished, seconds: 5000, outcome: WaitEnd.Expired),
        };
        var s = ActivityStats.Summarize(records, now, Utc);
        Equal(TimeSpan.FromSeconds(60), s.TypicalResponse, "median of the answered needs-you waits in the last 30 days");
        Equal(3, s.Responses, "three answers counted");
        Equal<TimeSpan?>(null, ActivityStats.Summarize(records.Skip(4), now, Utc).TypicalResponse, "nothing answered: none");
        Equal(TimeSpan.FromSeconds(60), ActivityStats.Summarize(records.Take(2), now, Utc).TypicalResponse, "an even count: the middle two's mean (30 s, 90 s)");
        // One prompt left open overnight doesn't swamp a month of quick answers.
        var quick = Enumerable.Range(0, 50).Select(i => Wait(Day(11, 9, 9), kind: WaitingState.Blocked, seconds: 20, outcome: WaitEnd.Answered))
            .Append(Wait(Day(11, 8, 9), kind: WaitingState.Blocked, seconds: 36000, outcome: WaitEnd.Answered));
        Equal(TimeSpan.FromSeconds(20), ActivityStats.Summarize(quick, now, Utc).TypicalResponse, "the outlier doesn't move it");

        Equal("<1 s", ActivityStats.FormatDuration(TimeSpan.FromMilliseconds(400)), "under a second");
        Equal("0 s", ActivityStats.FormatDuration(TimeSpan.Zero), "zero");
        Equal("42 s", ActivityStats.FormatDuration(TimeSpan.FromSeconds(42.4)), "seconds");
        Equal("1 m", ActivityStats.FormatDuration(TimeSpan.FromSeconds(59.6)), "rounds up to a minute");
        Equal("3 m 10 s", ActivityStats.FormatDuration(TimeSpan.FromSeconds(190)), "minutes and seconds");
        Equal("1 h 5 m", ActivityStats.FormatDuration(TimeSpan.FromSeconds(3600 + 300 + 20)), "hours and minutes");
        Equal("2 h", ActivityStats.FormatDuration(TimeSpan.FromHours(2)), "whole hours");
    }

    public static void AgentCounts()
    {
        var now = Day(11, 9, 15);
        var records = new[]
        {
            Wait(Day(11, 9), "pi"), Wait(Day(11, 8), "codex"), Wait(Day(11, 8), "codex", WaitingState.Blocked),
            Wait(Day(11, 7), "claude-code"), Wait(Day(11, 6), "zed"), Wait(Day(11, 6), "aider"),
            Wait(Day(10, 11), "opencode"),  // 29 days back: in
            Wait(Day(10, 10), "opencode"),  // 30 days back: out
            Wait(Day(5, 1), "claude-code"), // long ago: only in the calendar
        };
        var s = ActivityStats.Summarize(records, now, Utc);
        Equal("claude-code=1,codex=2,opencode=1,pi=1,aider=1,zed=1",
            string.Join(',', s.RecentAgents.Select(a => $"{a.Tool}={a.Count}")), "the agents' fixed order, then the rest by name");
        Equal(7, s.RecentTotal, "the 30-day total");
        Equal(9, s.Daily.Values.Sum(), "the calendar keeps everything");

        Equal("34,33,33", string.Join(',', ActivityStats.Percentages(new[] { 1, 1, 1 })), "largest remainder; ties to the earlier, sum 100");
        Equal("50,31,12,7", string.Join(',', ActivityStats.Percentages(new[] { 95, 59, 23, 13 })), "the screenshot's shares");
        Equal("0,0", string.Join(',', ActivityStats.Percentages(new[] { 0, 0 })), "nothing");
        Equal(100, ActivityStats.Percentages(new[] { 7, 3, 9, 1, 1, 1 }).Sum(), "always 100");

        // Heat levels: quartiles of the active days; the busiest is always the darkest.
        var t = ActivityStats.HeatThresholds(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 40 });
        Equal("0,1,1,1,2,2,3,3,4", string.Join(',', new[] { 0, 1, 2, 3, 4, 5, 6, 7, 40 }.Select(c => ActivityStats.HeatLevel(c, t))), "quartile levels");
        var same = ActivityStats.HeatThresholds(new[] { 5, 5, 5 });
        Equal(4, ActivityStats.HeatLevel(5, same), "equal days are all the busiest");
        Equal(0, ActivityStats.HeatLevel(0, ActivityStats.HeatThresholds(Array.Empty<int>())), "no activity");
    }

    public static void DaylightSaving()
    {
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        DateOnly D(int month, int day) => new(2026, month, day);
        // Spring forward, 2026-03-08 02:00 PST -> 03:00 PDT.
        Equal(D(3, 7), ActivityStats.LocalDay(Day(3, 8, 7, 30), pacific), "23:30 PST is still the 7th");
        Equal(D(3, 8), ActivityStats.LocalDay(Day(3, 8, 8, 30), pacific), "00:30 PST is the 8th");
        Equal(D(3, 8), ActivityStats.LocalDay(Day(3, 9, 6, 59), pacific), "23:59 PDT is still the 8th");
        Equal(D(3, 9), ActivityStats.LocalDay(Day(3, 9, 7, 0), pacific), "00:00 PDT is the 9th");
        // Fall back, 2026-11-01 02:00 PDT -> 01:00 PST: the repeated hour stays on the 1st.
        Equal(D(10, 31), ActivityStats.LocalDay(Day(11, 1, 6, 59), pacific), "23:59 PDT is the 31st");
        Equal(D(11, 1), ActivityStats.LocalDay(Day(11, 1, 8, 30), pacific), "01:30 PDT");
        Equal(D(11, 1), ActivityStats.LocalDay(Day(11, 1, 9, 30), pacific), "01:30 PST, the second time");
        Equal(D(11, 1), ActivityStats.LocalDay(Day(11, 2, 7, 59), pacific), "23:59 PST is still the 1st");

        // A 25-hour day holds its waits; the streak runs straight through both changes.
        var records = new List<WaitRecord>();
        for (var day = new DateTime(2026, 3, 6, 18, 0, 0, DateTimeKind.Utc); day <= Day(3, 10, 18); day = day.AddDays(1))
            records.Add(Wait(day));
        for (var day = new DateTime(2026, 10, 30, 18, 0, 0, DateTimeKind.Utc); day <= Day(11, 3, 18); day = day.AddDays(1))
            records.Add(Wait(day));
        records.Add(Wait(Day(11, 1, 8, 30)));
        records.Add(Wait(Day(11, 1, 9, 30)));
        var s = ActivityStats.Summarize(records, Day(11, 3, 20), pacific);
        Equal(3, s.Daily[D(11, 1)], "the fall-back day keeps all three");
        Equal(5, s.CurrentStreak.Days, "Oct 30 - Nov 3 in a row across the change");
        Equal(5, s.LongestStreak.Days, "Mar 6 - Mar 10 too");

        // The calendar's first cell is a whole number of weeks back, counted in days.
        foreach (var today in new[] { D(3, 8), D(3, 9), D(11, 1), D(11, 9) })
            foreach (var first in new[] { DayOfWeek.Sunday, DayOfWeek.Monday })
            {
                var start = ActivityStats.GridStart(today, 26, first);
                Equal(first, start.DayOfWeek, $"{today} starts on {first}");
                int span = today.DayNumber - start.DayNumber;
                True(span >= 175 && span < 182, $"{today}: 26 columns end with today ({span})");
            }
    }

    // The whole path: activity -> history -> summary.
    public static void EndToEnd()
    {
        string dir = TempDir();
        try
        {
            using var history = new WaitHistory(dir, () => T0);
            var activity = new AgentActivity(TimeSpan.FromSeconds(60));
            activity.WaitEnded += history.Record;
            var records = new List<WaitRecord>();
            activity.WaitEnded += records.Add;
            activity.Report("codex", "s", "C:\\secret\\hushproject", AgentEventKind.AwaitingUser, T0.AddMinutes(-10));
            Equal("hushproject", activity.Waiting.Single().Project, "the token's label knows the project");
            activity.Report("codex", "s", "C:\\secret\\hushproject", AgentEventKind.ThinkingStarted, T0.AddMinutes(-10).AddSeconds(12));
            activity.Report("codex", "s", "C:\\secret\\hushproject", AgentEventKind.TurnComplete, T0.AddMinutes(-9));
            activity.EndAll(T0.AddMinutes(-8));
            // A record has no field that could hold it: the tool, the state, the times and the outcome only.
            Equal("Tool,Kind,StartUtc,EndUtc,Outcome,Duration", string.Join(',', typeof(WaitRecord).GetProperties().Select(p => p.Name)), "record fields");
            True(records.Count == 2 && records.All(r => !r.ToString().Contains("hush")), "no record names the project");
            True(history.Flush(TimeSpan.FromSeconds(5)), "flushed");
            var s = ActivityStats.Summarize(history.Load(), T0, Utc);
            Equal(2, s.RecentTotal, "two waits");
            Equal(TimeSpan.FromSeconds(12), s.TypicalResponse, "the answered one");
            string text = string.Concat(Directory.GetFiles(dir).Select(File.ReadAllText));
            True(!text.Contains("secret") && !text.Contains("hush") && !text.Contains("\"s\"") && !text.Contains("codex:s"), "no project, path or session id on disk");
        }
        finally { Directory.Delete(dir, true); }
    }

    // Real payloads through the bridge. Claude Code: an approval answered after 5 s whose command then
    // runs for 90 s counts as a 5 s answer (PostToolUse's duration_ms leaves the prompt out); a
    // question's run is the question itself, so it is answered when it finishes. Codex: the same for a
    // question; an approval reports no answer moment, so it is counted but untimed.
    public static void ApprovalTiming()
    {
        var activity = new AgentActivity(TimeSpan.FromSeconds(60));
        var ended = new List<WaitRecord>();
        activity.WaitEnded += ended.Add;
        void Feed(object payload, DateTime at, string tool = "claude-code")
        {
            var msg = AgentsCursor.App.AgentHookBridge.Parse(tool, System.Text.Json.JsonSerializer.Serialize(payload))!;
            True(msg.TryGetKind(out var kind), "a known event: " + msg.Event);
            // As the tray does (TrayApplicationContext.HandleAgentEvent), after a round trip on the pipe.
            msg = AgentEventMessage.FromJson(msg.ToJsonLine())!;
            activity.Report(msg.Tool, msg.SessionId, msg.Project, kind, at, msg.InputIds, msg.AgentId, msg.CallIds, null, msg.CallRan);
        }
        var input = new { command = "dotnet test" };
        Feed(new { hook_event_name = "PreToolUse", session_id = "s", tool_name = "Bash", tool_input = input, tool_use_id = "t1" }, T0);
        Feed(new { hook_event_name = "PermissionRequest", session_id = "s", tool_name = "Bash", tool_input = input }, T0.AddSeconds(2));
        Feed(new { hook_event_name = "PostToolUse", session_id = "s", tool_name = "Bash", tool_input = input, tool_use_id = "t1", tool_response = new { }, duration_ms = 90_000 }, T0.AddSeconds(97));
        Equal("claude-code:Blocked:5s:Answered", string.Join(',', ended.Select(Describe)), "answered when the command started");
        ended.Clear();

        var question = new { questions = new[] { new { question = "Which?" } } };
        Feed(new { hook_event_name = "PreToolUse", session_id = "s", tool_name = "AskUserQuestion", tool_input = question, tool_use_id = "q1" }, T0);
        Feed(new { hook_event_name = "PostToolUse", session_id = "s", tool_name = "AskUserQuestion", tool_input = question, tool_use_id = "q1", tool_response = new { }, duration_ms = 40_000 }, T0.AddSeconds(40));
        Equal("claude-code:Blocked:40s:Answered", string.Join(',', ended.Select(Describe)), "a question is answered when it finishes");
        Equal<long?>(null, AgentsCursor.App.AgentHookBridge.Parse("claude-code", "{\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"Bash\"}")!.RanMs, "no duration_ms: no run time");
        ended.Clear();

        // Codex: a question finishes as it is answered; an approval says nothing of when you answered
        // (no run time, and its prompt names no call), so it is counted but not timed.
        var ask = new { questions = new[] { new { question = "Which?" } } };
        Feed(new { hook_event_name = "PreToolUse", session_id = "x", tool_name = "request_user_input", tool_input = ask, tool_use_id = "u1" }, T0, "codex");
        Feed(new { hook_event_name = "PostToolUse", session_id = "x", tool_name = "request_user_input", tool_input = ask, tool_use_id = "u1", tool_response = "{}" }, T0.AddSeconds(12), "codex");
        var run = new { command = "dotnet test" };
        Feed(new { hook_event_name = "PreToolUse", session_id = "x", tool_name = "Bash", tool_input = run, tool_use_id = "u2" }, T0.AddSeconds(20), "codex");
        Feed(new { hook_event_name = "PermissionRequest", session_id = "x", tool_name = "Bash", tool_input = run }, T0.AddSeconds(21), "codex");
        Feed(new { hook_event_name = "PostToolUse", session_id = "x", tool_name = "Bash", tool_input = run, tool_use_id = "u2", tool_response = "{}" }, T0.AddSeconds(120), "codex");
        Equal("codex:Blocked:12s:Answered,codex:Blocked:99s:Proceeded", string.Join(',', ended.Select(Describe)), "Codex question timed, approval not");
        Equal(false, AgentEventMessage.FromJson("{\"tool\":\"pi\",\"event\":\"tool_use\"}")!.ToJsonLine().Contains("callRan"), "the derived span isn't on the wire");
    }
}
