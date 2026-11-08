namespace WormsCursor.Core;

/// <summary>The normalised lifecycle event a bridge reports for one agent session. Tool-specific
/// raw events (Claude's and Codex's <c>Stop</c>, etc.) are mapped to these
/// by the <c>hook</c> bridge before they reach the pipe; this enum is the source of truth.</summary>
public enum AgentEventKind
{
    /// <summary>The agent started working on your prompt (Claude <c>UserPromptSubmit</c>). Clears "needs you".</summary>
    ThinkingStarted,
    /// <summary>An agent is blocked on you mid-turn. Without item ids it is a prompt owned by <i>the
    /// reporting agent</i> (main agent or one subagent) about one of its tool calls — a permission
    /// request (Claude and Codex <c>PermissionRequest</c>) or a question tool (Claude
    /// <c>AskUserQuestion</c>, Codex <c>request_user_input</c>). Call ids name the call it blocks; that
    /// call finishing (<see cref="ToolUse"/> naming it) or the agent's whole batch of calls resolving
    /// (<see cref="ToolsResolved"/>) answers it. Without call ids the call is unknown, so any tool
    /// activity of that agent answers it. With item ids it is a prompt that has its own identity and
    /// its own answer event, such as a Claude MCP elicitation, which is not attributed to an agent;
    /// <see cref="InputAnswered"/> with the same id resolves it. Either way it is held
    /// <b>indefinitely</b> (never swept by the idle timeout) until resolved, the turn ends, or the
    /// session ends.</summary>
    AwaitingUser,
    /// <summary>The agent finished its turn; the ball is in your court (Claude <c>Stop</c>,
    /// Codex <c>Stop</c>, Claude's <c>idle_prompt</c> reminder). Unlike <see cref="AwaitingUser"/>
    /// this just needs a fresh prompt, so it lingers then auto-clears once the idle timeout elapses.</summary>
    TurnComplete,
    /// <summary>An agent is actively using a tool (Pre/PostToolUse). Clears the finished-turn notice.
    /// Call ids name the call that just finished, which answers that agent's prompt about that call; a
    /// prompt about an unknown call is answered by any tool activity of the agent. Never touches a
    /// sibling subagent's prompts, a prompt about another call (a parallel call finishing says nothing
    /// about it), or an item-keyed prompt.</summary>
    ToolUse,
    /// <summary>Every tool call the agent had in flight has resolved — run, failed, or denied — and
    /// the results go back to the model (Claude <c>PostToolBatch</c>). Answers all of that agent's
    /// prompts: none of its calls can still be waiting on you.</summary>
    ToolsResolved,
    /// <summary>The turn ended on an error (Claude <c>StopFailure</c>). A finished-turn notice.</summary>
    Error,
    /// <summary>The session ended (Claude <c>SessionEnd</c> — /exit, Ctrl+C, /clear, logout). Drops
    /// the session entirely so its logo disappears at once instead of lingering until the TTL.</summary>
    SessionEnded,
    /// <summary>Questions were displayed while the agent continues working. Tool activity and turn
    /// completion do not answer them; they remain pending until user input arrives.</summary>
    InputRequested,
    /// <summary>The user answered specific items: asynchronous questions, or item-keyed
    /// <see cref="AwaitingUser"/> prompts.</summary>
    InputAnswered,
}

/// <summary>Why a waiting session wants you.</summary>
public enum WaitingState
{
    /// <summary>The agent is done (turn complete, idle reminder, or error) and just needs a fresh
    /// prompt. Timed: swept after the idle timeout.</summary>
    Finished,
    /// <summary>The agent is blocked on you: an approval, a question, an elicitation, or an
    /// unanswered asynchronous question is pending. Sticky: never swept.</summary>
    Blocked,
}

/// <summary>One session that currently needs the user: which tool's logo to show and why.</summary>
public readonly record struct WaitingAgent(string Tool, WaitingState State);

