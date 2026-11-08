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
    /// file is preserved (moved aside) before falling back, never silently left to be overwritten.</summary>
    public static CursorSettings Load(string? path = null)
    {
        var source = path ?? DefaultPath;
        if (File.Exists(source))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<CursorSettings>(File.ReadAllText(source), JsonOptions);
                if (loaded is not null)
                {
                    // Migration hook: when Version changes in future, upgrade here.
                    loaded.Normalize();
                    return loaded;
                }
                Quarantine(source); // a bare `null` document
            }
            catch (JsonException)
            {
                Quarantine(source); // unparseable (bad syntax, unknown enum name, …)
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Fall through to defaults on an unreadable file (locked, ACL-denied); it's left in place.
            }
        }
        return new CursorSettings();
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
