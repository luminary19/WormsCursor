using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the "progress / busy" cursor: an optional arrow plus a trailing comet ring of
/// dots. It's stateless — the engine owns the spin phase and the spring that drags the
/// ring behind movement, and passes the current ring centre + phase in here.
///
/// The canvas is square and the arrow glyph is drawn at its native <see cref="ArrowRenderer"/>
/// size, so the busy cursor matches the plain arrow's on-screen scale; the extra canvas
/// around it is transparent room for the ring to swing into.
/// </summary>
public static class ProgressRenderer
{
    public const int Dots = 8;

    /// <summary>Geometry for a given size: canvas, the arrow-tip hotspot, ring/dot radii
    /// and the tail length (tip → string anchor), all in canvas pixels.</summary>
    public readonly struct LayoutInfo
    {
        public readonly int Canvas, HotX, HotY;
        public readonly float RingR, DotR, TailLen;
        public LayoutInfo(int canvas, int hotX, int hotY, float ringR, float dotR, float tailLen)
        {
            Canvas = canvas; HotX = hotX; HotY = hotY;
            RingR = ringR; DotR = dotR; TailLen = tailLen;
        }
    }

    public static LayoutInfo Layout(CursorSettings s)
    {
        int sz = Math.Max(8, s.Size);
        // The ring hangs off the arrow's tail and can swing to any side, so the hotspot
        // (arrow tip) sits at the canvas centre with room all around. Clamp to 256
        // (Windows' practical custom-cursor limit) at very large sizes.
        int canvas = Math.Min((int)MathF.Round(sz * 2.0f), 256);
        int c = canvas / 2;
        return new LayoutInfo(canvas, c, c, sz * 0.16f, sz * 0.06f, sz * 0.40f);
    }

    /// <summary>Composites one busy frame. <paramref name="arrowDeg"/> rotates the arrow
    /// (0 = pointing right); <paramref name="ringX"/>/<paramref name="ringY"/> is the ring
    /// centre in canvas pixels; <paramref name="phase"/> spins the comet.</summary>
    public static Bitmap Compose(CursorSettings s, Bitmap arrowBase, double arrowDeg,
                                 float ringX, float ringY, float phase, bool withArrow)
    {
        var l = Layout(s);
        var bmp = new Bitmap(l.Canvas, l.Canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        if (withArrow) DrawArrow(g, l, arrowBase, arrowDeg);

        var fill = Parse(s.FillColor, Color.White);
        var outline = Parse(s.OutlineColor, Color.Black);
        float pen = (float)(s.OutlineThickness * (s.Size / 64f));
        for (int d = 0; d < Dots; d++)
        {
            float ang = phase + d * (MathF.PI * 2f / Dots);
            float cx = ringX + MathF.Cos(ang) * l.RingR;
            float cy = ringY + MathF.Sin(ang) * l.RingR;
            float t = d / (float)Dots;               // 0 = head (big/opaque) -> tail
            float rr = l.DotR * (1f - 0.65f * t);
            int alpha = (int)(255 * (1f - 0.78f * t));
            using (var b = new SolidBrush(Color.FromArgb(alpha, fill)))
                g.FillEllipse(b, cx - rr, cy - rr, rr * 2, rr * 2);
            if (pen > 0.01f)
                using (var p = new Pen(Color.FromArgb(alpha, outline), pen))
                    g.DrawEllipse(p, cx - rr, cy - rr, rr * 2, rr * 2);
        }
        return bmp;
    }

    /// <summary>Composites the help cursor: the arrow plus a "?" glyph that hangs off the
    /// arrow's tail like the busy ring (same pendulum), but tilts with the string instead
    /// of spinning. <paramref name="glyphDeg"/> is the string angle (the engine sets it so
    /// the "?" hangs upside-down at rest and swings as the cursor moves).</summary>
    public static Bitmap ComposeHelp(CursorSettings s, Bitmap arrowBase, double arrowDeg,
                                     float qx, float qy, float glyphDeg)
    {
        var l = Layout(s);
        var bmp = new Bitmap(l.Canvas, l.Canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        DrawArrow(g, l, arrowBase, arrowDeg);

        using var fam = new FontFamily("Arial");
        using var path = new GraphicsPath();
        path.AddString("?", fam, (int)FontStyle.Bold, s.Size * 0.55f, PointF.Empty, StringFormat.GenericTypographic);
        var gb = path.GetBounds();

        var st = g.Save();
        g.TranslateTransform(qx, qy);
        g.RotateTransform(glyphDeg);
        g.TranslateTransform(-(gb.X + gb.Width / 2f), -(gb.Y + gb.Height / 2f)); // centre the glyph on the bob
        using (var br = new SolidBrush(Parse(s.FillColor, Color.White)))
            g.FillPath(br, path);
        float pen = (float)(s.OutlineThickness * (s.Size / 64f));
        if (pen > 0.01f)
            using (var p = new Pen(Parse(s.OutlineColor, Color.Black), pen) { LineJoin = LineJoin.Round })
                g.DrawPath(p, path);
        g.Restore(st);
        return bmp;
    }

    // Draws the arrow rotated about its tip (the canvas-centre hotspot), at native size.
    static void DrawArrow(Graphics g, LayoutInfo l, Bitmap arrowBase, double arrowDeg)
    {
        int a = arrowBase.Width;                 // arrow tip sits at its own centre
        var st = g.Save();
        g.TranslateTransform(l.HotX, l.HotY);
        g.RotateTransform((float)arrowDeg);
        g.TranslateTransform(-a / 2f, -a / 2f);
        g.DrawImage(arrowBase, 0, 0);
        g.Restore(st);
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
