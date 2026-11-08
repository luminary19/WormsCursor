using System.Text.Json.Nodes;

namespace AgentsCursor.App.Services;

// Pi loads extensions listed in its settings.json `extensions` array (files or directories, absolute
// paths allowed; `-path` and `!glob` exclude, `+path` includes exactly). AgentsCursor's entry is the
// absolute path of the Pi plugin shipped next to the exe. Pi's settings file is machine-written JSON,
// so it is merged as a JSON tree, like Claude's.
public static partial class AgentHookRegistrar
{
    const string PiFile = "Pi settings.json";

    // %PI_CODING_AGENT_DIR%\settings.json when set, else ~/.pi/agent/settings.json.
    public static string PiPath
        => Path.Combine(ConfigDirFromEnvironment("PI_CODING_AGENT_DIR") ?? Path.Combine(Home, ".pi", "agent"), "settings.json");

    // Ours: an inclusion (no `-`/`!` prefix; an optional `+` is ignored) of a folder, or its index.ts,
    // that holds AgentsCursor's Pi plugin: today's, or one an older or moved copy registered. A
    // relative path is relative to Pi's agent folder. A folder of someone else's that happens to be
    // named integrations\pi is theirs.
    static bool IsPiEntry(string entry)
    {
        if (entry.StartsWith('-') || entry.StartsWith('!')) return false;
        string path;
        try
        {
            path = Path.GetFullPath(entry.TrimStart('+').Replace('/', '\\'), Path.GetDirectoryName(PiPath)!).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (path.EndsWith("\\index.ts", StringComparison.OrdinalIgnoreCase)) path = path[..^"\\index.ts".Length];
        return HoldsOurPlugin(path, "pi");
    }

    // The settings' `extensions` array (null when absent), which must hold only strings.
    static JsonArray? PiExtensionsOf(JsonObject root)
    {
        if (!root.ContainsKey("extensions")) return null;
        if (root["extensions"] is not JsonArray list || list.Any(e => StringValue(e) is null))
            throw new InvalidOperationException($"{PiFile}: extensions must be a list of paths.");
        return list;
    }

    static HookState PiState()
    {
        try
        {
            var root = ReadJsonObject(PiPath);
            var list = root is null ? null : PiExtensionsOf(root);
            var ours = list?.Select(StringValue).Where(e => IsPiEntry(e!)).ToArray() ?? Array.Empty<string?>();
            if (ours.Length == 0) return HookState.NotRegistered;
            return ours.Length == 1 && ours[0] == PluginDir("pi") ? HookState.Registered : HookState.Outdated;
        }
        catch (InvalidOperationException) { return HookState.ConfigConflict; }
    }

    static void RegisterPi()
    {
        string plugin = ShippedPluginDir("pi", "Pi");
        var root = ReadJsonObject(PiPath) ?? new JsonObject();
        var list = PiExtensionsOf(root);
        if (list is null) { list = new JsonArray(); root["extensions"] = list; }
        // Today's path takes the place of the first entry of ours, so the user's order is kept; any
        // other entries of ours (duplicates, older copies) go.
        int first = -1;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!IsPiEntry(StringValue(list[i])!)) continue;
            if (first >= 0) list.RemoveAt(first);
            first = i;
        }
        if (first >= 0) list[first] = plugin;
        else list.Add((JsonNode)plugin); // the non-generic Add: a plain string node
        BackupAndWrite(PiPath, root.ToJsonString(Indented));
    }

    static void UnregisterPi()
    {
        var root = ReadJsonObject(PiPath);
        var list = root is null ? null : PiExtensionsOf(root);
        if (list is null) return;
        int before = list.Count;
        for (int i = list.Count - 1; i >= 0; i--)
            if (IsPiEntry(StringValue(list[i])!)) list.RemoveAt(i);
        if (list.Count == before) return; // nothing of ours: leave the file alone
        if (list.Count == 0) root!.Remove("extensions");
        BackupAndWrite(PiPath, root!.ToJsonString(Indented));
    }
}
