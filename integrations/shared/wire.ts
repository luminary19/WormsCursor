import { userInfo } from "node:os";

/** The agents whose plugins report to AgentsCursor over a persistent connection. */
export type PluginTool = "pi" | "opencode";

/**
 * The events a plugin sends: AgentsCursor's normalised events (the same ones the Claude Code and
 * Codex hooks produce), plus `attach`, the connection-level first line that ties this host's sessions
 * to its connection, so the tray drops them when the connection closes.
 */
export type WireEvent = "attach" | "thinking_started" | "awaiting_user" | "input_answered" | "turn_complete" | "session_end";

/** One line on the pipe: AgentsCursor's `AgentEventMessage`, camelCase JSON. */
export interface WireMessage {
  tool: PluginTool;
  event: WireEvent;
  sessionId?: string;
  project?: string;
  /** The prompts an `awaiting_user` opens, or an `input_answered` answers. */
  inputIds?: string[];
}

/** The tray's pipe name, the same one AgentsCursor derives: `AgentsCursor.<user name>`. */
export function defaultPipeName(): string {
  return `AgentsCursor.${userInfo().username}`;
}

export function pipePath(name: string): string {
  return `\\\\.\\pipe\\${name}`;
}

/** One message as a pipe line. */
export function encode(message: WireMessage): string {
  return `${JSON.stringify(message)}\n`;
}
