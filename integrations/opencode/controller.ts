import type { CursorRuntime } from "./host.ts";
import { CursorState, type CursorSettings } from "./state.ts";
import { resolve } from "node:path";

/** V2 public events use data. V1 event/property shapes are deliberately unsupported. */
export interface CursorEvent { type: string; data?: unknown; location?: { directory: string } }
export interface CursorContext {
  location: { directory: string };
  event: { subscribe(options: { signal: AbortSignal }): AsyncIterable<CursorEvent> };
  session: { get(input: { sessionID: string }, options?: { signal?: AbortSignal }): Promise<{ parentID?: string }> };
}

const record = (value: unknown): Record<string, unknown> => value && typeof value === "object" ? value as Record<string, unknown> : {};

export class CursorController {
  private readonly state = new CursorState();
  private readonly abort = new AbortController();
  private timer: ReturnType<typeof setTimeout> | undefined;
  private running: Promise<void> | undefined;
  private stopped = false;
  private readonly context: CursorContext;
  private readonly runtime: CursorRuntime;
  private readonly settings: CursorSettings;
  private readonly now: () => number;

  constructor(
    context: CursorContext,
    runtime: CursorRuntime,
    settings: CursorSettings,
    now: () => number = Date.now,
  ) {
    this.context = context;
    this.runtime = runtime;
    this.settings = settings;
    this.now = now;
  }

  start(): void {
    if (this.running || this.stopped || !this.settings.enabled) return;
    void this.runtime.start(this.settings);
    this.running = this.listen();
  }

  async stop(): Promise<void> {
    this.stopped = true;
    this.abort.abort();
    if (this.timer) clearTimeout(this.timer);
    this.timer = undefined;
    this.state.clear();
    await this.runtime.stop();
  }

  async receive(event: CursorEvent): Promise<void> {
    if (this.stopped) return;
    if (event.location && resolve(event.location.directory).toLowerCase() !== resolve(this.context.location.directory).toLowerCase()) return;
    const data = record(event.data);
    if (event.type === "location.shutdown") {
      this.state.clear();
      this.publish();
      return;
    }
    if (event.type === "form.created") {
      const form = record(data.form);
      if (typeof form.sessionID !== "string" || typeof form.id !== "string") return;
      this.state.waiting(form.sessionID, `form:${form.id}`);
      this.publish();
      return;
    }
    const id = data.sessionID;
    if (typeof id !== "string") return;
    switch (event.type) {
      case "session.created":
        this.state.created(id, typeof data.parentID === "string" ? data.parentID : undefined);
        return;
      case "session.deleted":
      case "session.archived":
        this.state.remove(id);
        break;
      case "session.execution.interrupted":
        this.state.interrupted(id);
        break;
      case "session.execution.started":
        this.state.started(id);
        break;
      case "session.status": {
        const status = record(data.status).type;
        if (status === "busy" || status === "retry") this.state.started(id);
        else if (status === "idle") await this.settled(id);
        else return;
        break;
      }
      case "session.idle":
        await this.settled(id);
        break;
      case "permission.asked":
        if (typeof data.id !== "string") return;
        this.state.waiting(id, `permission:${data.id}`);
        break;
      case "permission.replied":
        if (typeof data.requestID !== "string") return;
        this.state.replied(id, `permission:${data.requestID}`);
        break;
      case "form.replied":
      case "form.cancelled":
        if (typeof data.id !== "string") return;
        this.state.replied(id, `form:${data.id}`);
        break;
      default: return;
    }
    if (!this.stopped) this.publish();
  }

  private async settled(id: string): Promise<void> {
    if (!this.state.isClassified(id)) {
      try {
        const info = await this.context.session.get({ sessionID: id }, { signal: this.abort.signal });
        if (this.stopped) return;
        this.state.classify(id, info.parentID);
      } catch {
        // Unknown hierarchy must never produce a false child-agent completion.
      }
    }
    if (!this.stopped) this.state.settled(id, this.now(), this.settings.lingerSeconds * 1000);
  }

  private publish(): void {
    if (this.stopped) return;
    const now = this.now();
    void this.runtime.apply(this.state.display(now), this.settings);
    if (this.timer) clearTimeout(this.timer);
    const expiry = this.state.nextExpiry(now);
    this.timer = expiry === undefined ? undefined : setTimeout(() => this.publish(), Math.max(1, expiry - now));
    this.timer?.unref();
  }

  private async listen(): Promise<void> {
    // V2 server plugins subscribe to the host's in-process event bus. Terminal
    // clients reconnect independently; this subscription belongs to plugin scope.
    try {
      for await (const event of this.context.event.subscribe({ signal: this.abort.signal })) {
        if (this.stopped) return;
        await this.receive(event);
      }
    } catch (error) {
      if (!this.stopped) console.error("OpenCode cursor event subscription failed:", error);
    } finally {
      if (!this.stopped) await this.stop();
    }
  }
}
