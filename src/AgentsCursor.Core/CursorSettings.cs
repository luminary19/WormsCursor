namespace AgentsCursor.Core;

/// <summary>Where the bouncing agent token is drawn while an agent is waiting. The explicit values
/// keep the numbers older settings files may hold meaning the same thing (0 was "Cursor", which is
/// now <see cref="BelowPointer"/>).</summary>
public enum NotifierPlacement
{
    /// <summary>The token hangs below the mouse pointer on a springy string and follows it.</summary>
    BelowPointer = 0,
    /// <summary>The token sits in a fixed screen corner and bounces in place.</summary>
    Corner = 1,
    /// <summary>The token floats above the pointer, like a balloon on a string.</summary>
    AbovePointer = 2,
    /// <summary>The token trails off the pointer's bottom-right, like a tooltip.</summary>
    BesidePointer = 3,
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
    /// <summary>The schema version this build writes. 2 renamed the placement "Cursor" to
    /// "BelowPointer" (<see cref="SettingsStore"/> migrates older files on load).</summary>
    public const int CurrentVersion = 2;

    /// <summary>Schema version — bump when the shape changes in a non-additive way so
    /// a future load can migrate. Additive changes are handled by tolerant JSON.</summary>
    public int Version { get; set; } = CurrentVersion;

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

    /// <summary>Where the token is drawn: below, above or beside the mouse pointer (following it), or
    /// pinned to a screen corner. Defaults to below the pointer.</summary>
    public NotifierPlacement Placement { get; set; } = NotifierPlacement.BelowPointer;

    /// <summary>Which corner the token pins to when <see cref="Placement"/> is
    /// <see cref="NotifierPlacement.Corner"/>. Defaults to the bottom-right.</summary>
    public ScreenCorner Corner { get; set; } = ScreenCorner.BottomRight;

    /// <summary>How lively the token moves. Defaults to <see cref="MotionStyle.Bouncy"/>; while Windows'
    /// animation effects are off every style moves as <see cref="MotionStyle.Still"/>
    /// (<see cref="MotionProfile.Effective"/>), whatever is saved here.</summary>
    public MotionStyle Motion { get; set; } = MotionStyle.Bouncy;

    /// <summary>Let the built-in mascots play their idle loops (<see cref="MascotLoops"/>). On by
    /// default; they stand still anyway while the token does (<see cref="MascotLoops.Animate"/>).</summary>
    public bool AnimateMascots { get; set; } = true;

    // ---------- sounds (preferences UI) ----------

    /// <summary>Play a chime when a session starts waiting on you. Off by default.</summary>
    public bool SoundsEnabled { get; set; }

    /// <summary>The chime for a session that newly needs your answer (blocked).</summary>
    public Chime NeedsYouSound { get; set; } = Chime.Rise;

    /// <summary>The chime for a session that just finished its turn.</summary>
    public Chime DoneSound { get; set; } = Chime.Settle;

    /// <summary>The range <see cref="SoundVolume"/> is clamped to.</summary>
    public const int MinVolume = 0, MaxVolume = 100;

    /// <summary>How loud the chimes play, 0-100, relative to the system volume (which is never changed).</summary>
    public int SoundVolume { get; set; } = 60;

    /// <summary>Keep showing the token while you present, share your screen or are on camera
    /// (<see cref="MeetingDetection"/>). Off by default: the token hides then.</summary>
    public bool ShowDuringMeetings { get; set; }

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

    /// <summary>The icon each agent's token shows: tool id -> icon id (a built-in
    /// <c>&lt;tool&gt;.&lt;variant&gt;</c>, or <c>custom:&lt;hash&gt;</c> for an uploaded SVG). Agents not
    /// listed show their original logo. Every assignment is normalized by
    /// <see cref="IconChoices.Normalize"/> into a fresh read-only dictionary (known tools only, unknown
    /// built-in ids and originals dropped, malformed custom ids dropped), and an edit replaces it rather
    /// than changing it in place, so settings copies can share it safely. A JSON <c>null</c> loads as
    /// "all originals".</summary>
    public IReadOnlyDictionary<string, string> Icons
    {
        get => _icons;
        set => _icons = IconChoices.Normalize(value);
    }
    IReadOnlyDictionary<string, string> _icons = IconChoices.None;

    /// <summary>The icon id <paramref name="tool"/>'s token shows: the chosen one, else its original.</summary>
    public string IconFor(string tool) => _icons.TryGetValue(tool, out var id) ? id : IconCatalog.OriginalId(tool);

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
        if (!Enum.IsDefined(Placement)) Placement = NotifierPlacement.BelowPointer;
        if (!Enum.IsDefined(Corner)) Corner = ScreenCorner.BottomRight;
        if (!Enum.IsDefined(Motion)) Motion = MotionStyle.Bouncy;
        if (!Enum.IsDefined(NeedsYouSound)) NeedsYouSound = Chime.Rise;
        if (!Enum.IsDefined(DoneSound)) DoneSound = Chime.Settle;
        SoundVolume = Math.Clamp(SoundVolume, MinVolume, MaxVolume);
        Version = CurrentVersion; // a loaded older file has been migrated by now
    }

    /// <summary>Puts the Display group of the Sound and Display page back to the defaults: the token's
    /// size, where it shows (placement and corner) and how it moves (<see cref="Motion"/>). Each
    /// agent's icon has its own reset on the Customization page, and the notifier switch, the sounds,
    /// the linger timeout, the outline colour and the hidden-while-using list are left alone.</summary>
    public void ResetDisplay()
    {
        var defaults = new CursorSettings();
        Size = defaults.Size;
        Placement = defaults.Placement;
        Corner = defaults.Corner;
        Motion = defaults.Motion;
    }

    /// <summary>True when what <see cref="ResetDisplay"/> resets is already the default. (A method, not
    /// a property, so it stays out of the saved JSON.)</summary>
    public bool HasDefaultDisplay()
    {
        var defaults = new CursorSettings();
        return Size == defaults.Size && Placement == defaults.Placement && Corner == defaults.Corner
            && Motion == defaults.Motion;
    }

    /// <summary>An independent copy. Every field is a value type, an immutable string, or a read-only
    /// collection (<see cref="SuppressedPrograms"/>, <see cref="Icons"/>: replaced on edit, never changed
    /// in place), so a member-wise clone is already a safe snapshot (<see cref="LiveSettings.Current"/>).</summary>
    public CursorSettings Clone() => (CursorSettings)MemberwiseClone();
}
