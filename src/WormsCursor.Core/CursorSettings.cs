namespace WormsCursor.Core;

/// <summary>
/// Tunable parameters for the rotating-cursor engine. Appearance fields are what the
/// preferences UI edits; the behaviour fields are engine tuning (not yet exposed).
/// Persisted as JSON by <see cref="SettingsStore"/>.
/// </summary>
public sealed class CursorSettings
{
    /// <summary>Schema version — bump when the shape changes in a non-additive way so
    /// a future load can migrate. Additive changes are handled by tolerant JSON.</summary>
    public int Version { get; set; } = 1;

    // ---------- appearance (preferences UI) ----------

    /// <summary>Cursor bitmap size in px (square; hotspot at the centre). The arrow
    /// geometry scales with this, keeping proportions at any size.</summary>
    public int Size { get; set; } = 64;

    /// <summary>Fill colour as an HTML hex string, e.g. "#FFFFFF".</summary>
    public string FillColor { get; set; } = "#FFFFFF";

    /// <summary>Outline colour as an HTML hex string, e.g. "#000000".</summary>
    public string OutlineColor { get; set; } = "#000000";

    /// <summary>Outline thickness at the reference size (64); scales with <see cref="Size"/>.
    /// 0 = no outline.</summary>
    public double OutlineThickness { get; set; } = 1.4;

    /// <summary>Corner rounding radius at the reference size (64); scales with
    /// <see cref="Size"/>. 0 = sharp corners.</summary>
    public double CornerRadius { get; set; } = 0.0;

    /// <summary>Click feedback: the pointer does a little "squash &amp; pop" while a mouse
    /// button is held, and the I-beam hops as you type. On by default.</summary>
    public bool ClickFeedback { get; set; } = true;

    // ---------- which cursors are themed (preferences UI) ----------

    /// <summary>Cursor kinds the user has switched OFF — left as the Windows default
    /// instead of themed. Stored by name (the <see cref="TestCursor"/> value, e.g.
    /// "Wait"); any kind NOT listed is enabled, so every cursor is themed by default and
    /// a newly-added cursor lights up automatically. Additive/tolerant JSON.</summary>
    public List<string> DisabledCursors { get; set; } = new();

    /// <summary>True if <paramref name="kind"/> should be themed (the default).
    /// <see cref="TestCursor.Off"/> isn't a themed cursor, so it always reads enabled.</summary>
    public bool IsCursorEnabled(TestCursor kind)
        => kind == TestCursor.Off
            || !DisabledCursors.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Turn theming of <paramref name="kind"/> on/off (no-op for
    /// <see cref="TestCursor.Off"/>); keeps the list duplicate-free.</summary>
    public void SetCursorEnabled(TestCursor kind, bool enabled)
    {
        if (kind == TestCursor.Off) return;
        DisabledCursors.RemoveAll(s => string.Equals(s, kind.ToString(), StringComparison.OrdinalIgnoreCase));
        if (!enabled) DisabledCursors.Add(kind.ToString());
    }

    // ---------- app state (persisted, not shown in the UI) ----------

    /// <summary>The app version whose "What's new" notes have already been shown, so the
    /// post-update changelog pops only once. Empty until first recorded.</summary>
    public string LastSeenVersion { get; set; } = "";

    // ---------- behaviour (engine tuning; not in the UI yet) ----------

    public int Steps { get; set; } = 360;
    public int Hz { get; set; } = 144;
    public double AimDist { get; set; } = 8.0;
    public double AimSmooth { get; set; } = 0.50;
    public double HystDeg { get; set; } = 3.0;
    public int IdleReset { get; set; } = 60;
    public double TurnDps { get; set; } = 720;
    public bool Debug { get; set; } = false;

    /// <summary>Clamp values into safe ranges (e.g. after loading a hand-edited or
    /// migrated file) so bad input can't break rendering.</summary>
    public void Normalize()
    {
        Size = Math.Clamp(Size, 24, 256);
        OutlineThickness = Math.Clamp(OutlineThickness, 0, 4);
        CornerRadius = Math.Clamp(CornerRadius, 0, 12);
        Steps = Math.Clamp(Steps, 8, 720);
        Hz = Math.Clamp(Hz, 30, 240);
        AimSmooth = Math.Clamp(AimSmooth, 0.05, 1.0);
    }

    public CursorSettings Clone()
    {
        var copy = (CursorSettings)MemberwiseClone();
        copy.DisabledCursors = new List<string>(DisabledCursors); // own list, so dialog edits don't alias the live settings
        return copy;
    }

    /// <summary>Copy all values from <paramref name="other"/> into this instance (so a
    /// reference held by the engine sees the change without being replaced).</summary>
    public void CopyFrom(CursorSettings other)
    {
        Version = other.Version;
        Size = other.Size;
        FillColor = other.FillColor;
        OutlineColor = other.OutlineColor;
        OutlineThickness = other.OutlineThickness;
        CornerRadius = other.CornerRadius;
        ClickFeedback = other.ClickFeedback;
        DisabledCursors = new List<string>(other.DisabledCursors);
        Steps = other.Steps;
        Hz = other.Hz;
        AimDist = other.AimDist;
        AimSmooth = other.AimSmooth;
        HystDeg = other.HystDeg;
        IdleReset = other.IdleReset;
        TurnDps = other.TurnDps;
        Debug = other.Debug;
    }
}
