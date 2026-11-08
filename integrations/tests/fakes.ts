import type { LinkFactory, TrayLink } from "../shared/tray-client.ts";
import type { WireMessage } from "../shared/wire.ts";

/** A tray link that records what a controller sends, instead of writing to a pipe. */
export class RecordingLink implements TrayLink {
  readonly sent: WireMessage[] = [];
  readonly snapshot: () => WireMessage[];
  starts = 0;
  stops = 0;

  constructor(snapshot: () => WireMessage[]) {
    this.snapshot = snapshot;
  }

  start(): void { this.starts++; }
  send(message: WireMessage): void { this.sent.push(message); }
  async stop(): Promise<void> { this.stops++; }
}

/** A link factory that keeps every link it made. */
export function recorder(): { links: RecordingLink[]; factory: LinkFactory } {
  const links: RecordingLink[] = [];
  return { links, factory: (snapshot) => { const link = new RecordingLink(snapshot); links.push(link); return link; } };
}

/** "event:session[:id,id]" for each message, for compact assertions. */
export function show(messages: readonly WireMessage[]): string[] {
  return messages.map((m) => [m.event, m.sessionId ?? "", ...(m.inputIds ? [m.inputIds.join(",")] : [])].join(":"));
}
