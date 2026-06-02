using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the hand cursor (OCR_HAND) from the baked <see cref="HandShape"/> as a SOLID,
/// opaque cursor: fill the outer silhouette with the fill colour, then fill the full
/// line drawing (outline + finger-separation lines + knuckle marks, even-odd) with the
/// outline colour on top. Filling — rather than stroking a dense polyline — keeps the
/// rounded parts artifact-free. Fill/outline colour and size are shared with the arrow;
/// corner radius does not apply, and the line weight comes from the source art.
/// The index fingertip (the hotspot) is placed at the canvas centre so the engine can
/// rotate the hand around it like the arrow's tip.
/// </summary>
public static class HandRenderer
{
    public static Bitmap DrawHand(CursorSettings s)
    {
        int size = Math.Max(8, s.Size);
        float cx = size / 2f, cy = size / 2f;
        float pad = size * 0.10f;

        // Fit so the farthest silhouette point from the fingertip (origin) stays inside
        // the canvas at any rotation.
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

        using (var fill = new SolidBrush(Parse(s.FillColor, Color.White)))
            g.FillPolygon(fill, Map(sil));

        using var path = new GraphicsPath { FillMode = FillMode.Alternate };
        foreach (var contour in HandShape.LineArt)
            path.AddPolygon(Map(contour));
        using (var line = new SolidBrush(Parse(s.OutlineColor, Color.Black)))
            g.FillPath(line, path);

        return bmp;
    }

    static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return ColorTranslator.FromHtml(hex.Trim()); }
        catch { return fallback; }
    }
}
