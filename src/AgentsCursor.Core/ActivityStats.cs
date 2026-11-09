using System.Globalization;

namespace AgentsCursor.Core;

/// <summary>A run of consecutive active days: how many, and the first and last of them (null when
/// there is none).</summary>
public readonly record struct Streak(int Days, DateOnly? From, DateOnly? To)
{
    public static readonly Streak None = new(0, null, null);
}

/// <summary>How many times one agent needed you.</summary>
public readonly record struct AgentCount(string Tool, int Count);

/// <summary>What the Analytics page shows, computed from the history in one pass.</summary>
public sealed record ActivitySummary(
    DateOnly Today,
    IReadOnlyDictionary<DateOnly, int> Daily,
    Streak CurrentStreak,
    Streak LongestStreak,
    TimeSpan? TypicalResponse,
    int Responses,
    IReadOnlyList<AgentCount> RecentAgents,
    int RecentTotal)
{
    /// <summary>Nothing recorded yet (a fresh install).</summary>
    public bool IsEmpty => Daily.Count == 0;
}

/// <summary>
/// The Analytics page's numbers, as pure functions of the history, "now" and a time zone. Every wait
/// belongs to the local calendar day its token appeared on (<see cref="LocalDay"/>), whatever the
/// daylight-saving offset was then.
/// <list type="bullet">
/// <item><b>Daily count</b>: waits (needs you and done alike) that started that day.</item>
/// <item><b>Active day</b>: a day with at least one wait.</item>
/// <item><b>Current streak</b>: the active days in a row up to today, or up to yesterday while
/// today has none yet (today isn't over); it breaks once a whole day passes with none.</item>
/// <item><b>Longest streak</b>: the longest such run in the history (the latest on a tie).</item>
/// <item><b>Typical response time</b>: the median <see cref="WaitRecord.Duration"/> of the needs-you
/// waits (<see cref="WaitingState.Blocked"/>) that ended <see cref="WaitEnd.Answered"/> and started in
/// the last <see cref="RecentDays"/> days: from the moment the session started needing you (its
/// token appeared, or would have while hidden) to the moment you answered, never the agent's work after
/// it: a prompt you sent, a question or form you answered, or an approved call starting to run (Claude
/// Code's <c>duration_ms</c>). Waits whose answer moment no event marks
/// (<see cref="WaitEnd.Proceeded"/>: a Codex approval, a Claude Code denial, a turn ending under a
/// prompt) are left out rather than timed to the agent's next event, as are done waits and waits
/// nobody answered (session ended, host closed, app stopped, the turn failed). The median, not the
/// mean, so one prompt left open overnight doesn't swamp a month of quick answers.</item>
/// <item><b>Agents</b>: waits per agent that started in the last <see cref="RecentDays"/> days,
/// in the agents' fixed order (<see cref="IconCatalog.Tools"/>, then any other tool by name).</item>
/// </list>
/// "The last N days" is today and the N - 1 days before it.
/// </summary>
public static class ActivityStats
{
    public const int RecentDays = 30;

