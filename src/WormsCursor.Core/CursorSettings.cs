namespace WormsCursor.Core;

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

/// <summary>
/// Tunable parameters for the agent-notifier token (the bouncing tool logo shown while an AI agent is
/// waiting for you). What was once a cursor-theming engine is now a single floating overlay, so these
/// settings only describe that token: its size, where it lives, and how long a stuck logo lingers.
/// Persisted as JSON by <see cref="SettingsStore"/>; additive/tolerant load (unknown fields ignored,
/// missing fields default), so an older settings file upgrades cleanly.
/// </summary>
public sealed class CursorSettings
{
    /// <summary>Schema version — bump when the shape changes in a non-additive way so
    /// a future load can migrate. Additive changes are handled by tolerant JSON.</summary>
    public int Version { get; set; } = 1;

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

    // ---------- app state (persisted, not shown in the UI) ----------

    /// <summary>The app version whose "What's new" notes have already been shown, so the
    /// post-update changelog pops only once. Empty until first recorded.</summary>
    public string LastSeenVersion { get; set; } = "";

    /// <summary>Clamp values into safe ranges (e.g. after loading a hand-edited or
    /// migrated file) so bad input can't break rendering.</summary>
    public void Normalize()
    {
        Size = Math.Clamp(Size, 48, 192);
        AgentNotifierTimeoutSeconds = Math.Clamp(AgentNotifierTimeoutSeconds, 10, 1800);
    }

    /// <summary>An independent copy. Every field is a value type, an immutable string, or the read-only
    /// <see cref="SuppressedPrograms"/> list (replaced on edit, never changed in place), so a member-wise
    /// clone is already a safe working copy for the dialog.</summary>
    public CursorSettings Clone() => (CursorSettings)MemberwiseClone();

    /// <summary>Copy the values the Preferences dialog edits (token appearance and the suppressed
    /// programs) from <paramref name="other"/> into this instance, so a reference held by the overlay
    /// sees the change without being replaced. Everything else is deliberately left alone: the
    /// agent-notifier enable + linger timeout have their own owner (the Agent settings dialog and the
    /// tray toggle), which may have changed them <i>after</i> the Preferences working copy was cloned —
    /// copying them here would revert those edits with a stale snapshot. <see cref="LastSeenVersion"/>
    /// is app state.</summary>
    public void CopyPreferencesFrom(CursorSettings other)
    {
        Size = other.Size;
        OutlineColor = other.OutlineColor;
        Placement = other.Placement;
        Corner = other.Corner;
        SuppressedPrograms = other.SuppressedPrograms;
    }
}
