import { createConnection, type Socket } from "node:net";
import { defaultPipeName, encode, pipePath, type PluginTool, type WireMessage } from "./wire.ts";

/** A timer handle that can be unref()ed, as Node's are. */
export interface TimerHandle { unref?(): unknown }

export interface TrayClientOptions {
  tool: PluginTool;
  /** Tests use a private pipe; the default is the tray's own. */
  pipeName?: string;
  /** Opens a connection to a pipe path (tests substitute their own). */
  connect?: (path: string) => Socket;
  setTimer?: (callback: () => void, ms: number) => TimerHandle;
  clearTimer?: (timer: TimerHandle) => void;
  /** 0..1, for the reconnect jitter. */
  random?: () => number;
}

/** A connection to the tray, as a plugin's controller uses it (tests substitute a recorder). */
export interface TrayLink {
  start(): void;
  send(message: WireMessage): void;
  stop(): Promise<void>;
}

/** Makes a host's link; `snapshot` is what to replay on every (re)connect. */
export type LinkFactory = (snapshot: () => WireMessage[]) => TrayLink;

const clearNodeTimer = (timer: TimerHandle): void => clearTimeout(timer as ReturnType<typeof setTimeout>);

const FIRST_RETRY_MS = 1_000;
const MAX_RETRY_MS = 30_000;
const JITTER = 0.2;

/** The delay before reconnect attempt `attempt` (0-based): 1, 2, 4, 8, 16, then 30 s, +/-20 %. */
export function retryDelay(attempt: number, random: () => number): number {
  const base = Math.min(MAX_RETRY_MS, FIRST_RETRY_MS * 2 ** attempt);
  return Math.round(base * (1 - JITTER + 2 * JITTER * random()));
}

/**
 * One plugin host's persistent connection to the AgentsCursor tray.
 *
 * It never starts the tray: when the pipe isn't there (the tray isn't running), it retries with
 * backoff and stays quiet, like the Claude Code and Codex hooks. On every connect it writes `attach`
 * and then `snapshot()`, the host's current blocked state, so a tray that starts (or restarts) late
 * still shows an agent that is waiting on you. Messages sent while disconnected are dropped, never
 * queued: the snapshot is what carries state across a reconnect. Nothing (no socket, no timer) exists
 * before `start()`, and neither the socket nor its timers keep the host process alive.
 */
export class TrayClient implements TrayLink {
  private readonly options: TrayClientOptions;
  private readonly snapshot: () => WireMessage[];
  private socket: Socket | undefined;
  private timer: TimerHandle | undefined;
  private attempt = 0;
  private started = false;
  private stopped = false;
  private isConnected = false;
  private stopping: Promise<void> | undefined;

  constructor(options: TrayClientOptions, snapshot: () => WireMessage[]) {
    this.options = options;
    this.snapshot = snapshot;
  }

  get connected(): boolean { return this.isConnected; }

  /** Begins connecting. Idempotent. */
  start(): void {
    if (this.started || this.stopped) return;
    this.started = true;
    this.open();
  }

  /** Writes `message` when connected; otherwise drops it (the snapshot replays state on reconnect). */
  send(message: WireMessage): void {
    if (this.isConnected && this.socket) this.socket.write(encode(message));
  }

  /** Cancels any retry and closes the connection, resolving once it has closed. Idempotent. */
  stop(): Promise<void> {
    this.stopping ??= this.close();
    return this.stopping;
  }

  private async close(): Promise<void> {
    this.stopped = true;
    if (this.timer) (this.options.clearTimer ?? clearNodeTimer)(this.timer);
    this.timer = undefined;
    const socket = this.socket;
    this.socket = undefined;
    this.isConnected = false;
    if (!socket || socket.destroyed) return;
    await new Promise<void>((resolve) => {
      // Held open until it has closed, so an awaited stop settles; a peer that never acknowledges the
      // end can't hold the host's shutdown for more than half a second.
      socket.ref();
      const force = setTimeout(() => socket.destroy(), 500);
      socket.once("close", () => {
        clearTimeout(force);
        resolve();
      });
      socket.end();
    });
  }

  private open(): void {
    if (this.stopped) return;
    const connect = this.options.connect ?? createConnection;
    const socket = connect(pipePath(this.options.pipeName ?? defaultPipeName()));
    this.socket = socket;
    socket.unref();
    socket.on("connect", () => {
      if (this.socket !== socket) return;
      this.isConnected = true;
      this.attempt = 0;
      socket.write(encode({ tool: this.options.tool, event: "attach" }));
      for (const message of this.snapshot()) socket.write(encode(message));
    });
    // A missing pipe, a refused write, a tray that exits: 'close' follows, which reconnects.
    socket.on("error", () => {});
    socket.on("close", () => {
      if (this.socket !== socket) return;
      this.socket = undefined;
      this.isConnected = false;
      this.schedule();
    });
  }

  private schedule(): void {
    if (this.stopped) return;
    const delay = retryDelay(this.attempt++, this.options.random ?? Math.random);
    const setTimer = this.options.setTimer ?? setTimeout;
    this.timer = setTimer(() => {
      this.timer = undefined;
      this.open();
    }, delay);
    this.timer.unref?.();
  }
}
