import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { access, mkdir, mkdtemp, readFile, readdir, rename, rm } from "node:fs/promises";
import { createConnection, type Socket } from "node:net";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type { CursorSettings, DisplayMode } from "./state.ts";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const DEFAULT_PIPE = "OpenCodeCursorOverlay-v1";

export interface CursorRuntime {
  start(settings: CursorSettings): Promise<void>;
  apply(display: DisplayMode, settings: CursorSettings): Promise<void>;
  stop(): Promise<void>;
}

export interface NativeMaterialization { hash: string; directory: string; executable: string }

async function exists(path: string): Promise<boolean> {
  try { await access(path); return true; } catch { return false; }
}

async function sourceFiles(directory: string, relative = ""): Promise<string[]> {
  const result: string[] = [];
  for (const entry of await readdir(join(directory, relative), { withFileTypes: true })) {
    if (["bin", "obj", "runtime"].includes(entry.name)) continue;
    const file = join(relative, entry.name);
    if (entry.isDirectory()) result.push(...await sourceFiles(directory, file));
    else if (entry.isFile()) result.push(file);
  }
  return result.sort();
}

function build(project: string, directory: string): Promise<void> {
  return new Promise((resolveBuild, reject) => {
    const child = spawn("dotnet", ["build", project, "-c", "Release", "--nologo", "-o", directory,
      `-p:BaseIntermediateOutputPath=${join(directory, "obj")}\\`], {
      cwd: repository, windowsHide: true, shell: false, stdio: "ignore",
    });
    let settled = false;
    const finish = (error?: Error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      error ? reject(error) : resolveBuild();
    };
    const timer = setTimeout(() => { child.kill(); finish(new Error("OpenCode cursor helper build timed out")); }, 60000);
    child.once("error", finish);
    child.once("exit", (code) => finish(code === 0 ? undefined : new Error(`OpenCode cursor helper build exited ${code}`)));
  });
}

/** Source-addressed builds never replace an executable used by another session. */
export async function materializeNativeHelper(): Promise<NativeMaterialization> {
  const native = join(repository, "src", "WormsCursor.OpenCodeOverlay");
  const digest = createHash("sha256");
  digest.update(await readFile(join(repository, "global.json")));
  for (const name of await sourceFiles(native)) {
    digest.update(name.replaceAll("\\", "/")).update("\0").update(await readFile(join(native, name))).update("\0");
  }
  const hash = digest.digest("hex").slice(0, 24);
  const cache = join(repository, "artifacts", "opencode");
  const directory = join(cache, hash);
  const executable = join(directory, "OpenCodeCursorOverlay.exe");
  if (await exists(executable)) return { hash, directory, executable };
  await mkdir(cache, { recursive: true });
  const staging = await mkdtemp(join(cache, `.${hash}-`));
  try {
    await build(join(native, "WormsCursor.OpenCodeOverlay.csproj"), staging);
    if (!await exists(join(staging, "OpenCodeCursorOverlay.exe"))) throw new Error("OpenCode cursor build produced no executable");
    await rm(join(staging, "obj"), { recursive: true, force: true });
    try { await rename(staging, directory); }
    catch (error) { if (!await exists(executable)) throw error; }
  } finally {
    await rm(staging, { recursive: true, force: true, maxRetries: 2 }).catch(() => {});
  }
  return { hash, directory, executable };
}

function connect(name: string, signal: AbortSignal): Promise<Socket | undefined> {
  if (signal.aborted) return Promise.resolve(undefined);
  return new Promise((resolveConnection) => {
    const socket = createConnection(`\\\\.\\pipe\\${name}`);
    let settled = false;
    const finish = (connected: boolean) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      signal.removeEventListener("abort", aborted);
      if (!connected) socket.destroy();
      resolveConnection(connected ? socket : undefined);
    };
    const aborted = () => finish(false);
    const timer = setTimeout(aborted, 150);
    signal.addEventListener("abort", aborted, { once: true });
    socket.once("connect", () => finish(true));
    socket.on("error", aborted);
  });
}

function write(socket: Socket, line: string): Promise<void> {
  return new Promise((resolveWrite, reject) => {
    let settled = false;
    const finish = (error?: Error | null) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      socket.removeListener("close", closed);
      error ? reject(error) : resolveWrite();
    };
    const closed = () => finish(new Error("OpenCode cursor pipe closed"));
    const timer = setTimeout(() => { socket.destroy(); closed(); }, 1000);
    socket.once("close", closed);
    try { socket.write(line, finish); } catch (error) { finish(error as Error); }
  });
}

