import { TrayClient, type LinkFactory, type TrayLink } from "../shared/tray-client.ts";
import type { WireMessage } from "../shared/wire.ts";

/** What this extension reads from Pi's `ExtensionContext` (checked against Pi's types in tests/contracts.ts). */
export interface PiContext {
  mode: string;
  hasUI: boolean;
  cwd: string;
  sessionManager: { getSessionId(): string };
}

export interface PiSessionEvent { reason: string }
export interface PiSettledEvent { aborted: boolean }
export interface PiPromptEvent { kind: string }

/** The part of Pi's `ExtensionAPI` this extension uses. */
export interface PiLike {
  on(event: "session_start", handler: (event: PiSessionEvent, ctx: PiContext) => unknown): unknown;
  on(event: "session_shutdown", handler: (event: PiSessionEvent, ctx: PiContext) => unknown): unknown;
  on(event: "agent_start", handler: (event: unknown, ctx: PiContext) => unknown): unknown;
  on(event: "agent_settled", handler: (event: PiSettledEvent, ctx: PiContext) => unknown): unknown;
  on(event: "ui_prompt_start", handler: (event: PiPromptEvent, ctx: PiContext) => unknown): unknown;
  on(event: "ui_prompt_end", handler: (event: PiPromptEvent, ctx: PiContext) => unknown): unknown;
}

const defaultLink: LinkFactory = (snapshot) => new TrayClient({ tool: "pi" }, snapshot);

/**
 * One interactive Pi session's report to AgentsCursor. Pi wraps every extension prompt (select,
 * confirm, input, editor, custom: the permission system's approvals and the question tool alike) in
 * `ui_prompt_start` / `ui_prompt_end`, depth-counted so the pairs never nest. A prompt raised during an
 * agent run means the agent is blocked on you; one raised while Pi is idle comes from a command you
 * just typed, so you are already at the keyboard and nothing waits.
 */
class PiSession {
  private readonly sessionId: string;
  private readonly project: string;
  private readonly link: TrayLink;
  private running = false;
  private counter = 0;
  private readonly open: string[] = [];

  constructor(sessionId: string, project: string, makeLink: LinkFactory) {
    this.sessionId = sessionId;
    this.project = project;
    this.link = makeLink(() => this.snapshot());
  }

  start(): void { this.link.start(); }

  agentStart(): void {
    if (this.running) return;
    this.running = true;
    this.send("thinking_started");
  }

  promptStart(): void {
    if (!this.running) return;
    const id = `ui:${++this.counter}`;
    this.open.push(id);
    this.send("awaiting_user", [id]);
  }

  promptEnd(): void {
    const id = this.open.pop();
    if (id) this.send("input_answered", [id]);
  }

  /** The run ended: finished, or (aborted with Escape) dropped like Codex's Interrupt. */
  settled(aborted: boolean): void {
    this.running = false;
    this.open.length = 0;
    this.send(aborted ? "session_end" : "turn_complete");
  }

  async shutdown(): Promise<void> {
    this.send("session_end");
    await this.link.stop();
  }

  /** What a (re)connected tray must learn: the prompts still waiting on you. */
  private snapshot(): WireMessage[] {
    return this.open.length === 0 ? [] : [this.message("awaiting_user", [...this.open])];
  }

  private send(event: WireMessage["event"], inputIds?: string[]): void {
    this.link.send(this.message(event, inputIds));
  }

  private message(event: WireMessage["event"], inputIds?: string[]): WireMessage {
    return { tool: "pi", event, sessionId: this.sessionId, project: this.project, ...(inputIds ? { inputIds } : {}) };
  }
}

/**
 * Registers the AgentsCursor reporter on one Pi runtime. All state lives in this call, never at module
 * level: Pi may share the loaded module between runtimes in one process (in-process subagents).
 * Only an interactive TUI session reports: in-process subagents have no UI (their approvals surface as
 * prompts in the parent's TUI), and print, json and rpc runs have no one at this screen. Nothing
 * connects until `session_start`; `session_shutdown` (quit, reload, new, resume, fork) closes the
 * connection, and the next `session_start` opens a new one.
 */
export function createPiExtension(pi: PiLike, makeLink: LinkFactory = defaultLink): void {
  let session: PiSession | undefined;

  pi.on("session_start", async (_event, ctx) => {
    if (ctx.mode !== "tui" || !ctx.hasUI) return;
    await session?.shutdown();
    session = new PiSession(ctx.sessionManager.getSessionId(), ctx.cwd, makeLink);
    session.start();
  });
  pi.on("session_shutdown", async () => {
    const ending = session;
    session = undefined;
    await ending?.shutdown();
  });
  pi.on("agent_start", () => session?.agentStart());
  pi.on("ui_prompt_start", () => session?.promptStart());
  pi.on("ui_prompt_end", () => session?.promptEnd());
  pi.on("agent_settled", (event) => session?.settled(event.aborted));
}
