using System.Windows.Media.Imaging;

namespace WormsCursor.App.ControlPanel;

/// <summary>Images the control panel shows.</summary>
static class PanelAssets
{
    /// <summary>The app icon next to the exe (tools/generate-icon.py), like the tray's; null if missing.</summary>
    public static BitmapSource? AppIcon { get; } = LoadAppIcon();

    static BitmapSource? LoadAppIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.png");
        if (!File.Exists(path)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.CacheOption = BitmapCacheOption.OnLoad; // read now; don't hold the file open
        image.EndInit();
        image.Freeze();
        return image;
    }
}
