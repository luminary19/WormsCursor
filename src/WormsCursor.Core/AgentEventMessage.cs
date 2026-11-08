using System.Text.Json;
using System.Text.Json.Serialization;

namespace WormsCursor.Core;

/// <summary>
/// The normalised agent event a <c>hook</c> bridge writes to the pipe — one JSON object per line.
/// Tool-specific stdin payloads are mapped to this shape by the bridge;
/// the tray app deserializes it and feeds <see cref="AgentActivity"/>. UI-free; serialised with
/// the same System.Text.Json the settings store uses.
/// </summary>
public sealed class AgentEventMessage
{
    /// <summary>Originating tool: <c>claude-code</c>, <c>codex</c>, <c>cursor</c>, …</summary>
    public string Tool { get; set; } = "";

    /// <summary>Normalised event: <c>thinking_started | awaiting_user | turn_complete | tool_use |
    /// tools_resolved | error | session_end | input_requested | input_answered</c>.</summary>
    public string Event { get; set; } = "";

    /// <summary>The tool's original event name, for logging/debugging (e.g. Claude's <c>Stop</c>).</summary>
    public string? RawEvent { get; set; }

    public string? SessionId { get; set; }
    /// <summary>The subagent that raised the event; absent for the main agent. Subagents share their
    /// parent's <see cref="SessionId"/>, so this is what scopes an approval prompt to one agent.</summary>
    public string? AgentId { get; set; }
    public string? Project { get; set; }
    public string? Message { get; set; }
    public string? Ts { get; set; }
    /// <summary>Stable ids of the items this event opens or answers: Codex asynchronous questions and
    /// their individual replies, or Claude MCP elicitations and their results.</summary>
    public string[]? InputIds { get; set; }
    /// <summary>The tool call an agent's prompt blocks (on <c>awaiting_user</c>), or the call that just
    /// finished (on <c>tool_use</c>): its tool-call id and/or a fingerprint of its tool name and input,
    /// whichever the tool's payload carries. See <see cref="AgentActivity"/>.</summary>
    public string[]? CallIds { get; set; }

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
            case "tools_resolved": kind = AgentEventKind.ToolsResolved; return true;
            case "error": kind = AgentEventKind.Error; return true;
            case "session_end": kind = AgentEventKind.SessionEnded; return true;
            case "input_requested": kind = AgentEventKind.InputRequested; return true;
            case "input_answered": kind = AgentEventKind.InputAnswered; return true;
            default: kind = default; return false;
        }
    }
}
