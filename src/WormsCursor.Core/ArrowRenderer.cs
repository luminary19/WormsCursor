using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the arrow bitmap for a given <see cref="CursorSettings"/>. The arrow points
/// +X (right) with its tip at the canvas centre, so the centre is both the rotation
/// pivot and the cursor hotspot. Geometry scales with <c>Size</c> (reference 64), and
/// fill/outline colour, outline thickness and corner radius come from the settings.
/// </summary>
public static class ArrowRenderer
{
    // Base arrow as offsets from the tip (at the origin), in reference units (size 64).
    // (tip, upper barb, shaft top, shaft back-top, shaft back-bottom, shaft bottom, lower barb)
    static readonly PointF[] BaseOffsets =
    {
        new(0, 0), new(-14, -9), new(-14, -3), new(-22, -3), new(-22, 3), new(-14, 3), new(-14, 9),
    };

    public static Bitmap DrawArrow(CursorSettings s)
    {
        int size = Math.Max(8, s.Size);
        float k = size / 64f;                 // scale factor vs the reference size
        float c = size / 2f;                  // tip = canvas centre = hotspot

        var pts = new PointF[BaseOffsets.Length];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new PointF(c + BaseOffsets[i].X * k, c + BaseOffsets[i].Y * k);

        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var path = BuildPath(pts, (float)(s.CornerRadius * k));
        using (var fill = new SolidBrush(ParseColor(s.FillColor, Color.White)))
            g.FillPath(fill, path);

        float pen = (float)(s.OutlineThickness * k);
        if (pen > 0.01f)
            using (var p = new Pen(ParseColor(s.OutlineColor, Color.Black), pen) { LineJoin = LineJoin.Round })
                g.DrawPath(p, path);

        return bmp;
    }

    // Builds the arrow outline. radius<=0 → sharp polygon; otherwise each vertex is
    // replaced by a quadratic-Bézier fillet (clamped to half the shorter adjacent edge).
    static GraphicsPath BuildPath(PointF[] pts, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f)
        {
            path.AddPolygon(pts);
            return path;
        }

        int n = pts.Length;
        var entry = new PointF[n]; // point where the fillet starts (on the edge from prev)
        var exit = new PointF[n];  // point where the fillet ends (on the edge to next)
        for (int i = 0; i < n; i++)
        {
            PointF prev = pts[(i - 1 + n) % n], cur = pts[i], next = pts[(i + 1) % n];
            PointF toPrev = new(prev.X - cur.X, prev.Y - cur.Y);
            PointF toNext = new(next.X - cur.X, next.Y - cur.Y);
            float lenPrev = MathF.Sqrt(toPrev.X * toPrev.X + toPrev.Y * toPrev.Y);
            float lenNext = MathF.Sqrt(toNext.X * toNext.X + toNext.Y * toNext.Y);
            float r = Math.Min(radius, Math.Min(lenPrev, lenNext) / 2f);
            entry[i] = new PointF(cur.X + toPrev.X * (r / lenPrev), cur.Y + toPrev.Y * (r / lenPrev));
            exit[i] = new PointF(cur.X + toNext.X * (r / lenNext), cur.Y + toNext.Y * (r / lenNext));
        }

        for (int i = 0; i < n; i++)
        {
            PointF cur = pts[i];
            // quadratic (entry, control=cur, exit) → cubic Bézier control points
            PointF c1 = new(entry[i].X + 2f / 3f * (cur.X - entry[i].X), entry[i].Y + 2f / 3f * (cur.Y - entry[i].Y));
            PointF c2 = new(exit[i].X + 2f / 3f * (cur.X - exit[i].X), exit[i].Y + 2f / 3f * (cur.Y - exit[i].Y));
            path.AddBezier(entry[i], c1, c2, exit[i]);
            path.AddLine(exit[i], entry[(i + 1) % n]); // straight edge to the next fillet
        }
        path.CloseFigure();
        return path;
    }

    static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
