namespace AgentsCursor.App.Services;

// OpenCode loads the plugins listed in its config's `plugins` array: a package specifier, or a local
// plugin directory as a file: URL, either as a bare string or as { package, options }. AgentsCursor's
// entry is the bare file: URL of the OpenCode plugin shipped next to the exe. The config is JSONC that
// people edit by hand, so it is changed by splicing text (JsoncEdit), never re-serialised, and always
// rewritten in place: Orca's OpenCode config overlay is a hard link to this file, and a temp file
// renamed over it would break the link and leave Orca reading a stale copy.
public static partial class AgentHookRegistrar
{
    const string OpenCodeFile = "OpenCode config";

    static string OpenCodeDir => ConfigDirFromEnvironment("OPENCODE_CONFIG_DIR") ?? Path.Combine(Home, ".config", "opencode");

    // %OPENCODE_CONFIG_DIR% (else ~/.config/opencode), opencode.jsonc — or opencode.json when only that exists.
    public static string OpenCodePath
    {
        get
        {
            string dir = OpenCodeDir, jsonc = Path.Combine(dir, "opencode.jsonc"), json = Path.Combine(dir, "opencode.json");
            return !File.Exists(jsonc) && File.Exists(json) ? json : jsonc;
        }
    }

    // A new config gets OpenCode's schema, as OpenCode itself writes it.
    const string NewOpenCodeConfig = "{\n  \"$schema\": \"https://opencode.ai/config.json\"\n}\n";

    // Today's entry: the shipped plugin folder as a file: URL.
    static string OpenCodeEntry(string pluginDir) => new Uri(pluginDir).AbsoluteUri;

    // Ours: a specifier (file: URL, absolute path, or a path relative to the config) of a folder, or its
    // index.ts, that holds AgentsCursor's OpenCode plugin: today's, an older or moved copy, or this
    // repository's checkout. A folder of someone else's that happens to be named integrations\opencode
    // is theirs.
    static bool IsOpenCodeEntry(string? specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier)) return false;
        string path;
        try
        {
            if (specifier.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) path = new Uri(specifier).LocalPath;
            else if (Path.IsPathFullyQualified(specifier)) path = specifier;
            else if (specifier.StartsWith('.')) path = Path.Combine(OpenCodeDir, specifier);
            else return false; // a package name
            path = Path.GetFullPath(path.Replace('/', '\\')).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (path.EndsWith("\\index.ts", StringComparison.OrdinalIgnoreCase)) path = path[..^"\\index.ts".Length];
        return HoldsOurPlugin(path, "opencode");
    }

    static List<JsoncEdit.Element> OpenCodeEntries(JsoncEdit.Document doc)
        => doc.Elements.Where(e => IsOpenCodeEntry(e.Specifier)).ToList();

    static HookState OpenCodeState()
    {
        try
        {
            string? text = ReadText(OpenCodePath);
            if (text is null) return HookState.NotRegistered;
            var ours = OpenCodeEntries(JsoncEdit.Parse(text, OpenCodeFile));
            if (ours.Count == 0) return HookState.NotRegistered;
            // Only the bare string counts as current: an { package, options } object carries the retired
            // plugin's own settings, which now live in AgentsCursor.
            return ours.Count == 1 && ours[0].IsString && ours[0].Specifier == OpenCodeEntry(PluginDir("opencode"))
                ? HookState.Registered : HookState.Outdated;
        }
        catch (InvalidOperationException) { return HookState.ConfigConflict; }
    }

    static void RegisterOpenCode()
    {
        string entry = OpenCodeEntry(ShippedPluginDir("opencode", "OpenCode"));
        string path = OpenCodePath;
        string text = ReadText(path) ?? NewOpenCodeConfig;
        // Extra entries of ours go, last first; then the first one becomes today's entry where it is,
        // so the user's plugin order is kept. With none, it is added at the end.
        while (true)
        {
            var doc = JsoncEdit.Parse(text, OpenCodeFile);
            var ours = OpenCodeEntries(doc);
            if (ours.Count > 1) { text = JsoncEdit.Remove(doc, ours[^1]); continue; }
            text = ours.Count == 1 ? JsoncEdit.Replace(doc, ours[0], entry) : JsoncEdit.Append(doc, entry);
            break;
        }
        BackupAndWrite(path, text);
    }

    static void UnregisterOpenCode()
    {
        string path = OpenCodePath;
        string? text = ReadText(path);
        if (text is null) return;
        bool changed = false;
        while (true)
        {
            var doc = JsoncEdit.Parse(text, OpenCodeFile);
            var ours = OpenCodeEntries(doc);
            if (ours.Count == 0) break;
            text = JsoncEdit.Remove(doc, ours[^1]);
            changed = true;
        }
        if (changed) BackupAndWrite(path, text);
    }
}
