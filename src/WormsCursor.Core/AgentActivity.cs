namespace WormsCursor.Core;

/// <summary>The normalised lifecycle event a bridge reports for one agent session. Tool-specific
/// raw events (Claude's <c>Stop</c>, Codex's <c>agent-turn-complete</c>, …) are mapped to these
/// by the <c>hook</c> bridge before they reach the pipe; this enum is the source of truth.</summary>
public enum AgentEventKind
{
    /// <summary>The agent started working on your prompt (Claude <c>UserPromptSubmit</c>). Clears "needs you".</summary>
    ThinkingStarted,
    /// <summary>The agent is blocked on you mid-turn — a permission/idle prompt (Claude
    /// <c>Notification</c> permission_prompt/idle_prompt). Sets "needs you".</summary>
    AwaitingUser,
    /// <summary>The agent finished its turn; the ball is in your court (Claude <c>Stop</c>,
    /// Codex <c>agent-turn-complete</c>). Sets "needs you" and fires a celebratory pulse.</summary>
    TurnComplete,
    /// <summary>The agent is actively using a tool (Pre/PostToolUse). Clears "needs you".</summary>
    ToolUse,
    /// <summary>The turn ended on an error (Claude <c>StopFailure</c>). Sets "needs you", red pulse.</summary>
    Error,
    /// <summary>The session ended (Claude <c>SessionEnd</c> — /exit, Ctrl+C, /clear, logout). Drops
    /// the session entirely so its logo disappears at once instead of lingering until the TTL.</summary>
    SessionEnded,
}

/// <summary>A one-shot animation accent emitted when a session transitions on a notable event
/// (turn complete → a logo "pop"; error → a red tint). Distinct from the steady waiting count.</summary>
public readonly record struct AgentPulse(string Tool, string Key, AgentEventKind Kind);

/// <summary>
/// Tracks which agent sessions currently <b>need the user's attention</b>, across every connected
/// tool, and exposes a single count for the cursor to render. UI-agnostic and thread-safe: the
/// pipe-listener thread calls <see cref="Report"/>; the tray pushes <see cref="WaitingCount"/> into
/// the engine and forwards <see cref="Pulse"/>s.
///
/// "Needs you" is set by <see cref="AgentEventKind.AwaitingUser"/>, <see cref="AgentEventKind.TurnComplete"/>
/// and <see cref="AgentEventKind.Error"/> (the agent is blocked or done — your move), and cleared by
/// <see cref="AgentEventKind.ThinkingStarted"/> / <see cref="AgentEventKind.ToolUse"/> (it's working again,
/// so you must have responded), and the session is dropped outright by <see cref="AgentEventKind.SessionEnded"/>
/// (the agent exited). Sessions with no event for <see cref="_ttl"/> are also swept, so one that never
/// sends a closing event can't wedge the count.
/// </summary>
public sealed class AgentActivity
{
    sealed class Entry { public bool NeedsYou; public DateTime LastUtc; public string Tool = ""; }

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _sessions = new();
    readonly TimeSpan _ttl;

    public AgentActivity(TimeSpan? ttl = null) => _ttl = ttl ?? TimeSpan.FromMinutes(30);

    /// <summary>Raised (outside the lock) whenever the waiting count changes, with the new value.</summary>
    public event Action<int>? WaitingCountChanged;

    /// <summary>Raised (outside the lock) for notable one-shot events, for animation accents.</summary>
    public event Action<AgentPulse>? Pulse;

    /// <summary>How many distinct agent sessions currently need the user.</summary>
    public int WaitingCount { get { lock (_gate) return CountNeedsYou(); } }

    /// <summary>The tool id of each session that currently needs the user (one entry per waiting
    /// agent, e.g. <c>["claude-code", "codex"]</c>) — the engine hangs one logo charm per entry.
    /// A snapshot taken under the lock, safe to hand to another thread.</summary>
    public IReadOnlyList<string> WaitingTools
    {
        get
        {
            lock (_gate)
            {
                var list = new List<string>();
                foreach (var e in _sessions.Values) if (e.NeedsYou) list.Add(e.Tool);
                return list;
            }
        }
    }

    /// <summary>Apply one normalised event for a session. <paramref name="sessionId"/> may be null
    /// (we then key by tool+project). <paramref name="nowUtc"/> is injected so callers/tests stay
    /// deterministic. Safe to call from any thread.</summary>
    public void Report(string tool, string? sessionId, string? project, AgentEventKind kind, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(tool)) tool = "unknown";
        int before, after;
        var key = Key(tool, sessionId, project);
        AgentPulse? pulse = null;
        lock (_gate)
        {
            SweepLocked(nowUtc);
            before = CountNeedsYou();
            if (kind == AgentEventKind.SessionEnded)
            {
                _sessions.Remove(key); // the session is gone — forget it, don't wait on it
            }
            else
            {
                if (!_sessions.TryGetValue(key, out var e)) { e = new Entry(); _sessions[key] = e; }
                e.Tool = tool;
                e.LastUtc = nowUtc;
                e.NeedsYou = kind switch
                {
                    AgentEventKind.AwaitingUser or AgentEventKind.TurnComplete or AgentEventKind.Error => true,
                    _ => false, // ThinkingStarted / ToolUse: the agent is working again
                };
                if (kind is AgentEventKind.TurnComplete or AgentEventKind.Error)
                    pulse = new AgentPulse(tool, key, kind);
            }
            after = CountNeedsYou();
        }
        if (pulse is { } p) Pulse?.Invoke(p);
        if (after != before) WaitingCountChanged?.Invoke(after);
    }

    /// <summary>Evict sessions idle longer than the TTL. Call periodically (e.g. a tray timer) so a
    /// session that never sends a closing event eventually drops out of the count.</summary>
    public void Sweep(DateTime nowUtc)
    {
        int before, after;
        lock (_gate)
        {
            before = CountNeedsYou();
            SweepLocked(nowUtc);
            after = CountNeedsYou();
        }
        if (after != before) WaitingCountChanged?.Invoke(after);
    }

    /// <summary>Forget everything (e.g. when the feature is toggled off).</summary>
    public void Clear()
    {
        bool changed;
        lock (_gate)
        {
            changed = CountNeedsYou() > 0;
            _sessions.Clear();
        }
        if (changed) WaitingCountChanged?.Invoke(0);
    }

    void SweepLocked(DateTime nowUtc)
    {
        List<string>? dead = null;
        foreach (var kv in _sessions)
            if (nowUtc - kv.Value.LastUtc > _ttl)
                (dead ??= new()).Add(kv.Key);
        if (dead is not null)
            foreach (var k in dead) _sessions.Remove(k);
    }

    int CountNeedsYou()
    {
        int n = 0;
        foreach (var e in _sessions.Values) if (e.NeedsYou) n++;
        return n;
    }

    // A null sessionId still wants per-session-ish granularity, so fall back to the project path.
    static string Key(string tool, string? sessionId, string? project)
        => !string.IsNullOrEmpty(sessionId) ? $"{tool}:{sessionId}"
         : !string.IsNullOrEmpty(project)   ? $"{tool}:proj:{project}"
         : tool;
}
