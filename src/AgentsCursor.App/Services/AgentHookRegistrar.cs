using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentsCursor.App.Services;

/// <summary>Whether a tool's config currently carries our connection.</summary>
public enum HookState
{
    /// <summary>No AgentsCursor connection present.</summary>
    NotRegistered,
    /// <summary>Our connection is there in exactly today's shape.</summary>
    Registered,
    /// <summary>Our connection is present but not in today's shape: an older version registered a smaller
    /// event set (e.g. before <c>SessionEnd</c> was added, so /clear and /exit no longer clear the
    /// indicator), a different command or plugin path (an old exe path, a moved checkout, the
    /// WormsCursor.exe this app was called before), a command form the tool's shell rejects, or an entry
    /// we no longer use. Re-registering refreshes it in place.</summary>
    Outdated,
    /// <summary>The config is malformed or unreadable, so it cannot be merged without risking user data.</summary>
    ConfigConflict,
}

/// <summary>A supported agent tool and where its config lives. <paramref name="AfterChange"/> is the step
/// the tool itself needs after AgentsCursor changes that config, before the change takes effect.</summary>
public sealed record HookTool(string Id, string DisplayName, string ConfigHint, string AfterChange);

/// <summary>One connection <see cref="AgentHookRegistrar.RefreshConnected"/> brought up to date:
/// <paramref name="Error"/> is null when it worked, else why it couldn't be written.</summary>
public sealed record RefreshResult(HookTool Tool, string? Error);

/// <summary>
/// Connects / disconnects / inspects AgentsCursor in each AI agent tool's config, so the tools report
/// to the running tray app: Claude Code and Codex run the <c>hook</c> bridge from their lifecycle hooks
/// (<c>HooksJson.cs</c>), OpenCode and Pi load the plugin shipped next to the exe
/// (<c>OpenCodePlugins.cs</c>, <c>PiExtensions.cs</c>). The first write backs the original file up to
/// <c>&lt;file&gt;.agentscursor.bak</c> (never overwritten afterwards, so it keeps the pre-AgentsCursor
/// file), every write merges rather than overwrites, and a malformed config is reported (never
/// clobbered). Our entries are recognised by a marker that survives a moved exe (the
/// <c>hook --tool &lt;id&gt;</c> arguments, or a plugin path ending in <c>integrations\&lt;id&gt;</c>), so
/// they can be found again across reinstalls, including the WormsCursor.exe this app was called before
/// its rename — and only those entries are ever removed, never a user's own.
/// </summary>
public static partial class AgentHookRegistrar
{
    // Relaxed encoder so the exe-path quotes serialise as \" (not ") and the file stays
    // readable — it's a config a human may open. Still valid JSON.
    static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Test seam: when set, config paths resolve under this directory instead of the real
    /// user profile (and the tools' config-dir environment variables are ignored), so the merge logic
    /// can be exercised without touching real tool configs.</summary>
    public static string? HomeOverride;

