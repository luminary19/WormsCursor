using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace AgentsCursor.App.ControlPanel;

/// <summary>
/// Brings WPF up inside the WinForms tray app. There is no App.xaml and no <c>Application.Run</c>:
/// the WinForms message loop already pumps the UI thread, which is also the WPF dispatcher's thread.
/// We create the one <see cref="System.Windows.Application"/> anyway, because WPF-UI's theme manager
/// keeps the Fluent theme and control styles in <c>Application.Current.Resources</c> and swaps the
/// light/dark dictionary there when Windows' theme changes.
/// </summary>
static class WpfHost
{
    /// <summary>Creates the WPF application object with the WPF-UI dictionaries merged, once, on the
    /// UI thread. <see cref="ShutdownMode.OnExplicitShutdown"/>: closing a window never ends anything;
    /// the tray decides when the app exits.</summary>
    public static void EnsureStarted()
    {
        if (System.Windows.Application.Current is not null) return;
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Before anything touches WPF-UI's UiApplication (it caches whether these are present).
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        UseOpaqueText(app.Resources);
        ApplicationThemeManager.Changed += (_, _) => UseOpaqueText(app.Resources);
    }

    // The Fluent text colours are translucent (secondary text is 62% black, for one), which WinUI's
    // DirectWrite blends exactly. WPF's text renderer doesn't: dark text drawn with a translucent brush
    // comes out much lighter than the colour says (light-theme secondary text measured 3.2:1 against
    // its card, under WCAG AA, where the same colour drawn opaque measures 6.1:1). So the text brushes
    // are replaced with the opaque colour each one would blend to over the theme's base background,
    // and refreshed whenever the theme dictionary is swapped.
    static readonly string[] TextColors = { "TextFillColorPrimary", "TextFillColorSecondary", "TextFillColorTertiary" };

    static void UseOpaqueText(ResourceDictionary resources)
    {
        if (resources["SolidBackgroundFillColorBase"] is not Color backdrop) return;
        foreach (string key in TextColors)
        {
            if (resources[key] is not Color text) continue;
            var brush = new SolidColorBrush(Over(text, backdrop));
            brush.Freeze();
            resources[key + "Brush"] = brush;
        }
    }

    static Color Over(Color top, Color bottom)
    {
        float a = top.A / 255f;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * a + b * (1 - a));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }
}
