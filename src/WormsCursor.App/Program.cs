namespace WormsCursor.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Watchdog mode: a detached sibling that restores the cursor if the main
        // process is terminated abruptly (Task Manager / debugger stop). Must run
        // before any WinForms init.
        if (args.Length == 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int pid))
            return Watchdog.Run(pid);

        // High-DPI mode etc. comes from ApplicationHighDpiMode in the .csproj.
        ApplicationConfiguration.Initialize();

        // No main window: the whole app lives in the tray.
        Application.Run(new TrayApplicationContext());
        return 0;
    }
}
