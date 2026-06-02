using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the hand cursor (OCR_HAND) from the baked <see cref="HandShape"/> Béziers,
/// the same way the arrow is drawn (a real <see cref="GraphicsPath"/>, not a flattened
/// polyline). It fills the outer silhouette with the fill colour for an opaque body,
/// then fills the full line drawing (all contours, even-odd) with the outline colour —
/// outline + finger-separation lines + knuckle marks. Exact curves keep the thin
/// outline ring precise, so the border fills cleanly with no light speckles.
///
/// Fill/outline colour and size are shared with the arrow; corner radius does not apply
/// and the line weight comes from the source art. The index fingertip (the hotspot) is
/// placed at the canvas centre so the engine rotates the hand around it like the tip.
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

        using (var body = new GraphicsPath())
        {
            body.AddBeziers(Map(sil));
            body.CloseFigure();
            using var fill = new SolidBrush(Parse(s.FillColor, Color.White));
            g.FillPath(fill, body);
        }

        using (var art = new GraphicsPath { FillMode = FillMode.Alternate })
        {
            foreach (var contour in HandShape.Contours)
            {
                art.StartFigure();
                art.AddBeziers(Map(contour));
                art.CloseFigure();
            }
            using var line = new SolidBrush(Parse(s.OutlineColor, Color.Black));
            g.FillPath(line, art);
        }

        return bmp;
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
