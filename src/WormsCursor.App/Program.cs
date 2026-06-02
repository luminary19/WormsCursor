namespace WormsCursor.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // High-DPI mode etc. comes from ApplicationHighDpiMode in the .csproj.
        ApplicationConfiguration.Initialize();

        // No main window: the whole app lives in the tray.
        Application.Run(new TrayApplicationContext());
    }
}
