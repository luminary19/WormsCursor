using System.Security.Cryptography;
using System.Text;

namespace AgentsCursor.Core;

/// <summary>
/// The SVG icons the user added, kept as <c>&lt;root&gt;\&lt;tool&gt;\&lt;hash&gt;.svg</c> (by default under
/// <c>%LocalAppData%\AgentsCursor\icons</c>, next to the settings, so app updates leave them alone).
/// Each file holds the <see cref="SvgSanitizer"/> output, never the original, and is named by the first
/// 64 bits of its SHA-256, which is also its icon id (<c>custom:&lt;hash&gt;</c>): adding the same picture
/// twice gives the same icon, and a file never changes once written. It is written to a temporary name
/// first and then moved into place, so a renderer never reads half a file. The name of the file it came
/// from, tidied, is kept beside it as <c>&lt;hash&gt;.name</c>, so the panel can call it by that name.
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
    const int MaxNameLength = 48;

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

    /// <summary>The name <paramref name="iconId"/> was added under (its file's name), or null when
    /// none was kept.</summary>
    public string? NameOf(string tool, string iconId)
    {
        if (PathFor(tool, iconId) is not { } path) return null;
        try { return File.Exists(NamePath(path)) ? CleanName(File.ReadAllText(NamePath(path))) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Sanitises <paramref name="svgText"/>, checks that it draws something within
    /// <see cref="RenderTimeout"/>, and keeps it for <paramref name="tool"/> under
    /// <paramref name="name"/> (a file name, tidied; optional). Returns its icon id: the existing one,
    /// with its name, when the same picture was added before. Throws <see cref="InvalidIconException"/>
    /// with the reason when it can't be used, and <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> when it can't be saved.</summary>
    public string Add(string tool, string svgText, string? name = null)
    {
        if (!IconCatalog.IsTool(tool)) throw new ArgumentException($"Unknown agent '{tool}'.", nameof(tool));
        string clean = Prepare(svgText);
        string id = IconCatalog.CustomPrefix + Hash(clean);
        Keep(tool, id, clean, CleanName(name), created: null);
        return id;
    }

    // Writes the icon and its name unless it is already kept, within the per-agent limit. With
    // <created>, the file gets that creation time, which sets its place in List.
    void Keep(string tool, string id, string clean, string? name, DateTime? created)
    {
        string path = PathFor(tool, id)!;
        lock (_write)
        {
            if (File.Exists(path)) return;
            if (List(tool).Count >= MaxPerTool)
                throw new InvalidIconException($"you already have {MaxPerTool} of your own icons for this agent; remove one first");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (name is not null) File.WriteAllText(NamePath(path), name, new UTF8Encoding(false));
            Write(path, clean);
            if (created is { } time) File.SetCreationTimeUtc(path, time);
        }
    }

    static void Write(string path, string clean)
    {
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
    /// <paramref name="settings"/>), so the token never points at a file that is gone. Returns what
    /// <see cref="Restore"/> needs to put it back.</summary>
    public RemovedIcon Remove(string tool, string iconId, LiveSettings settings)
    {
        string path = PathFor(tool, iconId) ?? throw new ArgumentException($"'{iconId}' isn't an added icon.", nameof(iconId));
        var removed = new RemovedIcon(tool, iconId, File.ReadAllText(path), NameOf(tool, iconId),
            File.GetCreationTimeUtc(path), settings.Live.IconFor(tool) == iconId);
        if (removed.WasShown)
            settings.Change(s => s.Icons = IconChoices.With(s.Icons, tool, IconCatalog.OriginalId(tool)));
        File.Delete(path);
        File.Delete(NamePath(path));
        return removed;
    }

    /// <summary>Puts a removed icon back as it was: the same id, name and place in the list, and the
    /// agent's icon again if it was. It counts toward the limit, and throws as <see cref="Add"/> does.</summary>
    public void Restore(RemovedIcon removed, LiveSettings settings)
    {
        ArgumentNullException.ThrowIfNull(removed);
        string clean = SvgSanitizer.Sanitize(removed.Text); // it was read back from disk
        if (IconCatalog.CustomPrefix + Hash(clean) != removed.Id)
            throw new InvalidIconException("it changed on disk before it was removed");
        Keep(removed.Tool, removed.Id, clean, removed.Name, removed.CreatedUtc);
        if (removed.WasShown)
            settings.Change(s => s.Icons = IconChoices.With(s.Icons, removed.Tool, removed.Id));
    }

    static string NamePath(string svgPath) => Path.ChangeExtension(svgPath, ".name");

    // A file name as a label: no folder or extension, no control characters, single spaces, not too long.
    static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string stem = Path.GetFileNameWithoutExtension(name.Trim());
        string text = string.Join(' ', new string(stem.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > MaxNameLength) text = text[..(MaxNameLength - 1)].TrimEnd() + "…";
        return text.Length == 0 ? null : text;
    }

    // Sanitising and the test drawing run on their own task, so a big file can't stall the caller past
    // the timeout. The sanitiser's reference limits (SvgReferences) guarantee that the drawing ends, so
    // one still running at the timeout finishes in the background, holding no lock, and is dropped.
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
            if (!work.Wait(RenderTimeout)) throw new InvalidIconException("it took too long to draw");
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
            throw new InvalidIconException("it doesn't draw anything visible");
        return text;
    }

    static string Hash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();
}

/// <summary>An icon <see cref="CustomIconStore.Remove"/> deleted: everything
/// <see cref="CustomIconStore.Restore"/> needs to put it back as it was.</summary>
public sealed record RemovedIcon(string Tool, string Id, string Text, string? Name, DateTime CreatedUtc, bool WasShown);
