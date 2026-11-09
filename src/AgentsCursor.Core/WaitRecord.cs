namespace AgentsCursor.Core;

/// <summary>How a wait (a session needing you, in one <see cref="WaitingState"/>) ended.</summary>
public enum WaitEnd
{
    /// <summary>A needs-you wait was answered, and its end is the moment you answered: you sent a
    /// prompt, answered a question or form, or approved a call that then reported how long it ran (it
    /// ends when the call started). Only these count towards the typical response time.</summary>
    Answered,
    /// <summary>A needs-you wait ended because the agent carried on, which says you answered but not
    /// when: an approved call finished without saying how long it ran (Codex never does), a denial
    /// resolved its batch of calls, or the turn ended. Its end includes the agent's own work after
    /// your answer, so it isn't timed.</summary>
    Proceeded,
    /// <summary>A done wait ended because the session moved on: you sent a new prompt, work started
    /// again, or it went on to need you.</summary>
    Resumed,
    /// <summary>A done wait timed out: the linger timeout cleared its token before you came back.</summary>
    Expired,
    /// <summary>Tracking ended: the session ended (exit, interrupt), its plugin host closed, or
    /// AgentsCursor stopped; or nobody answered a needs-you wait because the turn failed under it.</summary>
    Ended,
}

/// <summary>One finished wait, as the history keeps it: which agent, needs you (<see
/// cref="WaitingState.Blocked"/>) or done (<see cref="WaitingState.Finished"/>), when its token
/// appeared and when the wait ended (for an <see cref="WaitEnd.Answered"/> one, when you answered),
/// and why it ended. Nothing else: no session id, prompt, project or path.</summary>
public readonly record struct WaitRecord(string Tool, WaitingState Kind, DateTime StartUtc, DateTime EndUtc, WaitEnd Outcome)
{
    /// <summary>How long it waited (never negative, even if the clock stepped back).</summary>
    public TimeSpan Duration => EndUtc > StartUtc ? EndUtc - StartUtc : TimeSpan.Zero;
}
