using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// The Analytics charts' colours, one set per theme (each chosen for its own surface, not flipped).
/// The heatmap is one green ramp, near-zero to busiest. The agents chart gives each agent one fixed
/// colour: Claude Code and Pi their brand hues; Codex and OpenCode have monochrome marks (black,
/// grey), which can't carry identity in a chart (they read as grey, match each other and vanish on
/// the dark theme), so they take the blue and aqua of the reference chart palette. In the ring's fixed
/// order (Claude Code, Codex, OpenCode, Pi, round to Claude Code) every neighbouring pair passes the
/// colour-blind separation check in both themes; the legend names each one beside its swatch, so
/// colour is never the only cue. In the dark ramp an empty day sits just above the card (about 1.2:1,
/// as in GitHub's light ramp) and every level is about 1.5:1 lighter than the one before, so the
/// quietest active day stands out by lightness, not by hue alone.
/// </summary>
static class ChartColors
{
    // First: the ramps below are built through it (static fields initialise in order).
    static readonly Dictionary<string, Brush> Cache = new(StringComparer.Ordinal);

    public static bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    static readonly Brush[] LightRamp = Brushes("#EBEDF0", "#9BE9A8", "#40C463", "#30A14E", "#216E39");
    static readonly Brush[] DarkRamp = Brushes("#3E3E3E", "#1D7139", "#1A9144", "#27B24D", "#3DD85B");

    /// <summary>The heatmap fill for a level, 0 (no activity) to 4 (busiest).</summary>
    public static Brush Heat(int level) => (IsDark ? DarkRamp : LightRamp)[Math.Clamp(level, 0, 4)];

    /// <summary>The empty ring of the agents chart (nothing to show).</summary>
    public static Brush Track => Heat(0);

    public static Brush Agent(string tool)
    {
        bool dark = IsDark;
        return tool switch
        {
            "claude-code" => Brush(dark ? "#D06A48" : "#D97757"),
            "codex" => Brush(dark ? "#3987E5" : "#2A78D6"),
            "opencode" => Brush(dark ? "#199E70" : "#1BAF7A"),
            "pi" => Brush(dark ? "#C04FC2" : "#B83DBA"),
            _ => Brush(dark ? "#8A8A8A" : "#767676"),
        };
    }

    static Brush Brush(string hex)
    {
        if (!Cache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
        }
        return brush;
    }

    static Brush[] Brushes(params string[] hex) => hex.Select(Brush).ToArray();
}
