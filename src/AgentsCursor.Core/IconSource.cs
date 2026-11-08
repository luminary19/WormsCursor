using System.Collections.Concurrent;

namespace AgentsCursor.Core;

/// <summary>
/// Where an icon's SVG text comes from. A built-in id reads its embedded resource; a <c>custom:</c> id
/// reads the file in <see cref="Custom"/> and runs it through <see cref="SvgSanitizer"/> again (a file
/// changed on disk gets no free pass). Anything that can't be loaded falls back to the agent's original
/// logo and is logged once per id, so a bad icon never leaves the token blank. Thread-safe: it hands
/// out strings, and every renderer parses its own document from them.
/// </summary>
public static class IconSource
{
    /// <summary>Where a failed load is reported (the app wires its log); null drops the messages.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>The added icons. Set once at startup (tests point it at a temp folder).</summary>
    public static CustomIconStore Custom
    {
        get => _custom;
        set => _custom = value ?? throw new ArgumentNullException(nameof(value));
    }
    static volatile CustomIconStore _custom = new(CustomIconStore.DefaultRoot);

    static readonly ConcurrentDictionary<string, string> Texts = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, bool> Reported = new(StringComparer.Ordinal);

    /// <summary>The SVG text of <paramref name="iconId"/> for <paramref name="tool"/>, or of the tool's
    /// original when that icon can't be loaded; null only for a tool with no icons at all.</summary>
    public static string? Load(string tool, string iconId)
    {
        var original = IconCatalog.Original(tool);
        if (original is null) return null;
        if (TryLoad(tool, iconId, out var text)) return text;
        Report(tool, iconId, "couldn't be loaded");
        return TryRead(original, out var fallback) ? fallback : null;
    }

    /// <summary>Logs, once per icon, that <paramref name="iconId"/> is being replaced by the original.</summary>
    internal static void Report(string tool, string iconId, string what)
    {
        if (Reported.TryAdd(tool + "\n" + iconId, true))
            Log?.Invoke($"icon '{iconId}' for {tool} {what}; showing the original logo");
    }

    static bool TryLoad(string tool, string iconId, out string text)
    {
        text = "";
        if (IconCatalog.Find(iconId) is { } icon) return icon.Tool == tool && TryRead(icon, out text);
        if (_custom.PathFor(tool, iconId) is not { } path) return false;
        if (Texts.TryGetValue(path, out text!)) return true;
        try
        {
            text = Texts.GetOrAdd(path, SvgSanitizer.Sanitize(File.ReadAllText(path)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidIconException)
        {
            text = "";
            return false;
        }
    }

    static bool TryRead(IconInfo icon, out string text)
    {
        if (Texts.TryGetValue(icon.Resource, out text!)) return true;
        using var stream = typeof(IconSource).Assembly.GetManifestResourceStream(icon.Resource);
        if (stream is null) return false;
        using var reader = new StreamReader(stream);
        text = Texts.GetOrAdd(icon.Resource, reader.ReadToEnd());
        return true;
    }
}
