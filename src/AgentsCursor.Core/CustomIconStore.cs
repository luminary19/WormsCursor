using System.Security.Cryptography;
using System.Text;

namespace AgentsCursor.Core;

/// <summary>
/// The SVG icons the user added, kept as <c>&lt;root&gt;\&lt;tool&gt;\&lt;hash&gt;.svg</c> (by default under
/// <c>%LocalAppData%\AgentsCursor\icons</c>, next to the settings, so app updates leave them alone).
/// Each file holds the <see cref="SvgSanitizer"/> output, never the original, and is named by the first
/// 64 bits of its SHA-256, which is also its icon id (<c>custom:&lt;hash&gt;</c>): adding the same picture
/// twice gives the same icon, and a file never changes once written. It is written to a temporary name
/// first and then moved into place, so a renderer never reads half a file.
///
/// Thread-safe: <see cref="Add"/> runs on a background task (its quota check and write are one locked
/// step), everything else on the UI thread, and the renderers only read finished files through
/// <see cref="IconSource"/>.
/// </summary>
public sealed class CustomIconStore
{
    /// <summary>The most icons kept per agent.</summary>
    public const int MaxPerTool = 24;

    /// <summary>How long checking that an icon draws may take before it is turned down.</summary>
    public static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Where the icons live unless a test says otherwise.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentsCursor", "icons");

    readonly object _write = new(); // the quota check and the write happen as one step
    const int ProbePx = 64;      // the size an added icon is test-drawn at
    const int MinVisiblePx = 4;  // it must cover at least this much, each way, at that size

    public CustomIconStore(string root) => Root = root;

    public string Root { get; }

    /// <summary>The file of <paramref name="iconId"/> for <paramref name="tool"/>, or null when either
    /// isn't well-formed (so a hand-edited id can never point outside the store).</summary>
    public string? PathFor(string tool, string iconId)
        => IconCatalog.IsTool(tool) && IconCatalog.IsCustomId(iconId)
            ? Path.Combine(Root, tool, iconId[IconCatalog.CustomPrefix.Length..] + ".svg")
            : null;

    /// <summary><paramref name="tool"/>'s added icons, oldest first.</summary>
    public IReadOnlyList<string> List(string tool)
    {
        if (!IconCatalog.IsTool(tool)) return Array.Empty<string>();
        var dir = new DirectoryInfo(Path.Combine(Root, tool));
        if (!dir.Exists) return Array.Empty<string>();
        try
        {
            return dir.EnumerateFiles("*.svg")
                .Select(f => (Id: IconCatalog.CustomPrefix + Path.GetFileNameWithoutExtension(f.Name), f.CreationTimeUtc))
                .Where(f => IconCatalog.IsCustomId(f.Id))
                .OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Id, StringComparer.Ordinal)
                .Select(f => f.Id)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Sanitises <paramref name="svgText"/>, checks that it draws something within
    /// <see cref="RenderTimeout"/>, and keeps it for <paramref name="tool"/>; returns its icon id (the
    /// existing one when the same picture was added before). Throws <see cref="InvalidIconException"/>
    /// with the reason when it can't be used, and <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> when it can't be saved.</summary>
    public string Add(string tool, string svgText)
    {
        if (!IconCatalog.IsTool(tool)) throw new ArgumentException($"Unknown agent '{tool}'.", nameof(tool));
        string clean = Prepare(svgText);
        string id = IconCatalog.CustomPrefix + Hash(clean);
        string path = PathFor(tool, id)!;
        lock (_write)
        {
            if (File.Exists(path)) return id;
            if (List(tool).Count >= MaxPerTool)
                throw new InvalidIconException($"you already have {MaxPerTool} of your own icons for this agent; remove one first");
            Write(path, clean);
        }
        return id;
    }

    static void Write(string path, string clean)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, clean, new UTF8Encoding(false));
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { /* the same picture landed meanwhile */ }
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes <paramref name="iconId"/> from <paramref name="tool"/>'s icons. If the agent is
    /// showing it, the agent goes back to its original logo first (through
    /// <paramref name="settings"/>), so the token never points at a file that is gone.</summary>
    public void Remove(string tool, string iconId, LiveSettings settings)
    {
        string path = PathFor(tool, iconId) ?? throw new ArgumentException($"'{iconId}' isn't an added icon.", nameof(iconId));
        if (settings.Live.IconFor(tool) == iconId)
            settings.Change(s => s.Icons = IconChoices.With(s.Icons, tool, IconCatalog.OriginalId(tool)));
        File.Delete(path);
    }

    // Sanitising and the test drawing run on their own task, so a pathological file can't stall the
    // caller past the timeout. (A drawing that never ends is abandoned, not killed; it holds no lock.)
    static string Prepare(string svgText)
    {
        var work = Task.Run(() =>
        {
            string clean = SvgSanitizer.Sanitize(svgText);
            var visible = TokenSprites.VisibleSize(clean, ProbePx);
            return (clean, visible);
        });
        try
        {
            if (!work.Wait(RenderTimeout)) throw new InvalidIconException("it rendered empty or took too long");
        }
        catch (AggregateException ex) when (ex.InnerException is InvalidIconException invalid)
        {
            throw invalid;
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            throw new InvalidIconException($"it couldn't be drawn ({ex.InnerException.Message.TrimEnd('.')})");
        }
        var (text, size) = work.Result;
        if (size is not { } s || s.Width < MinVisiblePx || s.Height < MinVisiblePx)
            throw new InvalidIconException("it rendered empty or took too long");
        return text;
    }

    static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
}
