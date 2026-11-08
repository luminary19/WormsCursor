using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentsCursor.Core;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;

namespace AgentsCursor.App.ControlPanel;

/// <summary>A tool's logo as a WPF image, drawn by Core's renderer, for the Agents page cards.</summary>
static class ToolLogo
{
    /// <summary>The logo of <paramref name="toolId"/> at <paramref name="sizeDip"/> DIPs, rendered at
    /// <paramref name="dpiScale"/> so it is crisp. Uses a renderer of its own on the calling (UI) thread.</summary>
    public static BitmapSource Render(string toolId, double sizeDip, double dpiScale)
    {
        int px = Math.Max(1, (int)Math.Round(sizeDip * dpiScale));
        using var renderer = new NotifierRenderer();
        using var bitmap = new Gdi.Bitmap(px, px, GdiImaging.PixelFormat.Format32bppPArgb);
        using (var g = Gdi.Graphics.FromImage(bitmap))
        {
            g.Clear(Gdi.Color.Transparent);
            // The renderer's contrast rim reaches a little past the box; leave it room.
            renderer.DrawToken(g, new CursorSettings(), new TokenFace(toolId, Blocked: false, Others: 0),
                px / 2f, px / 2f, px * 0.86f, 0f);
        }

        var data = bitmap.LockBits(new Gdi.Rectangle(0, 0, px, px), GdiImaging.ImageLockMode.ReadOnly,
            GdiImaging.PixelFormat.Format32bppPArgb);
        try
        {
            var source = BitmapSource.Create(px, px, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32, null,
                data.Scan0, data.Stride * px, data.Stride);
            source.Freeze();
            return source;
        }
        finally { bitmap.UnlockBits(data); }
    }
}
