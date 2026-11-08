namespace AgentsCursor.App;

/// <summary>
/// The app's one diagnostic log: <c>%LocalAppData%\AgentsCursor\bridge.log</c> (named for the hook
/// bridge, its first writer). The hook bridge, the headless register verb, the overlay's frame loop
/// and settings persistence all append here, so "why didn't the token show?" has a single place to
/// look. Best-effort: logging never throws.
/// </summary>
static class AppLog
{
    public static void Write(string text)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentsCursor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "bridge.log"), $"{DateTime.Now:s} {text}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
