namespace AgentsCursor.Core;

/// <summary>Why the token isn't on screen, in the order they are checked.</summary>
public enum HideReason
{
    /// <summary>It is on screen.</summary>
    None,
    /// <summary>No agent is waiting on you.</summary>
    NothingWaiting,
    /// <summary>The token is turned off (the master switch, or the tray's Enabled).</summary>
    TurnedOff,
    /// <summary>An app on the hide-while-using list is in front.</summary>
    AppInFront,
    /// <summary>You're presenting, sharing your screen or in a call, and the token isn't set to show then.</summary>
    Meeting,
}

/// <summary>
/// Whether the token is on screen and, when it isn't, why: the one place every reason to hide it is
/// decided, for the overlay (show or park), the sounds (a chime only plays for a token you can see)
/// and the panel (which says when a meeting is hiding it). Pure and immutable.
/// </summary>
public sealed record TokenVisibility(HideReason Reason, Meeting? Meeting = null)
{
    /// <summary>Nothing is waiting.</summary>
    public static readonly TokenVisibility Idle = new(HideReason.NothingWaiting);

    /// <summary>True when the token is on screen.</summary>
    public bool Shown => Reason == HideReason.None;

    /// <summary>Whether the meeting signals matter right now: something waits, the token is on, no
    /// listed app is in front, and it isn't set to show during meetings. Only then are they checked
    /// (<see cref="MeetingWatcher"/>).</summary>
    public static bool NeedsMeetingCheck(bool waiting, bool enabled, bool appInFront, bool showDuringMeetings)
        => waiting && enabled && !appInFront && !showDuringMeetings;

    /// <summary>Decides: nothing waiting, then turned off, then an app in front, then a meeting
    /// (<paramref name="meeting"/>, ignored when <paramref name="showDuringMeetings"/>), else shown.</summary>
    public static TokenVisibility Decide(bool waiting, bool enabled, bool appInFront, bool showDuringMeetings, Meeting? meeting)
    {
        if (!waiting) return Idle;
        if (!enabled) return new(HideReason.TurnedOff);
        if (appInFront) return new(HideReason.AppInFront);
        if (!showDuringMeetings && meeting is not null) return new(HideReason.Meeting, meeting);
        return new(HideReason.None);
    }
}
