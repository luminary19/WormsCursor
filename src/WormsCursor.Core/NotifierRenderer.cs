using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the agent-notifier indicator: a little cluster of worm-charms that dangle off the cursor
/// when one or more AI agents are waiting for the user. It's stateless — the engine owns the
/// pendulum (a bob on a springy string, simulated in world coords so the charms swing when the
/// cursor moves and settle straight down at rest) and passes in the bob position + string angle,
/// exactly like <see cref="ProgressRenderer.ComposeHelp"/> does for the "?".
///
/// The count is read straight off the worms: one worm per waiting agent up to <c>cap</c>; beyond
/// that, <c>cap</c> worms plus a small "+N" so the exact number is still legible. Composited onto
/// whatever cursor bitmap is on screen, so every themed cursor carries it uniformly. The cluster
/// lives in the canvas padding below the hotspot, so it never disturbs the hotspot.
/// </summary>
public static class NotifierRenderer
{
    /// <summary>Draws <paramref name="count"/> charms hanging at the pendulum bob
    /// (<paramref name="bobX"/>/<paramref name="bobY"/>, canvas px), the whole cluster swinging by
    /// <paramref name="stringDeg"/> (0 = hanging straight down). <paramref name="cap"/> bounds how
    /// many worms are drawn before the count switches to a "+N" tag. <paramref name="error"/> tints
    /// the worms red. Draws into <paramref name="g"/> without leaving any transform behind.</summary>
    public static void DrawCharms(Graphics g, CursorSettings s, int count,
                                  float bobX, float bobY, float stringDeg, int cap, bool error = false)
    {
        if (count <= 0) return;
        int sz = Math.Max(8, s.Size);
        cap = Math.Clamp(cap, 1, 6);
        int shown = Math.Min(count, cap);

        var fill = error ? Color.FromArgb(222, 50, 50) : Parse(s.FillColor, Color.White);
        var outline = Parse(s.OutlineColor, Color.Black);
        float ob = (float)(s.OutlineThickness * (sz / 64f));

        float wormR = sz * 0.105f;            // worm body radius
        float spacing = wormR * 1.55f;        // gap between worms in the fan
        float drop = sz * 0.06f;              // how far the cluster sits below the bob

        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(bobX, bobY);
        g.RotateTransform(stringDeg);         // the cluster tilts with the string as it swings

        // Worms fanned horizontally and centred on the bob, hanging just below it.
        float x0 = -(shown - 1) * spacing / 2f;
        for (int i = 0; i < shown; i++)
            DrawWorm(g, x0 + i * spacing, drop, wormR, fill, outline, ob);

        // Overflow: keep the cluster readable for big counts with a small "+N" under the worms.
        if (count > cap)
            DrawCountTag(g, count, 0f, drop + wormR * 2.1f, sz, fill, outline, ob);

        g.Restore(saved);
    }

    // One worm: a rounded egg-shaped body (fatter at the bottom) with two googly eyes, drawn
    // outline-under / fill-over so it reads as a filled glyph with a clean border — matching the
    // rest of the cursor set. Local coords: (cx,cy) is the body centre.
    static void DrawWorm(Graphics g, float cx, float cy, float r, Color fill, Color outline, float ob)
    {
        float w = r * 1.7f, h = r * 2.05f;
        var body = new RectangleF(cx - w / 2f, cy - h / 2f, w, h);

        if (ob > 0.01f)
            using (var bo = new SolidBrush(outline))
                g.FillEllipse(bo, body.X - ob, body.Y - ob, body.Width + 2 * ob, body.Height + 2 * ob);
        using (var bf = new SolidBrush(fill))
            g.FillEllipse(bf, body);

        // Eyes near the top of the head: white sclera + dark pupil, looking slightly down.
        float eyeR = r * 0.36f, pupilR = eyeR * 0.5f;
        float eyeDx = r * 0.42f, eyeY = cy - h * 0.16f;
        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            float ex = cx + sgn * eyeDx;
            using (var bw = new SolidBrush(Color.White))
                g.FillEllipse(bw, ex - eyeR, eyeY - eyeR, eyeR * 2, eyeR * 2);
            using (var bp = new SolidBrush(outline))
                g.FillEllipse(bp, ex - pupilR, eyeY + eyeR * 0.18f - pupilR, pupilR * 2, pupilR * 2);
        }
    }

    // A small "+N" under the cluster, stroked outline-under / fill-over like the other glyphs, so a
    // count past the worm cap (e.g. "+4") stays crisp and legible at cursor sizes.
    static void DrawCountTag(Graphics g, int count, float cx, float cy, int sz,
                             Color fill, Color outline, float ob)
    {
        using var path = new GraphicsPath();
        float em = sz * 0.30f;
        using var fam = new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        path.AddString("+" + count, fam, (int)FontStyle.Bold, em, new PointF(cx, cy), fmt);

        if (ob > 0.01f)
            using (var po = new Pen(outline, 2.2f * ob) { LineJoin = LineJoin.Round })
                g.DrawPath(po, path);
        using (var bf = new SolidBrush(fill))
            g.FillPath(bf, path);
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
