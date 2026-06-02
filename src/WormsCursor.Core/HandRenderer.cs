using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the hand cursor (OCR_HAND) from the baked <see cref="HandShape"/> Béziers the
/// SAME way the arrow is drawn: fill the silhouette, then stroke every line with a pen
/// whose width is the outline thickness. So one thickness slider scales ALL of the hand's
/// lines together — the outer outline, the finger separators AND the knuckle creases —
/// exactly 1:1 with the arrow (there is no fixed-width ribbon baked into the geometry).
/// At thickness 0 it's a bare fill, just like the arrow.
///
/// Three sets of strokes (all in the outline colour, all scaled by the pen):
///  • the silhouette outer contour (also filled, so the outline owns the edge — no halo),
///  • finger SEPARATORS: short lines dropped from the notches between the folded
///    fingertips (found as local valleys on the silhouette's upper edge),
///  • knuckle CREASES: the small capsule marks, drawn as their centreline (a thick line),
///    not their outline (which would render as a hollow pill).
///
/// Fill/outline colour, size and outline thickness are shared with the arrow; corner
/// radius does not apply. The index fingertip (the hotspot) is placed at the canvas
/// centre so the engine rotates the hand around it like the tip.
/// </summary>
public static class HandRenderer
{
    public static Bitmap DrawHand(CursorSettings s)
    {
        int size = Math.Max(8, s.Size);
        float cx = size / 2f, cy = size / 2f, pad = size * 0.10f;

        var sil = HandShape.Silhouette;
        float maxR = 1f;
        for (int i = 0; i + 1 < sil.Length; i += 2)
            maxR = Math.Max(maxR, MathF.Sqrt(sil[i] * sil[i] + sil[i + 1] * sil[i + 1]));
        float scale = (size / 2f - pad) / maxR;

        PointF[] Map(float[] flat)
        {
            var pts = new PointF[flat.Length / 2];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new PointF(cx + flat[2 * i] * scale, cy + flat[2 * i + 1] * scale);
            return pts;
        }

        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var fillColor = Parse(s.FillColor, Color.White);
        var outlineColor = Parse(s.OutlineColor, Color.Black);

        using var body = new GraphicsPath();
        body.AddBeziers(Map(sil));
        body.CloseFigure();

        // 1) opaque body
        using (var fill = new SolidBrush(fillColor))
            g.FillPath(fill, body);

        // 2) all lines as pen strokes of the outline thickness — same model as the arrow,
        //    so the slider scales outline + separators + creases together. 0 = none.
        float pen = (float)(s.OutlineThickness * (size / 64f));
        if (pen > 0.01f)
            using (var p = new Pen(outlineColor, pen) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawPath(p, body);                                         // outer outline
                foreach (var (a, b) in FingerSeparators(Map(sil), cx))       // gaps between folded fingers
                    g.DrawLine(p, a, b);
                foreach (var (a, b) in CreaseLines(Map))                     // knuckle creases
                    g.DrawLine(p, a, b);
            }

        return bmp;
    }

    // Short vertical separators between the folded fingertips. The silhouette's upper edge
    // dips into a small valley (local max-Y, since fingertips point up = -Y) between each
    // pair of folded fingers; drop a short line down from each. Restricted to the upper
    // band and to the right of the index finger, and de-duplicated so two close nodes on
    // one finger don't double up.
    static IEnumerable<(PointF, PointF)> FingerSeparators(PointF[] silMapped, float cx)
    {
        var A = new List<PointF>();
        for (int i = 0; i < silMapped.Length; i += 3) A.Add(silMapped[i]); // anchor nodes
        int n = A.Count;
        float y0 = float.MaxValue, y1 = float.MinValue, x0 = float.MaxValue, x1 = float.MinValue;
        foreach (var q in A) { y0 = Math.Min(y0, q.Y); y1 = Math.Max(y1, q.Y); x0 = Math.Min(x0, q.X); x1 = Math.Max(x1, q.X); }
        float H = y1 - y0, Wd = x1 - x0;
        float regionMax = y0 + H * 0.45f;   // upper finger band only
        float xIndexEnd = cx + Wd * 0.02f;   // right of the index finger only

        var notches = new List<PointF>();
        for (int i = 0; i < n; i++)
        {
            var a = A[(i - 1 + n) % n]; var b = A[i]; var c = A[(i + 1) % n];
            if (b.Y > regionMax || b.X < xIndexEnd) continue;
            if (b.Y >= a.Y && b.Y >= c.Y) notches.Add(b);
        }
        notches.Sort((u, v) => u.X.CompareTo(v.X));
        float lastX = float.MinValue;
        foreach (var v in notches)
        {
            if (v.X - lastX < H * 0.08f) continue; // merge nodes on the same finger
            lastX = v.X;
            yield return (v, new PointF(v.X, v.Y + H * 0.16f));
        }
    }

    // The knuckle crease marks (the contours after the silhouette[0] and its inner edge[1])
    // are thin capsules; collapse each to its centreline segment so stroking it yields a
    // clean, thickness-scaled crease instead of a hollow capsule outline.
    static IEnumerable<(PointF, PointF)> CreaseLines(Func<float[], PointF[]> map)
    {
        for (int c = 2; c < HandShape.Contours.Length; c++)
        {
            var pts = map(HandShape.Contours[c]);
            float minX = pts[0].X, maxX = pts[0].X, minY = pts[0].Y, maxY = pts[0].Y;
            foreach (var q in pts)
            { minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y); }
            float bw = maxX - minX, bh = maxY - minY;
            if (bh >= bw) { float xm = (minX + maxX) / 2f, r = bw / 2f; yield return (new PointF(xm, minY + r), new PointF(xm, maxY - r)); }
            else { float ym = (minY + maxY) / 2f, r = bh / 2f; yield return (new PointF(minX + r, ym), new PointF(maxX - r, ym)); }
        }
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
