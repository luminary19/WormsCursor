namespace AgentsCursor.Core;

/// <summary>How a wait (a session needing you, in one <see cref="WaitingState"/>) ended.</summary>
public enum WaitEnd
{
    /// <summary>A needs-you wait was answered: the blocked call went ahead, was denied, the question
    /// was answered, or the turn moved on (a new prompt, the turn ending). Only these count towards
    /// the average response time.</summary>
    Answered,
    /// <summary>A done wait ended because the session moved on: you sent a new prompt, work started
    /// again, or it went on to need you.</summary>
    Resumed,
    /// <summary>A done wait timed out: the linger timeout cleared its token before you came back.</summary>
    Expired,
    /// <summary>Tracking ended: the session ended (exit, interrupt), its plugin host closed, or
    /// AgentsCursor stopped.</summary>
    Ended,
}

/// <summary>One finished wait, as the history keeps it: which agent, needs you (<see
/// cref="WaitingState.Blocked"/>) or done (<see cref="WaitingState.Finished"/>), when its token
/// appeared and went, and why it went. Nothing else: no session id, prompt, project or path.</summary>
public readonly record struct WaitRecord(string Tool, WaitingState Kind, DateTime StartUtc, DateTime EndUtc, WaitEnd Outcome)
{
    /// <summary>How long it waited (never negative, even if the clock stepped back).</summary>
    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
}
