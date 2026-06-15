using System.Text.Json;
using System.Text.Json.Serialization;

namespace WormsCursor.Core;

/// <summary>
/// The normalised agent event a <c>hook</c> bridge writes to the pipe — one JSON object per line.
/// Tool-specific payloads (Claude stdin, Codex argv, …) are mapped to this shape by the bridge;
/// the tray app deserializes it and feeds <see cref="AgentActivity"/>. UI-free; serialised with
/// the same System.Text.Json the settings store uses.
/// </summary>
public sealed class AgentEventMessage
{
    /// <summary>Originating tool: <c>claude-code</c>, <c>codex</c>, <c>cursor</c>, …</summary>
    public string Tool { get; set; } = "";

    /// <summary>Normalised event: <c>thinking_started | awaiting_user | turn_complete | tool_use |
    /// error | session_end</c>.</summary>
    public string Event { get; set; } = "";

    /// <summary>The tool's original event name, for logging/debugging (e.g. Claude's <c>Stop</c>).</summary>
    public string? RawEvent { get; set; }

    public string? SessionId { get; set; }
    public string? Project { get; set; }
    public string? Message { get; set; }
    public string? Ts { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialises to a single line (System.Text.Json escapes any newlines inside string
    /// values, so the line-delimited pipe protocol stays intact).</summary>
    public string ToJsonLine() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses one pipe line; returns null on malformed JSON (caller skips it).</summary>
    public static AgentEventMessage? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<AgentEventMessage>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Maps the normalised <see cref="Event"/> string onto an <see cref="AgentEventKind"/>.
    /// Returns false for an empty/unknown event (the tray then ignores the message).</summary>
    public bool TryGetKind(out AgentEventKind kind)
    {
        switch (Event)
        {
            case "thinking_started": kind = AgentEventKind.ThinkingStarted; return true;
            case "awaiting_user": kind = AgentEventKind.AwaitingUser; return true;
            case "turn_complete": kind = AgentEventKind.TurnComplete; return true;
            case "tool_use": kind = AgentEventKind.ToolUse; return true;
            case "error": kind = AgentEventKind.Error; return true;
            case "session_end": kind = AgentEventKind.SessionEnded; return true;
            default: kind = default; return false;
        }
    }
}
