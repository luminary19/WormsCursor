using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the agent-notifier indicator: a single waiting tool's logo (Claude Code's critter, Codex's
/// OpenAI knot — see <see cref="AgentLogos"/>) hung on the cursor as a bare, free-floating glyph, with
/// a small "+N" superscript when more than one agent is waiting.
///
/// It is deliberately drawn at the <b>exact same spot and on the exact same pendulum</b> as the busy
/// ring / help "?" — the engine passes in that pendulum's bob (<c>ringCX/ringCY</c>) and its swing
/// angle, so a logo behaves 1:1 with those elements, as if it were simply painted onto them. No tile,
/// no visible string: just the logo, swinging. Composited onto whatever cursor bitmap is on screen,
/// so every themed cursor carries it uniformly.
///
/// There is intentionally never a fan of logos — multiple waiting agents collapse to one logo plus a
/// "+N" count, which stays legible on the tiny cursor canvas.
/// </summary>
public static class NotifierRenderer
{
    /// <summary>Draws a single logo (the first entry in <paramref name="tools"/>, tool ids like
    /// "claude-code" / "codex") centred on the pendulum bob (<paramref name="bobX"/>/<paramref
    /// name="bobY"/>, canvas px) and tilted by <paramref name="swingDeg"/> (0 = upright at rest) — the
    /// same bob and swing the busy ring / help "?" use. When more than one agent waits, a small "+N"
    /// superscript (N = the rest) sits at the logo's top-right. Leaves no transform behind.</summary>
    public static void DrawCharms(Graphics g, CursorSettings s, IReadOnlyList<string> tools,
                                  float bobX, float bobY, float swingDeg)
    {
        if (tools is null || tools.Count == 0) return;
        int count = tools.Count;
        int sz = Math.Max(8, s.Size);

        var outline = Parse(s.OutlineColor, Color.Black);
        float box = sz * 0.42f;            // logo footprint (~ the "?" glyph's)
        float edge = sz * 0.055f;          // contrast rim so a bare logo reads on any background

        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(bobX, bobY);
        g.RotateTransform(swingDeg);       // tilt exactly like the "?" on the same string

        // Always exactly one logo (the most recent waiting tool); never a fan.
        AgentLogos.Draw(g, tools[0], new RectangleF(-box / 2f, -box / 2f, box, box), edge);
        if (count > 1)
            DrawPlusN(g, count - 1, box * 0.40f, -box * 0.34f, sz, outline);

        g.Restore(saved);
    }

    // A small outlined "+N" (white fill / theme outline, like the other glyphs) standing in for the
    // other waiting agents — a frameless superscript at the single logo's top-right corner.
    static void DrawPlusN(Graphics g, int extra, float cx, float cy, int sz, Color outline)
    {
        using var path = new GraphicsPath();
        float em = sz * 0.20f;
        using var fam = new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        path.AddString("+" + extra, fam, (int)FontStyle.Bold, em, new PointF(cx, cy), fmt);

        using (var po = new Pen(outline, Math.Max(1.5f, sz * 0.03f)) { LineJoin = LineJoin.Round })
            g.DrawPath(po, path);
        using (var bf = new SolidBrush(Color.White))
            g.FillPath(bf, path);
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
