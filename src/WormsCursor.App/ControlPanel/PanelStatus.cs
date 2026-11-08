namespace WormsCursor.App.ControlPanel;

/// <summary>How the dot beside the at-a-glance status is coloured.</summary>
public enum StatusTone { Off, Idle, Attention, Paused }

/// <summary>
/// What the control panel's at-a-glance status shows: whether the token is on, how many agent sessions
/// wait (and how many of those are blocked on you), and the listed program, if any, that is hiding the
/// token because it is in front. Pure, so the wording is unit-tested.
/// </summary>
public sealed record PanelStatus(bool Enabled, int Waiting, int Blocked, string? HiddenBy)
{
    /// <summary>The one-line status, e.g. "On · 2 agents need you".</summary>
    public string Text
    {
        get
        {
            if (!Enabled) return "Off";
            if (HiddenBy is not null) return $"Hidden while {HiddenBy} is in front";
            if (Blocked > 0) return Blocked == 1 ? "On · 1 agent needs you" : $"On · {Blocked} agents need you";
            if (Waiting > 0) return Waiting == 1 ? "On · 1 agent is done" : $"On · {Waiting} agents are done";
            return "On · No agents waiting";
        }
    }

    public StatusTone Tone =>
        !Enabled ? StatusTone.Off
        : HiddenBy is not null ? StatusTone.Paused
        : Waiting > 0 ? StatusTone.Attention
        : StatusTone.Idle;
}