/// <summary>
/// Tracks which agent sessions currently <b>need the user's attention</b>, across every connected
/// tool, and why. UI-agnostic and thread-safe: the pipe-listener thread calls <see cref="Report"/>;
/// the tray reads <see cref="Waiting"/> / <see cref="WaitingCount"/> and pushes them into the overlay.
///
/// A session needs you while any of these is pending:
/// <list type="bullet">
/// <item>a <b>finished-turn notice</b> — <see cref="AgentEventKind.TurnComplete"/> / <see cref="AgentEventKind.Error"/>
/// (your move), cleared when work resumes (<see cref="AgentEventKind.ThinkingStarted"/> / <see cref="AgentEventKind.ToolUse"/>).
/// This alone makes the session <see cref="WaitingState.Finished"/>;</item>
/// <item><b>agent prompts</b> — <see cref="AgentEventKind.AwaitingUser"/> without item ids, one per blocked tool
/// call. Subagents share their parent's session id, so these are keyed by agent id and by the call they block:
/// only that call finishing, or that agent's batch of calls resolving, clears one. A sibling subagent's tool use
/// never does, and neither does the same agent's parallel call finishing first;</item>
/// <item><b>item prompts</b> — <see cref="AgentEventKind.AwaitingUser"/> with ids, each cleared only by
/// <see cref="AgentEventKind.InputAnswered"/> for that id (no agent's tool use can answer them);</item>
/// <item><b>pending questions</b> — <see cref="AgentEventKind.InputRequested"/>, until each is answered.</item>
/// </list>
/// Any of the last three makes the session <see cref="WaitingState.Blocked"/>. A new prompt clears
/// everything; a finished turn clears the mid-turn prompts (nothing can still be blocked once the turn
/// is over) but not asynchronous questions, which outlive the turn. The session is dropped outright by
/// <see cref="AgentEventKind.SessionEnded"/>.
///
/// The idle timeout (<see cref="Ttl"/>) only sweeps <b>finished</b> sessions, so a logo can't wedge the
/// count after a quiet finish. A <b>blocked</b> session is never swept: the agent really is still
/// waiting on you. (Trade-off: a hard-killed agent that was blocked leaves its logo up until you clear
/// or disable it, since no closing event ever arrives.)
/// </summary>
public sealed class AgentActivity
{
    sealed class Entry
    {
        public bool Finished;          // the timed finished-turn / error notice
        public DateTime LastUtc;
        public string Tool = "";
        // (agent, call): agent "" is the main agent, call "" a prompt whose call the tool didn't name.
        public readonly HashSet<(string Agent, string Call)> BlockedCalls = new();
        public readonly HashSet<string> BlockedItems = new(StringComparer.Ordinal);
        public readonly HashSet<string> PendingInputs = new(StringComparer.Ordinal);
        public bool IsBlocked => BlockedCalls.Count > 0 || BlockedItems.Count > 0 || PendingInputs.Count > 0;
        public bool IsWaiting => Finished || IsBlocked;
        public WaitingState State => IsBlocked ? WaitingState.Blocked : WaitingState.Finished;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _sessions = new();
    TimeSpan _ttl;

    public AgentActivity(TimeSpan? ttl = null) => _ttl = ttl ?? TimeSpan.FromMinutes(1);

    /// <summary>The idle timeout after which a <see cref="WaitingState.Finished"/> session with no
    /// further events is swept out, so a finished-turn logo can't linger forever. Blocked sessions are
    /// sticky and ignore this. Settable live from the preferences UI.</summary>
    public TimeSpan Ttl
    {
        get { lock (_gate) return _ttl; }
        set { lock (_gate) _ttl = value; }
    }

    /// <summary>Raised outside the lock when <see cref="Waiting"/> changes (a session starts or stops
    /// waiting, or moves between finished and blocked), with the current count.</summary>
    public event Action<int>? WaitingChanged;

    /// <summary>How many distinct agent sessions currently need the user.</summary>
    public int WaitingCount { get { lock (_gate) return SnapshotLocked().Count; } }

    /// <summary>Every session that currently needs the user, one entry per session: its tool id (e.g.
    /// <c>claude-code</c>, <c>codex</c>) and whether it is blocked on you or merely finished. A snapshot
    /// taken under the lock, safe to hand to another thread.</summary>
    public IReadOnlyList<WaitingAgent> Waiting { get { lock (_gate) return SnapshotLocked(); } }

