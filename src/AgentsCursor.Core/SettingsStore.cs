using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentsCursor.Core;

/// <summary>
/// Loads/saves <see cref="CursorSettings"/> as JSON in %LocalAppData%\AgentsCursor\.
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
        "AgentsCursor",
        "settings.json");

    /// <summary>Where the settings lived before the app was renamed from WormsCursor to AgentsCursor.</summary>
    public static string LegacyPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WormsCursor",
        "settings.json");

    /// <summary>Loads settings, or returns normalized defaults if missing/corrupt/unreadable. Never
    /// touches any file: a corrupt one stays where it is, and <see cref="Save"/> keeps a copy of it
    /// before replacing it.
    ///
    /// It also owns the carry-over from before the rename: while <paramref name="path"/> doesn't exist
    /// yet, the settings are read from <paramref name="legacyPath"/> (by default, for the default path,
    /// WormsCursor's <see cref="LegacyPath"/>) when that exists. The old file is only ever read; the
    /// first <see cref="Save"/> writes the new one, so there is no copy step that can fail and be
    /// forgotten. A new file that exists but can't be read never falls back to the old one, which
    /// would bring back stale settings.</summary>
    public static CursorSettings Load(string? path = null, string? legacyPath = null)
    {
        legacyPath ??= path is null ? LegacyPath : null;
        string source = path ?? DefaultPath;
        if (!File.Exists(source) && legacyPath is not null && File.Exists(legacyPath)) source = legacyPath;
        return TryParse(source, out _) ?? new CursorSettings();
    }

    // The settings in `source`, normalized; null when it is missing, unreadable (locked, ACL-denied)
    // or corrupt. `corrupt` tells the last case apart: the file exists but isn't valid settings JSON.
    static CursorSettings? TryParse(string source, out bool corrupt)
    {
        corrupt = false;
        if (!File.Exists(source)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(source), documentOptions: NodeOptions);
            if (node is JsonObject settings) Migrate(settings);
            var loaded = node?.Deserialize<CursorSettings>(JsonOptions);
            if (loaded is null) { corrupt = true; return null; } // a bare `null` document
            loaded.Normalize();
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Unparseable: bad syntax, an unknown enum name, or a property named twice (JsonObject
            // refuses duplicate names with an ArgumentException).
            corrupt = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    static readonly JsonDocumentOptions NodeOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // Brings an older file's JSON up to <see cref="CursorSettings.CurrentVersion"/> before it is read.
    // A file without a Version is version 1. Version 2 renamed the placement "Cursor" (hanging below
    // the pointer) to "BelowPointer"; its number, 0, is unchanged.
    static void Migrate(JsonObject settings)
    {
        int version = settings["Version"] is JsonValue v && v.TryGetValue(out int n) ? n : 1;
        if (version < 2 && settings["Placement"] is JsonValue p && p.TryGetValue(out string? placement)
            && string.Equals(placement, "Cursor", StringComparison.OrdinalIgnoreCase))
            settings["Placement"] = nameof(NotifierPlacement.BelowPointer);
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
