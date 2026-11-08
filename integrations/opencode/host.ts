import { spawn, type ChildProcess } from "node:child_process";
import { createHash } from "node:crypto";
import { access, mkdir, mkdtemp, readFile, readdir, rename, rm } from "node:fs/promises";
import { createConnection, type Socket } from "node:net";
import { dirname, join, relative, resolve } from "node:path";
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

export interface MaterializeOptions {
  /** Repository root holding global.json and src/AgentsCursor.OpenCodeOverlay (default: this checkout). */
  root?: string;
  /** Builds the helper project into a directory (default: `dotnet build`). */
  build?: (project: string, directory: string) => Promise<void>;
  /** Current time in milliseconds (default: `Date.now`). */
  now?: () => number;
}

// A failed build is remembered per source hash for a cooldown, so connect retries (every few seconds)
// re-attempt only the connect/spawn and never re-run `dotnet build` each time. A compile error fails
// the same way until the sources change, which is a new hash and builds at once; a transient failure
// (a timeout on a busy machine, a locked file, an offline restore) is retried once the cooldown ends.
// Keyed by the content-addressed output directory.
export const BUILD_RETRY_COOLDOWN_MS = 5 * 60_000;
const failedBuilds = new Map<string, { error: Error; at: number }>();

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

// Shared source the helper compiles in from outside its own directory (`<Compile Include="../...">`,
// e.g. AgentsCursor.Core's settings and foreground watcher). It is build input like the helper's own
// files, so it must be part of the content address, or an edit to it would reuse a stale build.
async function linkedSources(project: string): Promise<string[]> {
  const xml = await readFile(project, "utf8");
  return [...xml.matchAll(/<Compile\s+Include="([^"]+)"/g)]
    .map((match) => resolve(dirname(project), match[1]))
    .sort();
}

// The dotnet driver hands the work to MSBuild worker processes, so killing only its PID would leave
// them writing into the staging directory that is about to be deleted. Resolves once taskkill exits.
function killTree(child: ChildProcess): Promise<void> {
  return new Promise((resolveKill) => {
    if (child.pid === undefined) { resolveKill(); return; }
    const killer = spawn("taskkill", ["/T", "/F", "/PID", String(child.pid)], { windowsHide: true, shell: false, stdio: "ignore" });
    killer.once("error", () => { child.kill(); resolveKill(); });
    killer.once("exit", () => resolveKill());
  });
}

// -nodeReuse:false keeps no MSBuild nodes alive after the build. The project's own directory is the
// working directory, so the SDK still resolves the repository's global.json.
function dotnetBuild(project: string, directory: string): Promise<void> {
  return new Promise((resolveBuild, reject) => {
    const child = spawn("dotnet", ["build", project, "-c", "Release", "--nologo", "-nodeReuse:false", "-o", directory,
      `-p:BaseIntermediateOutputPath=${join(directory, "obj")}\\`], {
      cwd: dirname(project), windowsHide: true, shell: false, stdio: "ignore",
    });
    let settled = false;
    const finish = (error?: Error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      error ? reject(error) : resolveBuild();
    };
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true; // the killed driver's own exit must not be reported as an ordinary failure
      void killTree(child).then(() => finish(new Error("OpenCode cursor helper build timed out")));
    }, 60000);
    child.once("error", finish);
    child.once("exit", (code) => {
      if (!timedOut) finish(code === 0 ? undefined : new Error(`OpenCode cursor helper build exited ${code}`));
    });
  });
}

function rememberFailure(directory: string, hash: string, error: unknown, at: number): Error {
  const failure = error instanceof Error ? error : new Error(String(error));
  failedBuilds.set(directory, { error: failure, at });
  console.error(`AgentsCursor: OpenCode cursor helper build ${hash} failed; not rebuilding for `
    + `${BUILD_RETRY_COOLDOWN_MS / 60_000} minutes or until its sources change: ${failure.message}`);
  return failure;
}

/** Source-addressed builds never replace an executable used by another session. */
export async function materializeNativeHelper(options: MaterializeOptions = {}): Promise<NativeMaterialization> {
  const root = options.root ?? repository;
  const build = options.build ?? dotnetBuild;
  const now = options.now ?? Date.now;
  const native = join(root, "src", "AgentsCursor.OpenCodeOverlay");
  const project = join(native, "AgentsCursor.OpenCodeOverlay.csproj");
  const digest = createHash("sha256");
  digest.update(await readFile(join(root, "global.json")));
  for (const name of await sourceFiles(native)) {
    digest.update(name.replaceAll("\\", "/")).update("\0").update(await readFile(join(native, name))).update("\0");
  }
  for (const file of await linkedSources(project)) {
    digest.update(`linked:${relative(root, file).replaceAll("\\", "/")}`).update("\0").update(await readFile(file)).update("\0");
  }
  const hash = digest.digest("hex").slice(0, 24);
  const cache = join(root, "artifacts", "opencode");
  const directory = join(cache, hash);
  const executable = join(directory, "OpenCodeCursorOverlay.exe");
  if (await exists(executable)) return { hash, directory, executable };
  const failed = failedBuilds.get(directory);
  if (failed && now() - failed.at < BUILD_RETRY_COOLDOWN_MS) throw failed.error;
  await mkdir(cache, { recursive: true });
  const staging = await mkdtemp(join(cache, `.${hash}-`));
  try {
    try {
      await build(project, staging);
      if (!await exists(join(staging, "OpenCodeCursorOverlay.exe"))) throw new Error("OpenCode cursor build produced no executable");
    } catch (error) {
      throw rememberFailure(directory, hash, error, now());
    }
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