    /// <summary>Apply one normalised event for a session. <paramref name="sessionId"/> may be null
    /// (we then key by tool+project). <paramref name="agentId"/> names the subagent that raised the
    /// event (null = the main agent). <paramref name="itemIds"/> name the questions or item prompts the
    /// event opens or answers. <paramref name="callIds"/> name the tool call an agent prompt blocks
    /// (<see cref="AgentEventKind.AwaitingUser"/>), or the call that just finished
    /// (<see cref="AgentEventKind.ToolUse"/>). <paramref name="nowUtc"/> is injected so callers/tests
    /// stay deterministic. Safe to call from any thread.</summary>
    public void Report(string tool, string? sessionId, string? project, AgentEventKind kind, DateTime nowUtc,
        IReadOnlyList<string>? itemIds = null, string? agentId = null, IReadOnlyList<string>? callIds = null)
    {
        if (string.IsNullOrEmpty(tool)) tool = "unknown";
        int after;
        bool changed;
        var key = Key(tool, sessionId, project);
        lock (_gate)
        {
            var before = SnapshotLocked();
            SweepLocked(nowUtc);
            if (kind == AgentEventKind.SessionEnded)
                _sessions.Remove(key); // the session is gone — forget it, don't wait on it
            else
            {
                if (!_sessions.TryGetValue(key, out var e)) { e = new Entry(); _sessions[key] = e; }
                e.Tool = tool;
                e.LastUtc = nowUtc;
                Apply(e, kind, itemIds ?? Array.Empty<string>(), agentId ?? "", callIds ?? Array.Empty<string>());
            }
            var now = SnapshotLocked();
            after = now.Count;
            changed = !before.SequenceEqual(now);
        }
        if (changed) WaitingChanged?.Invoke(after);
    }

    static void Apply(Entry e, AgentEventKind kind, IReadOnlyList<string> items, string agent, IReadOnlyList<string> calls)
    {
        switch (kind)
        {
            case AgentEventKind.InputRequested:
                e.PendingInputs.UnionWith(items);
                return; // questions shown mid-work don't touch the finished notice
            case AgentEventKind.InputAnswered:
                e.PendingInputs.ExceptWith(items);
                e.BlockedItems.ExceptWith(items);
                // An answer acknowledges a finished turn, but not an independent agent prompt.
                e.Finished = false;
                return;
            case AgentEventKind.AwaitingUser:
                if (items.Count > 0) e.BlockedItems.UnionWith(items);
                else if (calls.Count == 0) e.BlockedCalls.Add((agent, ""));
                else foreach (string call in calls) e.BlockedCalls.Add((agent, call));
                break;
            case AgentEventKind.ToolUse:
                // Only this agent's prompt about the call that finished (or about a call nobody named)
                // is answered: a sibling subagent, or a parallel call of the same agent, may still be
                // blocked on you, and an item prompt waits for its own answer.
                e.BlockedCalls.RemoveWhere(p => p.Agent == agent && (p.Call.Length == 0 || calls.Contains(p.Call)));
                break;
            case AgentEventKind.ToolsResolved:
                e.BlockedCalls.RemoveWhere(p => p.Agent == agent);
                break;
            case AgentEventKind.ThinkingStarted:
                e.BlockedCalls.Clear();
                e.BlockedItems.Clear();
                e.PendingInputs.Clear();
                break;
            default: // TurnComplete / Error: the turn is over, so nothing mid-turn is still blocked
                e.BlockedCalls.Clear();
                e.BlockedItems.Clear();
                break;
        }
        e.Finished = kind is AgentEventKind.TurnComplete or AgentEventKind.Error;
    }

    /// <summary>Evict <see cref="WaitingState.Finished"/> sessions idle longer than the TTL. Call
    /// periodically (e.g. a tray timer) so a finished-turn logo eventually drops out of the count.
    /// Blocked sessions are never evicted here.</summary>
    public void Sweep(DateTime nowUtc)
    {
        int after;
        bool changed;
        lock (_gate)
        {
            var before = SnapshotLocked();
            SweepLocked(nowUtc);
            var now = SnapshotLocked();
            after = now.Count;
            changed = !before.SequenceEqual(now);
        }
        if (changed) WaitingChanged?.Invoke(after);
    }

    void SweepLocked(DateTime nowUtc)
    {
        List<string>? dead = null;
        foreach (var kv in _sessions)
            if (!kv.Value.IsBlocked && nowUtc - kv.Value.LastUtc > _ttl)
                (dead ??= new()).Add(kv.Key);
        if (dead is not null)
            foreach (var k in dead) _sessions.Remove(k);
    }

    List<WaitingAgent> SnapshotLocked()
    {
        var list = new List<WaitingAgent>();
        foreach (var e in _sessions.Values)
            if (e.IsWaiting) list.Add(new WaitingAgent(e.Tool, e.State));
        return list;
    }

    // A null sessionId still wants per-session-ish granularity, so fall back to the project path.
    static string Key(string tool, string? sessionId, string? project)
        => !string.IsNullOrEmpty(sessionId) ? $"{tool}:{sessionId}"
         : !string.IsNullOrEmpty(project)   ? $"{tool}:proj:{project}"
         : tool;
}
