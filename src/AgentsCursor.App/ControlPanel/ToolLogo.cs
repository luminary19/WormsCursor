using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentsCursor.Core;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;

namespace AgentsCursor.App.ControlPanel;

/// <summary>A tool's icon as a WPF image, drawn by Core's renderer: the Agents page cards (always the
/// original logo, since a card identifies the agent) and the Customization page's tiles.</summary>
static class ToolLogo
{
    // The share of the well the renderer's box may take: its contrast rim reaches a little past it.
    const float BoxShare = 0.86f;
    // For the cards, the side of a square of the same area as the logo, as a share of the well.
    const float CardWeight = 0.72f;

    /// <summary>The original logo of <paramref name="toolId"/> in a well of <paramref name="sizeDip"/>
    /// DIPs, rendered at <paramref name="dpiScale"/> so it is crisp. The four logos have very different
    /// shapes (the wide Claude critter, the tall OpenCode mark), so each is sized by its area, not its
    /// longer side: side by side on the Agents page they carry the same visual weight.</summary>
    public static BitmapSource Render(string toolId, double sizeDip, double dpiScale)
    {
        string iconId = IconCatalog.OriginalId(toolId);
        using var renderer = new NotifierRenderer();
        var shape = renderer.IconShape(toolId, iconId);
        float area = MathF.Sqrt(Math.Max(1e-3f, shape.Width * shape.Height));
        float share = Math.Min(BoxShare, CardWeight / area);
        return Render(renderer, toolId, iconId, sizeDip, dpiScale, share);
    }

    /// <summary>The icon <paramref name="iconId"/> of <paramref name="toolId"/> at <paramref name="sizeDip"/>
    /// DIPs, rendered at <paramref name="dpiScale"/> and fitted as the token fits it. Uses a renderer of
    /// its own on the calling (UI) thread.</summary>
    public static BitmapSource Render(string toolId, string iconId, double sizeDip, double dpiScale)
    {
        using var renderer = new NotifierRenderer();
        return Render(renderer, toolId, iconId, sizeDip, dpiScale, BoxShare);
    }

    /// <summary>As the overload without a renderer, but drawn by <paramref name="renderer"/> (the caller's,
    /// on its thread), showing a mascot's frame at <paramref name="mascotClock"/> seconds into its loop.</summary>
    public static BitmapSource Render(NotifierRenderer renderer, string toolId, string iconId, double sizeDip, double dpiScale,
                                      double? mascotClock)
        => Render(renderer, toolId, iconId, sizeDip, dpiScale, BoxShare, mascotClock);

    static BitmapSource Render(NotifierRenderer renderer, string toolId, string iconId, double sizeDip, double dpiScale, float share,
                               double? mascotClock = null)
    {
        int px = Math.Max(1, (int)Math.Round(sizeDip * dpiScale));
        using var bitmap = new Gdi.Bitmap(px, px, GdiImaging.PixelFormat.Format32bppPArgb);
        using (var g = Gdi.Graphics.FromImage(bitmap))
        {
            g.Clear(Gdi.Color.Transparent);
            renderer.DrawIcon(g, toolId, iconId, px / 2f, px / 2f, px * share, (float)dpiScale, mascotClock);
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
