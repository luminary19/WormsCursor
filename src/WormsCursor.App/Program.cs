using Velopack;

namespace WormsCursor.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Velopack hijacks Main when invoked with --veloapp-* args during
        // install / update / uninstall: it runs the matching hook and exits
        // before any UI spins up. Must stay the very first call in Main.
        // No-ops cleanly for dev builds run straight out of bin\.
        VelopackApp.Build().Run();

        // High-DPI mode etc. comes from ApplicationHighDpiMode in the .csproj.
        ApplicationConfiguration.Initialize();

        // No main window: the whole app lives in the tray.
        Application.Run(new TrayApplicationContext());
    }
}
