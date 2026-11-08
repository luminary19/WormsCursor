import { resolve } from "node:path";
import { TrayClient, type LinkFactory, type TrayLink } from "../shared/tray-client.ts";
import { OpenCodeState } from "./state.ts";

/** V2 public events carry `data`; V1 event/property shapes are deliberately unsupported. */
export interface CursorEvent { type: string; data?: unknown; location?: { directory: string } }

/** The part of OpenCode's plugin `Context` this plugin uses (checked against the SDK in tests/contracts.ts). */
export interface CursorContext {
  location: { directory: string };
  event: { subscribe(options: { signal: AbortSignal }): AsyncIterable<CursorEvent> };
  session: { get(input: { sessionID: string }, options?: { signal?: AbortSignal }): Promise<{ parentID?: string }> };
}

const record = (value: unknown): Record<string, unknown> => value && typeof value === "object" ? value as Record<string, unknown> : {};
const text = (value: unknown): string | undefined => typeof value === "string" ? value : undefined;

const defaultLink: LinkFactory = (snapshot) => new TrayClient({ tool: "opencode" }, snapshot);

// How far up a subagent chain the root is looked for.
const MAX_DEPTH = 16;

/**
 * Reports one OpenCode location (plugin scope) to AgentsCursor over one persistent connection: its
 * permissions and forms as blocked-on-you, a root session's finished turn, and interrupts. The tray
 * owns the token, its linger and its switch. Events from another location are ignored.
 */
export class OpenCodeController {
  private readonly context: CursorContext;
  private readonly state: OpenCodeState;
  private readonly link: TrayLink;
  private readonly abort = new AbortController();
  private running: Promise<void> | undefined;
  private stopped = false;

  constructor(context: CursorContext, makeLink: LinkFactory = defaultLink) {
    this.context = context;
    this.state = new OpenCodeState(context.location.directory);
    this.link = makeLink(() => this.state.snapshot());
  }

  start(): void {
    if (this.running || this.stopped) return;
    this.link.start();
    this.running = this.listen();
  }

  async stop(): Promise<void> {
    if (this.stopped) return;
    this.stopped = true;
    this.abort.abort();
    await this.link.stop();
  }

  async receive(event: CursorEvent): Promise<void> {
    if (this.stopped || !this.ours(event)) return;
    const data = record(event.data);
    if (event.type === "location.shutdown") return this.send(this.state.shutdown());
    if (event.type === "form.created") {
      const form = record(data.form);
      const id = text(form.id), session = text(form.sessionID);
      if (!id || !session) return;
      await this.classify(session);
      return this.send(this.state.asked(`form:${id}`, session));
    }
    const session = text(data.sessionID);
    if (!session) return;
    switch (event.type) {
      case "session.created":
        this.state.created(session, text(data.parentID));
        return;
      case "session.execution.started":
        await this.classify(session);
        return this.send(this.state.started(session));
      case "session.status": {
        const status = record(data.status).type;
        await this.classify(session);
        if (status === "busy" || status === "retry") return this.send(this.state.started(session));
        if (status === "idle") return this.send(this.state.settled(session));
        return;
      }
      // OpenCode 2.2 ends a run with execution.succeeded / failed (a real `opencode run` emits no
      // status or idle event at all); session.status idle and session.idle describe the same
      // transition where they do come, and the state counts only the first.
      case "session.execution.succeeded":
      case "session.execution.failed":
      case "session.idle":
        await this.classify(session);
        return this.send(this.state.settled(session));
      case "permission.asked": {
        const id = text(data.id);
        if (!id) return;
        await this.classify(session);
        return this.send(this.state.asked(`permission:${id}`, session));
      }
      case "permission.replied": {
        const id = text(data.requestID);
        return id ? this.send(this.state.answered(`permission:${id}`)) : undefined;
      }
      case "form.replied":
      case "form.cancelled": {
        const id = text(data.id);
        return id ? this.send(this.state.answered(`form:${id}`)) : undefined;
      }
      case "session.execution.interrupted":
        await this.classify(session);
        return this.send(this.state.interrupted(session));
      case "session.deleted":
      case "session.archived":
        await this.classify(session);
        return this.send(this.state.removed(session));
      default:
        return;
    }
  }

  private ours(event: CursorEvent): boolean {
    if (!event.location) return true;
    return resolve(event.location.directory).toLowerCase() === resolve(this.context.location.directory).toLowerCase();
  }

  // Learns the hierarchy up to the root for a session that appeared before this plugin loaded. A
  // failed lookup leaves the link unknown: the session then counts as its own root for prompts, and
  // is never reported as finished.
  private async classify(session: string): Promise<void> {
    let current: string | undefined = session;
    for (let depth = 0; current && depth < MAX_DEPTH; depth++) {
      if (!this.state.isClassified(current)) {
        try {
          const info = await this.context.session.get({ sessionID: current }, { signal: this.abort.signal });
          if (this.stopped) return;
          this.state.classify(current, info.parentID);
        } catch {
          return;
        }
      }
      current = this.state.parentOf(current);
    }
  }

  private send(messages: ReturnType<OpenCodeState["snapshot"]>): void {
    if (this.stopped) return;
    for (const message of messages) this.link.send(message);
  }

  private async listen(): Promise<void> {
    // V2 server plugins subscribe to the host's in-process event bus; terminal clients reconnect
    // independently, so this subscription belongs to the plugin scope.
    try {
      for await (const event of this.context.event.subscribe({ signal: this.abort.signal })) {
        if (this.stopped) return;
        await this.receive(event);
      }
    } catch (error) {
      if (!this.stopped) console.error("AgentsCursor: OpenCode event subscription failed:", error);
    } finally {
      if (!this.stopped) await this.stop();
    }
  }
}
