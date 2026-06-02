using System.Drawing;
using System.Drawing.Drawing2D;

namespace WormsCursor.Core;

/// <summary>
/// Draws the base arrow bitmap. The arrow points +X (to the right) with its tip at
/// the canvas center, so the center doubles as both the rotation pivot and the
/// cursor hotspot.
/// </summary>
public static class ArrowRenderer
{
    /// <summary>Renders the arrow into a fresh <paramref name="canvas"/>×<paramref name="canvas"/> bitmap.</summary>
    public static Bitmap DrawArrow(int canvas)
    {
        int pivot = canvas / 2;
        var bmp = new Bitmap(canvas, canvas);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int cx = pivot, cy = pivot;
        PointF[] pts =
        {
            new(cx,      cy),       // tip
            new(cx - 14, cy - 9),   // upper barb
            new(cx - 14, cy - 3),
            new(cx - 22, cy - 3),   // shaft
            new(cx - 22, cy + 3),
            new(cx - 14, cy + 3),
            new(cx - 14, cy + 9),   // lower barb
        };

        using var fill = new SolidBrush(Color.White);
        using var pen = new Pen(Color.Black, 1.4f);
        g.FillPolygon(fill, pts);
        g.DrawPolygon(pen, pts);
        return bmp;
    }
}
