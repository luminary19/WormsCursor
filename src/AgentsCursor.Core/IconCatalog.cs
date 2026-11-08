namespace AgentsCursor.Core;

/// <summary>One built-in token icon: its id (<c>&lt;tool&gt;.&lt;variant&gt;</c>), the agent it belongs to,
/// its display name, and the embedded SVG resource it is drawn from.</summary>
public sealed record IconInfo(string Id, string Tool, string Name, string Resource);

/// <summary>
/// The built-in token icons, one set per agent. Each agent's <c>original</c> is its own logo; the
/// tool ids match the bridge's and the plugins' normalised <c>tool</c> field. The SVGs are embedded in
/// this assembly under <c>Icons/&lt;tool&gt;/&lt;variant&gt;.svg</c>. Static and immutable: it holds only
/// strings, never a parsed document, so any thread may read it.
/// </summary>
public static class IconCatalog
{
    /// <summary>The agents AgentsCursor draws, in the order the panel lists them.</summary>
    public static IReadOnlyList<string> Tools { get; } = new[] { "claude-code", "codex", "opencode", "pi" };

    static readonly IconInfo[] All = Tools.Select(tool => Make(tool, "original", "Original")).ToArray();

    static IconInfo Make(string tool, string variant, string name)
        => new($"{tool}.{variant}", tool, name, $"Icons/{tool}/{variant}.svg");

    /// <summary>The icon with this id, or null when there is none.</summary>
    public static IconInfo? Find(string? id) => All.FirstOrDefault(i => i.Id == id);

    /// <summary><paramref name="tool"/>'s own logo, or null for a tool AgentsCursor doesn't know.</summary>
    public static IconInfo? Original(string? tool) => Find($"{tool}.original");

    /// <summary>Every built-in icon for <paramref name="tool"/>, its original first.</summary>
    public static IReadOnlyList<IconInfo> ForTool(string tool) => All.Where(i => i.Tool == tool).ToArray();
}
