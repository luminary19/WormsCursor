import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import { createServer, type Server, type Socket } from "node:net";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { retryDelay, TrayClient, type TimerHandle } from "../shared/tray-client.ts";
import { pipePath, type WireMessage } from "../shared/wire.ts";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const blocked: WireMessage = { tool: "pi", event: "awaiting_user", sessionId: "s", inputIds: ["ui:1"] };

/** A stand-in tray: a pipe server that collects each connection's lines. */
class FakeTray {
  readonly name = `AgentsCursorTest.${process.pid}.${Math.random().toString(36).slice(2)}`;
  readonly connections: { socket: Socket; lines: string[] }[] = [];
  private server: Server | undefined;
  private waiters: (() => void)[] = [];

  async listen(): Promise<void> {
    this.server = createServer((socket) => {
      const connection = { socket, lines: [] as string[] };
      this.connections.push(connection);
      let buffer = "";
      socket.on("data", (chunk) => {
        buffer += chunk.toString("utf8");
        let newline;
        while ((newline = buffer.indexOf("\n")) >= 0) {
          connection.lines.push(buffer.slice(0, newline));
          buffer = buffer.slice(newline + 1);
        }
        this.notify();
      });
      socket.on("end", () => this.notify());
      socket.on("error", () => {});
      this.notify();
    });
    await new Promise<void>((resolve) => this.server!.listen(pipePath(this.name), resolve));
  }

  async close(): Promise<void> {
    for (const { socket } of this.connections) socket.destroy();
    await new Promise<void>((resolve) => this.server ? this.server.close(() => resolve()) : resolve());
  }

  /** Waits until `ready()` holds (re-checked on every server event). */
  async until(ready: () => boolean, ms = 3_000): Promise<void> {
    const deadline = Date.now() + ms;
    while (!ready()) {
      if (Date.now() > deadline) throw new Error("timed out waiting for the fake tray");
      await new Promise<void>((resolve) => {
        this.waiters.push(resolve);
        setTimeout(resolve, 50);
      });
    }
  }

  private notify(): void {
    const waiters = this.waiters;
    this.waiters = [];
    for (const wake of waiters) wake();
  }
}

/** Timers the test fires by hand. */
class ManualTimers {
  readonly pending: { callback: () => void; ms: number; cleared: boolean; unref(): unknown }[] = [];
  readonly set = (callback: () => void, ms: number): TimerHandle => {
    const timer = { callback, ms, cleared: false, unref: () => timer };
    this.pending.push(timer);
    return timer;
  };
  readonly clear = (timer: TimerHandle): void => { (timer as { cleared: boolean }).cleared = true; };
  fire(): void {
    const timer = this.pending.at(-1);
    assert.ok(timer && !timer.cleared, "a live retry timer");
    timer.callback();
  }
}

test("on connect it attaches first, then replays the snapshot", async () => {
  const tray = new FakeTray();
  await tray.listen();
  const client = new TrayClient({ tool: "pi", pipeName: tray.name }, () => [blocked]);
  client.start();
  await tray.until(() => tray.connections[0]?.lines.length === 2);
  assert.deepEqual(tray.connections[0].lines.map((l) => JSON.parse(l)), [{ tool: "pi", event: "attach" }, blocked]);
  assert.equal(client.connected, true);
  client.send({ tool: "pi", event: "turn_complete", sessionId: "s" });
  await tray.until(() => tray.connections[0].lines.length === 3);
  await client.stop();
  await tray.close();
});

test("after the tray goes away it reconnects with backoff and replays only the snapshot", async () => {
  const tray = new FakeTray();
  await tray.listen();
  const timers = new ManualTimers();
  let state: WireMessage[] = [];
  const client = new TrayClient({ tool: "opencode", pipeName: tray.name, setTimer: timers.set, clearTimer: timers.clear, random: () => 0.5 }, () => state);
  client.start();
  await tray.until(() => tray.connections.length === 1 && tray.connections[0].lines.length === 1);

  tray.connections[0].socket.destroy(); // the tray exits
  await new Promise((resolve) => setTimeout(resolve, 100));
  assert.equal(client.connected, false);
  assert.equal(timers.pending.length, 1);
  assert.equal(timers.pending[0].ms, 1_000);
  client.send({ tool: "opencode", event: "turn_complete", sessionId: "x" }); // dropped, never queued

  state = [{ tool: "opencode", event: "awaiting_user", sessionId: "root", inputIds: ["form:q"] }];
  timers.fire();
  await tray.until(() => tray.connections.length === 2 && tray.connections[1].lines.length === 2);
  assert.deepEqual(tray.connections[1].lines.map((l) => JSON.parse(l).event), ["attach", "awaiting_user"]);
  await client.stop();
  await tray.close();
});

test("with no tray running it never launches one: it retries quietly, and stop cancels the retry", async () => {
  const timers = new ManualTimers();
  const client = new TrayClient({ tool: "pi", pipeName: `AgentsCursorTest.missing.${process.pid}`, setTimer: timers.set, clearTimer: timers.clear, random: () => 0.5 }, () => []);
  client.start();
  client.start(); // idempotent
  await new Promise((resolve) => setTimeout(resolve, 100));
  assert.equal(timers.pending.length, 1);
  timers.fire(); // the second attempt fails too: the next delay doubles
  await new Promise((resolve) => setTimeout(resolve, 100));
  assert.deepEqual(timers.pending.map((t) => t.ms), [1_000, 2_000]);
  await client.stop();
  assert.equal(timers.pending.at(-1)!.cleared, true);
  await client.stop(); // idempotent
});

test("nothing exists before start(): no socket, no timer", () => {
  let connects = 0, timers = 0;
  const client = new TrayClient({
    tool: "pi",
    connect: () => { connects++; throw new Error("unexpected"); },
    setTimer: () => { timers++; return {}; },
  }, () => []);
  assert.equal(client.connected, false);
  client.send(blocked);
  assert.equal(connects, 0);
  assert.equal(timers, 0);
});

test("stop() ends an open connection and resolves once it has closed", async () => {
  const tray = new FakeTray();
  await tray.listen();
  const client = new TrayClient({ tool: "pi", pipeName: tray.name }, () => []);
  client.start();
  await tray.until(() => tray.connections[0]?.lines.length === 1);
  let ended = false;
  tray.connections[0].socket.on("end", () => { ended = true; });
  await client.stop();
  await tray.until(() => ended);
  assert.equal(client.connected, false);
  await tray.close();
});

test("the reconnect delay doubles from 1 s to a 30 s cap, with +/-20 % jitter", () => {
  assert.deepEqual([0, 1, 2, 3, 4, 5, 9].map((n) => retryDelay(n, () => 0.5)), [1_000, 2_000, 4_000, 8_000, 16_000, 30_000, 30_000]);
  assert.equal(retryDelay(0, () => 0), 800);
  assert.equal(retryDelay(5, () => 1), 36_000);
});

test("no plugin code can start a process", () => {
  for (const dir of ["shared", "pi", "opencode"]) {
    for (const file of readdirSync(join(root, dir)).filter((f) => f.endsWith(".ts"))) {
      const source = readFileSync(join(root, dir, file), "utf8");
      assert.ok(!/child_process|\bspawn\(|\bexec(File)?\(/.test(source), `${dir}/${file} must not start processes`);
    }
  }
});
