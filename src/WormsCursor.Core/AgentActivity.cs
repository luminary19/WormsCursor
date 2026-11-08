namespace WormsCursor.Core;

/// <summary>The normalised lifecycle event a bridge reports for one agent session. Tool-specific
/// raw events (Claude's and Codex's <c>Stop</c>, etc.) are mapped to these
/// by the <c>hook</c> bridge before they reach the pipe; this enum is the source of truth.</summary>
public enum AgentEventKind
{
    /// <summary>The agent started working on your prompt (Claude <c>UserPromptSubmit</c>). Clears "needs you".</summary>
    ThinkingStarted,
    /// <summary>The agent is blocked on you mid-turn — a permission/idle prompt (Claude
    /// <c>Notification</c> permission_prompt/idle_prompt). Sets "needs you" <b>indefinitely</b>: the
    /// agent is actively waiting on your decision, so this logo is never swept by the idle timeout —
    /// it stays until you respond (the session reports work again) or it ends.</summary>
    AwaitingUser,
    /// <summary>The agent finished its turn; the ball is in your court (Claude <c>Stop</c>,
    /// Codex <c>Stop</c>). Sets "needs you" and fires a celebratory pulse. Unlike
    /// <see cref="AwaitingUser"/> this just needs a fresh prompt, so its logo lingers then auto-clears
    /// once the idle timeout elapses.</summary>
    TurnComplete,
    /// <summary>The agent is actively using a tool (Pre/PostToolUse). Clears "needs you".</summary>
    ToolUse,
    /// <summary>The turn ended on an error (Claude <c>StopFailure</c>). Sets "needs you", red pulse.</summary>
    Error,
    /// <summary>The session ended (Claude <c>SessionEnd</c> — /exit, Ctrl+C, /clear, logout). Drops
    /// the session entirely so its logo disappears at once instead of lingering until the TTL.</summary>
    SessionEnded,
    /// <summary>Questions were displayed while the agent continues working. Tool activity and turn
    /// completion do not answer them; they remain pending until user input arrives.</summary>
    InputRequested,
    /// <summary>The user answered specific asynchronous questions.</summary>
    InputAnswered,
}

/// <summary>
/// Tracks which agent sessions currently <b>need the user's attention</b>, across every connected
/// tool, and exposes a single count for the cursor to render. UI-agnostic and thread-safe: the
/// pipe-listener thread calls <see cref="Report"/>; the tray reads <see cref="WaitingTools"/> /
/// <see cref="WaitingCount"/> and pushes them into the overlay.
///
/// "Needs you" is set by <see cref="AgentEventKind.AwaitingUser"/>, <see cref="AgentEventKind.TurnComplete"/>
/// and <see cref="AgentEventKind.Error"/> (the agent is blocked or done — your move), and cleared by
/// <see cref="AgentEventKind.ThinkingStarted"/> / <see cref="AgentEventKind.ToolUse"/> (it's working again,
/// so you must have responded), and the session is dropped outright by <see cref="AgentEventKind.SessionEnded"/>
/// (the agent exited).
///
/// The idle timeout (<see cref="_ttl"/>) only sweeps <b>timed</b> waits — a finished turn or an error,
/// which just need a fresh prompt — so a logo can't wedge the count after a quiet finish. A session
/// blocked on your approval (<see cref="AgentEventKind.AwaitingUser"/>) is <b>sticky</b>: it is never
/// swept and stays until you actually respond or it ends, because the agent really is still waiting on
/// you. (Trade-off: a hard-killed agent that was awaiting approval leaves its logo up until you clear
/// or disable it, since no closing event ever arrives.)
/// </summary>
public sealed class AgentActivity
{
    // Sticky == this wait is the agent blocking on your approval; it survives the idle sweep entirely.
    sealed class Entry
    {
        public bool NeedsYou;
        public bool Sticky;
        public DateTime LastUtc;
        public string Tool = "";
        public readonly HashSet<string> PendingInputs = new(StringComparer.Ordinal);
        public bool IsWaiting => NeedsYou || PendingInputs.Count > 0;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _sessions = new();
    TimeSpan _ttl;

    public AgentActivity(TimeSpan? ttl = null) => _ttl = ttl ?? TimeSpan.FromMinutes(1);