export interface NativeRuntimeOptions {
  pipeName?: string;
  helperArguments?: readonly string[];
  materialize?: () => Promise<NativeMaterialization>;
}

/** One socket per plugin instance, latest-state coalescing, no event subprocesses. */
export class NativeCursorRuntime implements CursorRuntime {
  private readonly pipeName: string;
  private readonly helperArguments: readonly string[];
  private readonly materialize: () => Promise<NativeMaterialization>;
  private abort = new AbortController();
  private active = false;
  private desired = JSON.stringify({ command: "state", display: "hidden", size: 64 }) + "\n";
  private sent: string | undefined;
  private socket: Socket | undefined;
  private worker: Promise<void> | undefined;
  private retry: ReturnType<typeof setTimeout> | undefined;
  private retryDelay = 250;
  private status = "Disconnected";

  constructor(options: NativeRuntimeOptions = {}) {
    this.pipeName = options.pipeName ?? DEFAULT_PIPE;
    this.helperArguments = options.helperArguments ?? [];
    this.materialize = options.materialize ?? materializeNativeHelper;
  }

  getStatus(): string { return this.status; }

  start(settings: CursorSettings): Promise<void> { return this.apply("hidden", settings); }

  apply(display: DisplayMode, settings: CursorSettings): Promise<void> {
    if (!settings.enabled) return this.stop();
    if (!this.active) { this.active = true; this.abort = new AbortController(); }
    this.desired = JSON.stringify({ command: "state", display, size: settings.tokenSize }) + "\n";
    return this.flush();
  }

  async stop(): Promise<void> {
    this.active = false;
    this.abort.abort();
    if (this.retry) clearTimeout(this.retry);
    this.retry = undefined;
    const socket = this.socket;
    this.socket = undefined;
    this.sent = undefined;
    this.status = "Disconnected";
    if (!socket || socket.destroyed) return;
    await new Promise<void>((resolveStop) => {
      const timer = setTimeout(() => socket.destroy(), 1000);
      socket.once("close", () => { clearTimeout(timer); resolveStop(); });
      socket.end();
    });
  }

  private flush(): Promise<void> {
    if (!this.active || this.retry) return Promise.resolve();
    if (this.worker) return this.worker;
    const signal = this.abort.signal;
    const task = this.drain(signal).catch((error: unknown) => {
      if (!signal.aborted) {
        this.status = `Unavailable: ${error instanceof Error ? error.message : String(error)}`;
        this.scheduleRetry();
      }
    }).finally(() => {
      if (this.worker === task) this.worker = undefined;
      if (this.active && !this.retry && this.sent !== this.desired) void this.flush();
    });
    this.worker = task;
    return task;
  }

  private async drain(signal: AbortSignal): Promise<void> {
    if (!this.socket || this.socket.destroyed) await this.ensureConnected(signal);
    while (!signal.aborted && this.socket && this.sent !== this.desired) {
      const desired = this.desired;
      await write(this.socket, desired);
      if (signal.aborted) return;
      this.sent = desired;
    }
  }

  private attach(socket: Socket, signal: AbortSignal): void {
    if (signal.aborted) { socket.destroy(); return; }
    this.socket = socket;
    this.sent = undefined;
    this.retryDelay = 250;
    this.status = "Connected";
    socket.unref();
    socket.once("close", () => {
      if (this.socket !== socket) return;
      this.socket = undefined;
      this.sent = undefined;
      this.status = "Disconnected";
      this.scheduleRetry();
    });
  }

  private async ensureConnected(signal: AbortSignal): Promise<void> {
    const existing = await connect(this.pipeName, signal);
    if (existing) { this.attach(existing, signal); return; }
    if (signal.aborted) return;
    const helper = await this.materialize();
    if (signal.aborted) return;
    const child = spawn(helper.executable, [...this.helperArguments], {
      detached: true, windowsHide: true, shell: false, stdio: "ignore",
    });
    child.once("error", () => {});
    child.unref();
    const deadline = Date.now() + 5000;
    while (!signal.aborted && Date.now() < deadline) {
      const socket = await connect(this.pipeName, signal);
      if (socket) { this.attach(socket, signal); return; }
      if (!signal.aborted) await new Promise<void>((resolveWait) => setTimeout(resolveWait, 50));
    }
    if (!signal.aborted) throw new Error("native helper did not accept named-pipe connections");
  }

  private scheduleRetry(): void {
    if (!this.active || this.retry) return;
    this.retry = setTimeout(() => {
      this.retry = undefined;
      void this.flush();
    }, this.retryDelay);
    this.retry.unref();
    this.retryDelay = Math.min(5000, this.retryDelay * 2);
  }
}