    static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AgentsCursor.exe");
    static string Home => HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // A tool's config directory from its environment variable, unless the test seam is set.
    static string? ConfigDirFromEnvironment(string variable)
    {
        if (HomeOverride is not null) return null;
        string? dir = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    /// <summary>The plugin folder shipped next to the exe for <paramref name="toolId"/> (<c>pi</c>,
    /// <c>opencode</c>): <c>&lt;exe dir&gt;\integrations\&lt;id&gt;</c>. Derived from the same exe path as
    /// the hook commands, so a moved app reads as outdated the same way for every tool.</summary>
    public static string PluginDir(string toolId) => Path.Combine(Path.GetDirectoryName(Exe)!, "integrations", toolId);

    // The plugin folder to register, which must exist: a build without the integrations can't connect.
    static string ShippedPluginDir(string toolId, string name)
    {
        string dir = PluginDir(toolId);
        if (!File.Exists(Path.Combine(dir, "index.ts")))
            throw new InvalidOperationException($"This copy of AgentsCursor doesn't include the {name} plugin.");
        return dir;
    }

    public static IReadOnlyList<HookTool> Tools { get; } = new[]
    {
        new HookTool("claude-code", "Claude Code", "~/.claude/settings.json",
            "Start a new Claude Code session to pick it up."),
        // Codex runs a changed hook only after you trust it (Codex: /hooks), separately in Orca.
        new HookTool("codex", "Codex (desktop + CLI)", "CODEX_HOME/hooks.json",
            "In Codex, approve the changed AgentsCursor hooks under /hooks, also in sessions Orca starts, then start a new session."),
        new HookTool("opencode", "OpenCode", "OPENCODE_CONFIG_DIR/opencode.jsonc",
            "Run `opencode reload` in open OpenCode sessions, or start a new one. Orca picks it up too."),
        new HookTool("pi", "Pi", "PI_CODING_AGENT_DIR/settings.json",
            "Run /reload in open Pi sessions, or start a new one."),
    };

    /// <summary>The config file <paramref name="toolId"/>'s connection is written to.</summary>
    public static string ConfigPath(string toolId) => toolId switch
    {
        "claude-code" => ClaudePath,
        "codex" => CodexPath,
        "opencode" => OpenCodePath,
        "pi" => PiPath,
        _ => throw UnknownTool(toolId),
    };

    public static HookState GetState(string toolId) => toolId switch
    {
        "claude-code" => ClaudeState(),
        "codex" => CodexState(),
        "opencode" => OpenCodeState(),
        "pi" => PiState(),
        _ => HookState.NotRegistered,
    };

    public static void Register(string toolId)
    {
        switch (toolId)
        {
            case "claude-code": RegisterClaude(); break;
            case "codex": RegisterCodex(); break;
            case "opencode": RegisterOpenCode(); break;
            case "pi": RegisterPi(); break;
            default: throw UnknownTool(toolId);
        }
    }

    public static void Unregister(string toolId)
    {
        switch (toolId)
        {
            case "claude-code": UnregisterFrom(ClaudePath, ClaudeFile, IsClaudeHandler); break;
            case "codex": UnregisterFrom(CodexPath, CodexFile, IsCodexHandler); break;
            case "opencode": UnregisterOpenCode(); break;
            case "pi": UnregisterPi(); break;
            default: throw UnknownTool(toolId);
        }
    }

    /// <summary>Keeps the connections the user made current: every tool whose connection is ours but
    /// <see cref="HookState.Outdated"/> (another exe or plugin path, an older shape, missing events) is
    /// re-registered in place. Tools that aren't connected, are already current, or whose config can't
    /// be read are left untouched: no write and no backup (so, for Codex, no hook re-approval). Returns
    /// one result per tool it tried, with the error when the write failed.</summary>
    public static IReadOnlyList<RefreshResult> RefreshConnected()
    {
        var results = new List<RefreshResult>();
        foreach (var tool in Tools)
        {
            if (GetState(tool.Id) != HookState.Outdated) continue;
            try
            {
                Register(tool.Id);
                results.Add(new RefreshResult(tool, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                results.Add(new RefreshResult(tool, ex.Message));
            }
        }
        return results;
    }

    static ArgumentException UnknownTool(string toolId)
        => new($"Unknown tool '{toolId}' (expected one of: {string.Join(", ", Tools.Select(t => t.Id))}).", nameof(toolId));

    // ---------- shared file IO ----------

    static string? StringValue(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // The file's text, or null when it doesn't exist; an unreadable file is a ConfigConflict.
    static string? ReadText(string path)
    {
        if (!File.Exists(path)) return null;
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} can't be read ({ex.Message}) — check it isn't locked or read-protected, then retry.");
        }
    }

    static JsonObject? ReadJsonObject(string path)
    {
        string? text = ReadText(path);
        if (text is null) return null;
        try { return JsonNode.Parse(text) as JsonObject ?? throw new JsonException("Expected an object."); }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} isn't valid JSON — fix or remove it, then retry.");
        }
    }

    // The backup is taken once and never overwritten: a later write would otherwise replace the
    // user's original with our already-modified file, losing the only pre-AgentsCursor copy. A config
    // this app already changed under its old name, WormsCursor, has that original in the old-named
    // backup; the file now holds WormsCursor's hooks, so it must not become the backup.
    //
    // The file is rewritten in place (truncated, never replaced by a renamed temp file), so every hard
    // link to it sees the change: Orca's OpenCode config overlay is a hard link to the user's config.
    // A UTF-8 byte order mark the file had is kept.
    static void BackupAndWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string backup = path + ".agentscursor.bak", legacyBackup = path + ".wormscursor.bak";
        bool exists = File.Exists(path);
        if (exists && !File.Exists(backup) && !File.Exists(legacyBackup)) File.Copy(path, backup);
        File.WriteAllText(path, content, new UTF8Encoding(exists && HasBom(path)));
    }

    static bool HasBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[3];
        return stream.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }
}
