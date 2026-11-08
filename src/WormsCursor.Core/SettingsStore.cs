using System.Text.Json;
using System.Text.Json.Serialization;

namespace WormsCursor.Core;

/// <summary>
/// Loads/saves <see cref="CursorSettings"/> as JSON in %LocalAppData%\WormsCursor\.
/// Mirrors PowerLink's store: indented JSON, atomic write (tmp + replace), tolerant
/// load (corrupt/missing → defaults). A file that exists but won't parse is moved aside to
/// <c>settings.json.corrupt-&lt;timestamp&gt;</c> first, so the next save can't destroy a
/// hand edit that merely has a typo. Living in LocalAppData (not next to the exe)
/// means an installer/update that replaces the app folder leaves settings untouched.
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

    /// <summary>Loads settings, or returns normalized defaults if missing/corrupt/unreadable. A corrupt
    /// file is preserved (moved aside) before falling back, never silently left to be overwritten.
    /// For the owning app; another process that only reads the file uses <see cref="Read"/>.</summary>
    public static CursorSettings Load(string? path = null)
    {
        var source = path ?? DefaultPath;
        var loaded = TryParse(source, out bool corrupt);
        if (corrupt) Quarantine(source);
        return loaded ?? new CursorSettings();
    }

    /// <summary>Like <see cref="Load"/>, but never touches the file: a corrupt one is left for the
    /// owning app to deal with. For a read-only consumer in another process (the OpenCode helper),
    /// which must not move the tray app's settings out from under it.</summary>
    public static CursorSettings Read(string? path = null)
        => TryParse(path ?? DefaultPath, out _) ?? new CursorSettings();

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

    // Moves a corrupt settings file aside so the defaults we fall back to can't overwrite it on the
    // next save. Best-effort: if even the move fails there's nothing better to do than use defaults.
    static void Quarantine(string source)
    {
        try { File.Move(source, $"{source}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Saves settings atomically (write tmp, then replace). Throws
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> if the file can't be
    /// written (read-only, locked); the caller decides whether that's fatal.</summary>
    public static void Save(CursorSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var target = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

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
