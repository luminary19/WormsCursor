using System.Text.Json;
using System.Text.Json.Serialization;

namespace WormsCursor.Core;

/// <summary>
/// Loads/saves <see cref="CursorSettings"/> as JSON in %LocalAppData%\WormsCursor\.
/// Mirrors PowerLink's store: indented JSON, atomic write (tmp + replace), tolerant
/// load (corrupt/missing → defaults). Living in LocalAppData (not next to the exe)
/// means an installer/update that replaces the app folder leaves settings untouched.
/// </summary>
public static class SettingsStore
{
    // Enums as readable names (e.g. "Corner"/"BottomRight"), not bare integers, so the settings
    // file stays human-editable. The converter also accepts numbers on read, so any legacy value loads.
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WormsCursor",
        "settings.json");

    /// <summary>Loads settings, or returns normalized defaults if missing/corrupt.</summary>
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
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                // Fall through to defaults on a corrupt/unreadable file.
            }
        }
        return new CursorSettings();
    }

    /// <summary>Saves settings atomically (write tmp, then replace).</summary>
    public static void Save(CursorSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var target = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tmp = target + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(target))
            File.Replace(tmp, target, destinationBackupFileName: null);
        else
            File.Move(tmp, target);
    }
}
