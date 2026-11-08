namespace AgentsCursor.Core;

/// <summary>
/// What the token shows for the sessions currently waiting on you: one tool's logo, which
/// <see cref="StatusBadge"/> it carries (the "?" when blocked on you, the check when finished), and
/// how many other sessions wait behind it (the "+N" count).
///
/// A session blocked on you takes priority over one that merely finished, so the logo you see is
/// the one that needs an answer. Immutable, so a host can swap it between threads freely.
/// </summary>
public sealed record TokenFace(string Tool, bool Blocked, int Others)
{
    /// <summary>The face for <paramref name="waiting"/> (one entry per waiting session, as reported
    /// by <see cref="AgentActivity.Waiting"/>), or null when nothing waits. Shows the first blocked
    /// session, else the first session; entries without a tool id are ignored.</summary>
    public static TokenFace? For(IReadOnlyList<WaitingAgent>? waiting)
    {
        if (waiting is null) return null;
        WaitingAgent? shown = null;
        int count = 0;
        foreach (var agent in waiting)
        {
            if (string.IsNullOrEmpty(agent.Tool)) continue;
            count++;
            bool blocked = agent.State == WaitingState.Blocked;
            if (shown is null || (blocked && shown.Value.State != WaitingState.Blocked)) shown = agent;
        }
        return shown is { } s ? new TokenFace(s.Tool, s.State == WaitingState.Blocked, count - 1) : null;
    }
}
