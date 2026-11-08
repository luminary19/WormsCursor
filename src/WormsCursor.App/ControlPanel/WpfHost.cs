using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace WormsCursor.App.ControlPanel;

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
    }
}