    public static DateOnly LocalDay(DateTime utc, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));

    public static ActivitySummary Summarize(IEnumerable<WaitRecord> records, DateTime nowUtc, TimeZoneInfo zone)
    {
        var today = LocalDay(nowUtc, zone);
        var recentFrom = today.AddDays(-(RecentDays - 1));
        var daily = new Dictionary<DateOnly, int>();
        var agents = new Dictionary<string, int>(StringComparer.Ordinal);
        var responses = new List<long>();
        int recentTotal = 0;

        foreach (var record in records)
        {
            var day = LocalDay(record.StartUtc, zone);
            if (day > today) continue; // from a clock that ran ahead
            daily[day] = daily.GetValueOrDefault(day) + 1;
            if (day < recentFrom) continue;
            recentTotal++;
            agents[record.Tool] = agents.GetValueOrDefault(record.Tool) + 1;
            if (record.Kind == WaitingState.Blocked && record.Outcome == WaitEnd.Answered)
                responses.Add(record.Duration.Ticks);
        }

        var active = daily.Keys.ToHashSet();
        return new ActivitySummary(
            today,
            daily,
            CurrentStreak(active, today),
            LongestStreak(active),
            Median(responses),
            responses.Count,
            OrderAgents(agents),
            recentTotal);
    }

    /// <summary>The median of <paramref name="ticks"/> (the mean of the middle two for an even count),
    /// or null for none.</summary>
    static TimeSpan? Median(List<long> ticks)
    {
        if (ticks.Count == 0) return null;
        ticks.Sort();
        int mid = ticks.Count / 2;
        return TimeSpan.FromTicks(ticks.Count % 2 == 1 ? ticks[mid] : ticks[mid - 1] + (ticks[mid] - ticks[mid - 1]) / 2);
    }

    public static Streak CurrentStreak(IReadOnlySet<DateOnly> active, DateOnly today)
    {
        var last = active.Contains(today) ? today : today.AddDays(-1);
        if (!active.Contains(last)) return Streak.None;
        var first = last;
        while (active.Contains(first.AddDays(-1))) first = first.AddDays(-1);
        return new Streak(last.DayNumber - first.DayNumber + 1, first, last);
    }

    public static Streak LongestStreak(IEnumerable<DateOnly> active)
    {
        var best = Streak.None;
        DateOnly? runStart = null, previous = null;
        foreach (var day in active.Distinct().Order())
        {
            if (previous is not { } p || day.DayNumber != p.DayNumber + 1) runStart = day;
            previous = day;
            int length = day.DayNumber - runStart!.Value.DayNumber + 1;
            if (length >= best.Days) best = new Streak(length, runStart, day);
        }
        return best;
    }

    static IReadOnlyList<AgentCount> OrderAgents(Dictionary<string, int> counts)
    {
        int Rank(string tool)
        {
            for (int i = 0; i < IconCatalog.Tools.Count; i++)
                if (IconCatalog.Tools[i] == tool) return i;
            return int.MaxValue;
        }
        return counts
            .OrderBy(kv => Rank(kv.Key)).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new AgentCount(kv.Key, kv.Value))
            .ToArray();
    }

    /// <summary>A duration the way the tiles show it: "42 s", "3 m 10 s", "3 m", "1 h 5 m"
    /// (rounded to the second; under a second is "&lt;1 s").</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        long seconds = (long)Math.Round(Math.Max(0, duration.TotalSeconds), MidpointRounding.AwayFromZero);
        if (duration > TimeSpan.Zero && seconds == 0) return "<1 s";
        if (seconds < 60) return $"{seconds} s";
        long minutes = seconds / 60, s = seconds % 60;
        if (minutes < 60) return s == 0 ? $"{minutes} m" : $"{minutes} m {s} s";
        // Past an hour the seconds are noise: round to the minute.
        minutes = (long)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);
        long h = minutes / 60, m = minutes % 60;
        return m == 0 ? $"{h} h" : $"{h} h {m} m";
    }

    /// <summary>The first day (top-left cell) of a heatmap of <paramref name="weeks"/> week columns
    /// whose last column holds <paramref name="today"/>, with weeks starting on
    /// <paramref name="firstDay"/>. Calendar days, not hours, so daylight saving never shifts it.</summary>
    public static DateOnly GridStart(DateOnly today, int weeks, DayOfWeek firstDay)
    {
        int intoWeek = ((int)today.DayOfWeek - (int)firstDay + 7) % 7;
        return today.AddDays(-intoWeek - 7 * (weeks - 1));
    }

    /// <summary>The heatmap's level boundaries for these day counts: the quartiles (nearest rank)
    /// of the non-zero counts, so a single busy day doesn't wash every other day out.</summary>
    public static int[] HeatThresholds(IEnumerable<int> counts)
    {
        var nonZero = counts.Where(c => c > 0).Order().ToArray();
        if (nonZero.Length == 0) return new[] { 0, 0, 0, 0 };
        int Rank(double q) => nonZero[Math.Clamp((int)Math.Ceiling(q * nonZero.Length) - 1, 0, nonZero.Length - 1)];
        return new[] { Rank(0.25), Rank(0.5), Rank(0.75), nonZero[^1] };
    }

    /// <summary>A day's heatmap level for <see cref="HeatThresholds"/>: 0 for none, then 1 to 4 by
    /// quartile of the active days (the busiest day is always 4).</summary>
    public static int HeatLevel(int count, int[] thresholds)
    {
        if (count <= 0) return 0;
        if (count > thresholds[2] || count >= thresholds[3]) return 4;
        return count <= thresholds[0] ? 1 : count <= thresholds[1] ? 2 : 3;
    }

    /// <summary>Whole percentages of <paramref name="counts"/> that add up to exactly 100 (largest
    /// remainder; ties go to the earlier count), or all zeros when there is nothing.</summary>
    public static int[] Percentages(IReadOnlyList<int> counts)
    {
        long total = counts.Sum(c => (long)Math.Max(0, c));
        var result = new int[counts.Count];
        if (total == 0) return result;
        var remainders = new (double Remainder, int Index)[counts.Count];
        int given = 0;
        for (int i = 0; i < counts.Count; i++)
        {
            double exact = 100.0 * Math.Max(0, counts[i]) / total;
            result[i] = (int)Math.Floor(exact);
            given += result[i];
            remainders[i] = (exact - result[i], i);
        }
        foreach (var (_, index) in remainders.OrderByDescending(r => r.Remainder).ThenBy(r => r.Index).Take(100 - given))
            result[index]++;
        return result;
    }

    /// <summary>"Sat, Nov 8" in <paramref name="culture"/>'s abbreviations.</summary>
    public static string ShortDate(DateOnly day, CultureInfo culture)
        => day.ToString("ddd, MMM d", culture);
}
