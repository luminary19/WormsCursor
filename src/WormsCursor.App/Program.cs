namespace WormsCursor.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Single-instance guard FIRST, before any UI: if another instance is already
        // running, Acquire() signals it to open Preferences and returns null, so we exit
        // immediately — no tray icon, no cursor takeover.
        using var single = SingleInstance.Acquire();
        if (single is null) return;

        // High-DPI mode etc. comes from ApplicationHighDpiMode in the .csproj.
        ApplicationConfiguration.Initialize();

        // No main window: the whole app lives in the tray.
        var tray = new TrayApplicationContext();
        single.StartListening(tray.OpenPreferences);
        Application.Run(tray);
    }
}
