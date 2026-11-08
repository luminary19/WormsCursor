namespace AgentsCursor.Core;

/// <summary>Where the bouncing agent token is drawn while an agent is waiting.</summary>
public enum NotifierPlacement
{
    /// <summary>The token hangs next to the mouse pointer and follows it (pendulum bounce).</summary>
    Cursor,
    /// <summary>The token sits in a fixed screen corner and bounces in place.</summary>
    Corner,
}

/// <summary>Which screen corner the <see cref="NotifierPlacement.Corner"/> token pins to.</summary>
public enum ScreenCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>How the control panel looks: following Windows' light/dark setting, or always one of them.</summary>
public enum ThemePreference { System, Light, Dark }

/// <summary>
/// Tunable parameters for the agent-notifier token (the bouncing tool logo shown while an AI agent is
/// waiting for you). What was once a cursor-theming engine is now a single floating overlay, so these
/// settings mostly describe that token: its size, where it lives, and how long a stuck logo lingers
/// (plus the hidden-while-using programs and the control panel's theme).
/// Persisted as JSON by <see cref="SettingsStore"/>; additive/tolerant load (unknown fields ignored,
/// missing fields default), so an older settings file upgrades cleanly.
/// </summary>
public sealed class CursorSettings
{
    /// <summary>Schema version — bump when the shape changes in a non-additive way so
    /// a future load can migrate. Additive changes are handled by tolerant JSON.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The range <see cref="Size"/> is clamped to.</summary>
    public const int MinSize = 48, MaxSize = 192;

    /// <summary>The range <see cref="AgentNotifierTimeoutSeconds"/> is clamped to.</summary>
    public const int MinTimeoutSeconds = 10, MaxTimeoutSeconds = 1800;

    // ---------- token appearance (preferences UI) ----------

    /// <summary>Token size in px (logical, at 96 DPI — scaled up on high-DPI monitors). The logo
    /// and its swing space scale with this.</summary>
    public int Size { get; set; } = 96;

    /// <summary>Outline colour as an HTML hex string, e.g. "#000000" — used for the small "+N"
    /// badge drawn when more than one agent is waiting.</summary>
    public string OutlineColor { get; set; } = "#000000";

    // ---------- agent notifier (preferences UI) ----------

    /// <summary>Show the waiting tool's logo while AI agents are waiting for the user (always a single
    /// logo plus a "+N" count when several wait). On by default; the token only appears once a hook is
    /// registered and an agent reports an event.</summary>
    public bool AgentNotifierEnabled { get; set; } = true;

    /// <summary>How long a waiting agent's logo lingers before it's swept, in seconds, if the agent
    /// never sends a closing event. Default 20s; clamped to 10s–30min.</summary>
    public int AgentNotifierTimeoutSeconds { get; set; } = 20;

    /// <summary>Where the token is drawn: hanging off the mouse pointer, or pinned to a screen
    /// corner. Defaults to following the cursor.</summary>
    public NotifierPlacement Placement { get; set; } = NotifierPlacement.Cursor;

    /// <summary>Which corner the token pins to when <see cref="Placement"/> is
    /// <see cref="NotifierPlacement.Corner"/>. Defaults to the bottom-right.</summary>
    public ScreenCorner Corner { get; set; } = ScreenCorner.BottomRight;

    /// <summary>Programs (exe file names, e.g. "dota2.exe") that hide the token while one of their
    /// windows is in the foreground, in any window mode. Agents keep being tracked meanwhile; the token
    /// comes back when focus moves elsewhere and something is still waiting. Every assignment is
    /// normalized by <see cref="ProgramSuppression.Normalize"/> into a fresh read-only list, so a JSON
    /// <c>null</c>, a full path or a missing ".exe" all load sanely, and settings copies can share the
    /// list safely. A saved empty list stays empty; a file without the field gets the default.</summary>
    public IReadOnlyList<string> SuppressedPrograms
    {
        get => _suppressedPrograms;
        set => _suppressedPrograms = ProgramSuppression.Normalize(value);
    }
    IReadOnlyList<string> _suppressedPrograms = ProgramSuppression.Defaults;

    // ---------- control panel (preferences UI) ----------

    /// <summary>The control panel's theme. Defaults to following Windows.</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    // ---------- app state (persisted, not shown in the UI) ----------

    /// <summary>The app version whose "What's new" notes have already been shown, so the
    /// post-update changelog pops only once. Empty until first recorded.</summary>
    public string LastSeenVersion { get; set; } = "";

    /// <summary>Clamp values into safe ranges (e.g. after loading a hand-edited or
    /// migrated file) so bad input can't break rendering.</summary>
    public void Normalize()
    {
        Size = Math.Clamp(Size, MinSize, MaxSize);
        AgentNotifierTimeoutSeconds = Math.Clamp(AgentNotifierTimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);
        if (!Enum.IsDefined(Theme)) Theme = ThemePreference.System; // a hand-edited number out of range
    }

    /// <summary>Puts the token's look back to the defaults: size, placement, corner and outline
    /// colour. Leaves the notifier switch, the linger timeout and the hidden-while-using list alone.</summary>
    public void ResetAppearance()
    {
        var defaults = new CursorSettings();
        Size = defaults.Size;
        Placement = defaults.Placement;
        Corner = defaults.Corner;
        OutlineColor = defaults.OutlineColor;
    }

    /// <summary>True when the look <see cref="ResetAppearance"/> resets is already the default. (A
    /// method, not a property, so it stays out of the saved JSON.)</summary>
    public bool HasDefaultAppearance()
    {
        var defaults = new CursorSettings();
        return Size == defaults.Size && Placement == defaults.Placement && Corner == defaults.Corner
               && string.Equals(OutlineColor, defaults.OutlineColor, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An independent copy. Every field is a value type, an immutable string, or the read-only
    /// <see cref="SuppressedPrograms"/> list (replaced on edit, never changed in place), so a member-wise
    /// clone is already a safe snapshot (<see cref="LiveSettings.Current"/>).</summary>
    public CursorSettings Clone() => (CursorSettings)MemberwiseClone();
}
