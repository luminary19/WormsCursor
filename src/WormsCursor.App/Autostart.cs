using Microsoft.Win32;

namespace WormsCursor.App;

/// <summary>
/// Run-on-login integration. Abstracted so a future MSIX/Store build can swap the
/// registry implementation for a <c>windows.startupTask</c> one without touching the UI.
/// </summary>
public interface IAutostart
{
    bool IsEnabled { get; }
    void Enable();
    void Disable();
}

/// <summary>
/// Per-user autostart via <c>HKCU\…\Run</c> — no admin, no COM. It shows up in Task
/// Manager / Settings → Apps → Startup, and we respect a user's toggle there by reading
/// the matching <c>StartupApproved\Run</c> flag (first byte: 2 = enabled, 3 = disabled).
/// </summary>
public sealed class RegistryAutostart : IAutostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    const string ValueName = "WormsCursor";

    static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>True only when the Run entry launches <i>this</i> exe. An entry left behind by another
    /// copy (e.g. a dev build in bin\, or an old install) reads as off, so the checkbox shows unticked
    /// and ticking it rewrites the entry to the current exe instead of hiding a stale autostart.</summary>
    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string stored || !LaunchesExe(stored, Environment.ProcessPath))
                return false;

            // If the user switched us off in Task Manager / Settings, StartupApproved
            // holds a flag whose first byte is != 2 (2 = enabled, 3 = disabled).
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(ValueName) is byte[] flag && flag.Length > 0 && flag[0] != 2)
                return false;

            return true;
        }
    }

    /// <summary>Whether a stored Run command (as <see cref="Enable"/> writes it: the quoted exe path)
    /// launches <paramref name="exe"/> — compared as full paths, quotes trimmed, case-insensitive.</summary>
    internal static bool LaunchesExe(string stored, string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        string path = stored.Trim().Trim('"');
        if (path.Length == 0) return false;
        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // not a usable path, so it can't be launching us
        }
    }

    public void Enable()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            run.SetValue(ValueName, Command);

        // Clear any "disabled" override so it actually launches at logon.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