    /// <summary>The idle timeout after which a <i>timed</i> waiting session (a finished turn or error)
    /// with no further events is swept out of the count, so a finished-turn logo can't linger forever.
    /// Sessions blocked on your approval (<see cref="AgentEventKind.AwaitingUser"/>) are sticky and
    /// ignore this. Settable live from the preferences UI.</summary>
    public TimeSpan Ttl
    {
        get { lock (_gate) return _ttl; }
        set { lock (_gate) _ttl = value; }
    }

    /// <summary>Raised outside the lock when the waiting logos change, with the current count.
    /// Replacing a Claude session with a Codex session also refreshes the display at the same count.</summary>
    public event Action<int>? WaitingChanged;

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
                foreach (var e in _sessions.Values) if (e.IsWaiting) list.Add(e.Tool);
                return list;
            }
        }
    }

    /// <summary>Apply one normalised event for a session. <paramref name="sessionId"/> may be null
    /// (we then key by tool+project). <paramref name="nowUtc"/> is injected so callers/tests stay
    /// deterministic. Safe to call from any thread.</summary>
    public void Report(string tool, string? sessionId, string? project, AgentEventKind kind, DateTime nowUtc,
        IReadOnlyList<string>? inputIds = null)
    {
        if (string.IsNullOrEmpty(tool)) tool = "unknown";
        int after;
        bool changed;
        var key = Key(tool, sessionId, project);
        lock (_gate)
        {
            var before = WaitingTools;
            SweepLocked(nowUtc);
            if (kind == AgentEventKind.SessionEnded)
            {
                _sessions.Remove(key); // the session is gone — forget it, don't wait on it
            }
            else
            {
                if (!_sessions.TryGetValue(key, out var e)) { e = new Entry(); _sessions[key] = e; }
                e.Tool = tool;
                e.LastUtc = nowUtc;
                if (kind == AgentEventKind.InputRequested)
                {
                    if (inputIds is not null) e.PendingInputs.UnionWith(inputIds);
                }
                else if (kind == AgentEventKind.InputAnswered)
                {
                    if (inputIds is not null) e.PendingInputs.ExceptWith(inputIds);
                    // Answering a question acknowledges a finished turn, but does not answer an
                    // independent approval prompt. Async questions and approvals can overlap.
                    if (!e.Sticky) e.NeedsYou = false;
                }
                else
                {
                    if (kind == AgentEventKind.ThinkingStarted) e.PendingInputs.Clear();
                    e.NeedsYou = kind switch
                    {
                        AgentEventKind.AwaitingUser or AgentEventKind.TurnComplete or AgentEventKind.Error => true,
                        _ => false, // ThinkingStarted / ToolUse: the agent is working again
                    };
                    // Approval prompts remain sticky; completion and error notices expire.
                    e.Sticky = kind == AgentEventKind.AwaitingUser;
                }
            }
            after = CountNeedsYou();
            changed = !before.SequenceEqual(WaitingTools);
        }
        if (changed) WaitingChanged?.Invoke(after);
    }

    /// <summary>Evict <i>timed</i> sessions (finished turn / error) idle longer than the TTL. Call
    /// periodically (e.g. a tray timer) so a finished-turn logo eventually drops out of the count.
    /// Sticky sessions (blocked on your approval) are never evicted here.</summary>
    public void Sweep(DateTime nowUtc)
    {
        int before, after;
        lock (_gate)
        {
            before = CountNeedsYou();
            SweepLocked(nowUtc);
            after = CountNeedsYou();
        }
        if (after != before) WaitingChanged?.Invoke(after);
    }

    void SweepLocked(DateTime nowUtc)
    {
        List<string>? dead = null;
        foreach (var kv in _sessions)
            if (!kv.Value.Sticky && kv.Value.PendingInputs.Count == 0 && nowUtc - kv.Value.LastUtc > _ttl)
                (dead ??= new()).Add(kv.Key);
        if (dead is not null)
            foreach (var k in dead) _sessions.Remove(k);
    }

    int CountNeedsYou()
    {
        int n = 0;
        foreach (var e in _sessions.Values) if (e.IsWaiting) n++;
        return n;
    }

    // A null sessionId still wants per-session-ish granularity, so fall back to the project path.
    static string Key(string tool, string? sessionId, string? project)
        => !string.IsNullOrEmpty(sessionId) ? $"{tool}:{sessionId}"
         : !string.IsNullOrEmpty(project)   ? $"{tool}:proj:{project}"
         : tool;
}
