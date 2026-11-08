using Microsoft.Win32;

namespace AgentsCursor.App;

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
/// The per-user startup entries <see cref="RegistryAutostart"/> works with: the command stored under
/// <c>HKCU\…\Run</c>, and the <c>StartupApproved\Run</c> flag Task Manager / Settings → Apps → Startup
/// keep beside it. A seam, so the autostart logic is tested without touching the real registry.
/// </summary>
public interface IStartupEntries
{
    /// <summary>The Run command stored under <paramref name="name"/>, or null when there is none.</summary>
    string? GetCommand(string name);
    void SetCommand(string name, string command);
    void DeleteCommand(string name);
    /// <summary>The StartupApproved flag for <paramref name="name"/>, or null when there is none.</summary>
    byte[]? GetApproval(string name);
    void DeleteApproval(string name);
}

/// <summary>
/// Per-user autostart via <c>HKCU\…\Run</c> — no admin, no COM. It shows up in Task
/// Manager / Settings → Apps → Startup, and we respect a user's toggle there by reading
/// the matching <c>StartupApproved\Run</c> flag (first byte: 2 = enabled, 3 = disabled).
/// </summary>
public sealed class RegistryAutostart : IAutostart
{
    const string ValueName = "AgentsCursor";

    /// <summary>The Run value this app wrote before it was renamed from WormsCursor.</summary>
    const string LegacyValueName = "WormsCursor";

    readonly IStartupEntries _entries;
    readonly string? _exe;

    public RegistryAutostart() : this(new RegistryStartupEntries(), Environment.ProcessPath) { }

    /// <param name="entries">Where the entries live (the registry, or a test's fake).</param>
    /// <param name="exe">The exe autostart launches: the running one.</param>
    public RegistryAutostart(IStartupEntries entries, string? exe)
    {
        _entries = entries;
        _exe = exe;
    }

    /// <summary>True only when the Run entry launches <i>this</i> exe. An entry left behind by another
    /// copy (e.g. a dev build in bin\, or an old install) reads as off, so the checkbox shows unticked
    /// and ticking it rewrites the entry to the current exe instead of hiding a stale autostart.</summary>
    public bool IsEnabled
        => _entries.GetCommand(ValueName) is string stored && LaunchesExe(stored, _exe) && IsApproved(ValueName);

    // Not switched off in Task Manager / Settings: no StartupApproved flag, or one whose first byte is 2.
    bool IsApproved(string name)
        => _entries.GetApproval(name) is not { Length: > 0 } flag || flag[0] == 2;

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
        _entries.SetCommand(ValueName, $"\"{_exe}\"");
        // Clear any "disabled" override so it actually launches at logon.
        _entries.DeleteApproval(ValueName);
    }

    public void Disable() => _entries.DeleteCommand(ValueName);

    /// <summary>The one-time carry-over from before the rename: removes the Run entry WormsCursor
    /// wrote (and its StartupApproved flag), and when that entry was switched on, writes ours for the
    /// running exe in its place, so Start with Windows survives the rename. A WormsCursor entry the
    /// user had switched off in Task Manager is just removed. Returns whether there was one.</summary>
    public bool MigrateLegacy()
    {
        if (_entries.GetCommand(LegacyValueName) is null) return false;
        if (IsApproved(LegacyValueName)) Enable(); // ours first, so a failure here loses nothing
        _entries.DeleteCommand(LegacyValueName);
        _entries.DeleteApproval(LegacyValueName);
        return true;
    }

    /// <summary>The real entries, under HKCU.</summary>
    sealed class RegistryStartupEntries : IStartupEntries
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        public string? GetCommand(string name)
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue(name) as string;
        }

        public void SetCommand(string name, string command)
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(name, command);
        }

        public void DeleteCommand(string name)
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(name, throwOnMissingValue: false);
        }

        public byte[]? GetApproval(string name)
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(name) as byte[];
        }

        public void DeleteApproval(string name)
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}
