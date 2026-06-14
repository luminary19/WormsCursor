using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the agent-notifier indicator: the logo of each waiting tool (Claude Code's critter, Codex's
/// OpenAI knot — see <see cref="AgentLogos"/>) hung on the cursor as a bare, free-floating glyph.
///
/// It is deliberately drawn at the <b>exact same spot and on the exact same pendulum</b> as the busy
/// ring / help "?" — the engine passes in that pendulum's bob (<c>ringCX/ringCY</c>) and its swing
/// angle, so a logo behaves 1:1 with those elements, as if it were simply painted onto them. No tile,
/// no visible string: just the logo, swinging. Composited onto whatever cursor bitmap is on screen,
/// so every themed cursor carries it uniformly.
///
/// (Multiple waiting agents currently fan from the same bob and share its swing; a future pass will
/// give each its own free, non-overlapping pendulum.)
/// </summary>
public static class NotifierRenderer
{
    /// <summary>Draws one logo per entry in <paramref name="tools"/> (tool ids like "claude-code" /
    /// "codex") centred on the pendulum bob (<paramref name="bobX"/>/<paramref name="bobY"/>, canvas
    /// px) and tilted by <paramref name="swingDeg"/> (0 = upright at rest) — the same bob and swing
    /// the busy ring / help "?" use. <paramref name="cap"/> bounds how many logos are drawn before a
    /// small "+N" stands in for the rest. Leaves no transform behind.</summary>
    public static void DrawCharms(Graphics g, CursorSettings s, IReadOnlyList<string> tools,
                                  float bobX, float bobY, float swingDeg, int cap)
    {
        if (tools is null || tools.Count == 0) return;
        int count = tools.Count;
        int sz = Math.Max(8, s.Size);
        cap = Math.Clamp(cap, 1, 6);
        int shown = Math.Min(count, cap);

        var outline = Parse(s.OutlineColor, Color.Black);
        float box = sz * 0.42f;            // logo footprint (~ the "?" glyph's)
        float spacing = box * 0.98f;       // gentle fan for multiples (they share the bob + swing)
        float edge = sz * 0.055f;          // contrast rim so a bare logo reads on any background

        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(bobX, bobY);
        g.RotateTransform(swingDeg);       // tilt exactly like the "?" on the same string

        float x0 = -(shown - 1) * spacing / 2f;
        for (int i = 0; i < shown; i++)
        {
            float x = x0 + i * spacing;
            AgentLogos.Draw(g, tools[i], new RectangleF(x - box / 2f, -box / 2f, box, box), edge);
        }

        if (count > cap)
            DrawPlusN(g, count - shown, x0 + (shown - 1) * spacing + box * 0.40f, -box * 0.34f, sz, outline);

        g.Restore(saved);
    }

    // A small outlined "+N" (white fill / theme outline, like the other glyphs) standing in for the
    // agents past the cap — drawn frameless next to the last logo.
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
