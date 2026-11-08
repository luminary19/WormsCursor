using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace AgentsCursor.Core;

/// <summary>One built-in token icon: its id (<c>&lt;tool&gt;.&lt;variant&gt;</c>), the agent it belongs to,
/// its display name, and the embedded SVG resource it is drawn from.</summary>
public sealed record IconInfo(string Id, string Tool, string Name, string Resource);

/// <summary>
/// The built-in token icons, one set per agent: its <c>original</c> logo first, then the playful mascot
/// takes on it. The tool ids match the bridge's and the plugins' normalised <c>tool</c> field. The SVGs
/// are embedded in this assembly under <c>Icons/&lt;tool&gt;/&lt;variant&gt;.svg</c>. Static and immutable:
/// it holds only strings, never a parsed document, so any thread may read it.
///
/// An uploaded icon has the id <c>custom:&lt;16 hex digits&gt;</c> (the first 64 bits of its SHA-256), and
/// lives in <see cref="CustomIconStore"/>.
/// </summary>
public static class IconCatalog
{
    /// <summary>The agents AgentsCursor draws, in the order the panel lists them.</summary>
    public static IReadOnlyList<string> Tools { get; } = new[] { "claude-code", "codex", "opencode", "pi" };

    /// <summary>The agent's short name, as the icon gallery labels it ("Codex", not the connection's
    /// "Codex (desktop & CLI)").</summary>
    public static string AgentName(string tool) => tool switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        "opencode" => "OpenCode",
        "pi" => "Pi",
        _ => tool,
    };

    /// <summary>What an uploaded icon's id starts with.</summary>
    public const string CustomPrefix = "custom:";

    static readonly IconInfo[] All =
    {
        Make("claude-code", "original", "Original"),
        Make("claude-code", "waving", "Waving"),
        Make("claude-code", "party", "Party"),
        Make("claude-code", "peek", "Peekaboo"),
        Make("codex", "original", "Original"),
        Make("codex", "buddy", "Buddy"),
        Make("codex", "spin", "Spin"),
        Make("codex", "sleepy", "Sleepy"),
        Make("opencode", "original", "Original"),
        Make("opencode", "buddy", "Buddy"),
        Make("opencode", "brackets", "Brackets"),
        Make("opencode", "stack", "Stack"),
        Make("pi", "original", "Original"),
        Make("pi", "walker", "Walker"),
        Make("pi", "shades", "Shades"),
        Make("pi", "pie", "Pie"),
    };

    static readonly Regex CustomId = new("^custom:[0-9a-f]{16}$", RegexOptions.CultureInvariant);

    static IconInfo Make(string tool, string variant, string name)
        => new($"{tool}.{variant}", tool, name, $"Icons/{tool}/{variant}.svg");

    /// <summary>Every built-in icon, grouped by agent in <see cref="Tools"/> order.</summary>
    public static IReadOnlyList<IconInfo> BuiltIns => All;

    /// <summary>The built-in icon with this id, or null when there is none.</summary>
    public static IconInfo? Find(string? id) => All.FirstOrDefault(i => i.Id == id);

    /// <summary><paramref name="tool"/>'s own logo, or null for a tool AgentsCursor doesn't know.</summary>
    public static IconInfo? Original(string? tool) => Find($"{tool}.original");

    /// <summary>The id of <paramref name="tool"/>'s own logo.</summary>
    public static string OriginalId(string tool) => tool + ".original";

    /// <summary>Every built-in icon for <paramref name="tool"/>, its original first.</summary>
    public static IReadOnlyList<IconInfo> ForTool(string tool) => All.Where(i => i.Tool == tool).ToArray();

    /// <summary>Whether <paramref name="tool"/> is one of <see cref="Tools"/>.</summary>
    public static bool IsTool(string? tool) => tool is not null && Tools.Contains(tool);

    /// <summary>Whether <paramref name="id"/> is a well-formed uploaded-icon id (so it is safe to use in
    /// a file name).</summary>
    public static bool IsCustomId(string? id) => id is not null && CustomId.IsMatch(id);
}

/// <summary>
/// The per-agent icon choice (<see cref="CursorSettings.Icons"/>): normalisation into a fresh
/// read-only dictionary, and the edits the panel makes, each returning a new one.
/// </summary>
public static class IconChoices
{
    /// <summary>No choices: every agent shows its original logo.</summary>
    public static IReadOnlyDictionary<string, string> None { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>A fresh read-only copy of <paramref name="choices"/> holding only what means something:
    /// known tools, mapped to one of that tool's built-in icons other than its original, or to a
    /// well-formed <c>custom:</c> id (kept even when its file is gone; the renderer falls back). Values
    /// are trimmed; null gives <see cref="None"/>.</summary>
    public static IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? choices)
    {
        if (choices is null || choices.Count == 0) return None;
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tool, value) in choices)
        {
            string? id = value?.Trim();
            if (!IconCatalog.IsTool(tool) || string.IsNullOrEmpty(id) || id == IconCatalog.OriginalId(tool)) continue;
            bool builtIn = IconCatalog.Find(id)?.Tool == tool;
            if (builtIn || IconCatalog.IsCustomId(id)) kept[tool] = id;
        }
        return kept.Count == 0 ? None : new ReadOnlyDictionary<string, string>(kept);
    }

    /// <summary><paramref name="choices"/> with <paramref name="tool"/> showing <paramref name="iconId"/>.</summary>
    public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> choices, string tool, string iconId)
        => Normalize(new Dictionary<string, string>(choices) { [tool] = iconId });
}
