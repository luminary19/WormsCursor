using System.Collections.Concurrent;

namespace AgentsCursor.Core;

/// <summary>
/// Where an icon's SVG text comes from. A built-in id reads its embedded resource. Anything that can't
/// be loaded falls back to the agent's original logo and is logged once per id, so a bad icon never
/// leaves the token blank. Thread-safe: it hands out strings, and every renderer parses its own
/// document from them.
/// </summary>
public static class IconSource
{
    /// <summary>Where a failed load is reported (the app wires its log); null drops the messages.</summary>
    public static Action<string>? Log { get; set; }

    static readonly ConcurrentDictionary<string, string> Texts = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, bool> Reported = new(StringComparer.Ordinal);

    /// <summary>The SVG text of <paramref name="iconId"/> for <paramref name="tool"/>, or of the tool's
    /// original when that icon can't be loaded; null only for a tool with no icons at all.</summary>
    public static string? Load(string tool, string iconId)
    {
        var original = IconCatalog.Original(tool);
        if (original is null) return null;
        var icon = IconCatalog.Find(iconId);
        if (icon is not null && icon.Tool == tool && TryRead(icon, out var text)) return text;
        if (Reported.TryAdd(iconId, true))
            Log?.Invoke($"icon '{iconId}' for {tool} couldn't be loaded; showing the original logo");
        return TryRead(original, out var fallback) ? fallback : null;
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
