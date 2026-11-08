using System.Text.Json;
using System.Text.Json.Serialization;

namespace WormsCursor.Core;

/// <summary>
/// Loads/saves <see cref="CursorSettings"/> as JSON in %LocalAppData%\WormsCursor\.
/// Mirrors PowerLink's store: indented JSON, atomic write (tmp + replace), tolerant
/// load (corrupt/missing → defaults). A file that exists but won't parse is never replaced
/// unless a copy of it was saved first: <see cref="Save"/> copies it to
/// <c>settings.json.corrupt-&lt;timestamp&gt;</c> before writing, and refuses to write if that copy
/// fails, so a hand edit that merely has a typo is never destroyed. Living in LocalAppData (not next
/// to the exe) means an installer/update that replaces the app folder leaves settings untouched.
/// </summary>
public static class SettingsStore
{
    // Enums as readable names (e.g. "Corner"/"BottomRight"), not bare integers, so the settings
    // file stays human-editable. The converter also accepts numbers on read, so any legacy value loads.
    // Reading also forgives the usual hand-edit slips (a trailing comma, a // comment).
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WormsCursor",
        "settings.json");

    /// <summary>Loads settings, or returns normalized defaults if missing/corrupt/unreadable. Never
    /// touches the file: a corrupt one stays where it is, and <see cref="Save"/> keeps a copy of it
    /// before replacing it. For the owning app; another process that only reads the file uses
    /// <see cref="TryRead"/>, which tells a failed read apart from the defaults.</summary>
    public static CursorSettings Load(string? path = null)
        => TryParse(path ?? DefaultPath, out _) ?? new CursorSettings();

    /// <summary>For a read-only consumer in another process (the OpenCode helper), which must not move
    /// the tray app's settings out from under it: never touches the file. Returns the defaults when no
    /// file exists yet (nothing saved), but null when one exists and can't be read or parsed right now
    /// (locked mid-save, ACL-denied, corrupt), so the caller keeps what it had instead of mistaking a
    /// failed read for the default settings.</summary>
    public static CursorSettings? TryRead(string? path = null)
    {
        var source = path ?? DefaultPath;
        return File.Exists(source) ? TryParse(source, out _) : new CursorSettings();
    }

    // The settings in `source`, normalized; null when it is missing, unreadable (locked, ACL-denied)
    // or corrupt. `corrupt` tells the last case apart: the file exists but isn't valid settings JSON.
    static CursorSettings? TryParse(string source, out bool corrupt)
    {
        corrupt = false;
        if (!File.Exists(source)) return null;
        try
        {
            var loaded = JsonSerializer.Deserialize<CursorSettings>(File.ReadAllText(source), JsonOptions);
            if (loaded is null) { corrupt = true; return null; } // a bare `null` document
            // Migration hook: when Version changes in future, upgrade here.
            loaded.Normalize();
            return loaded;
        }
        catch (JsonException)
        {
            corrupt = true; // unparseable (bad syntax, unknown enum name, …)
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Saves settings atomically (write tmp, then replace). An existing file that doesn't
    /// parse is first copied to <c>&lt;path&gt;.corrupt-&lt;timestamp&gt;</c>. Throws
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> if the file can't be
    /// written (read-only, locked) or that copy can't be made — the target is then left untouched;
    /// the caller decides whether that's fatal.</summary>
    public static void Save(CursorSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var target = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // The one place a file gets replaced, so the one place that keeps an unparseable one. A
        // failed copy throws before anything is written.
        TryParse(target, out bool corrupt);
        if (corrupt) File.Copy(target, $"{target}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}");

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tmp = target + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            if (File.Exists(target))
                File.Replace(tmp, target, destinationBackupFileName: null);
            else
                File.Move(tmp, target);
        }
        catch
        {
            // e.g. a read-only or locked target: don't leave the orphaned tmp behind, then report it.
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}
