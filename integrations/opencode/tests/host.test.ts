import assert from "node:assert/strict";
import { once } from "node:events";
import { createServer, type Socket } from "node:net";
import test from "node:test";
import { mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { materializeNativeHelper, NativeCursorRuntime } from "../host.ts";
import { DEFAULT_SETTINGS } from "../state.ts";

const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
async function until(check: () => boolean, timeout = 5000): Promise<void> {
  const deadline = Date.now() + timeout;
  while (!check()) { if (Date.now() > deadline) throw new Error("test condition timed out"); await delay(10); }
}

test("one persistent connection coalesces bursts and replays state after pipe failure", { skip: process.platform !== "win32" }, async () => {
  const pipeName = `OpenCodeCursorTest-${process.pid}-${Date.now()}`;
  const sockets: Socket[] = [];
  const lines: string[] = [];
  const server = createServer((socket) => {
    sockets.push(socket);
    let pending = "";
    socket.on("data", (chunk) => {
      pending += String(chunk);
      for (;;) { const end = pending.indexOf("\n"); if (end < 0) break; lines.push(pending.slice(0, end)); pending = pending.slice(end + 1); }
    });
  });
  server.listen(`\\\\.\\pipe\\${pipeName}`);
  await once(server, "listening");
  const runtime = new NativeCursorRuntime({ pipeName, materialize: async () => { throw new Error("must use existing helper"); } });
  try {
    await runtime.start({ ...DEFAULT_SETTINGS });
    const pending = Array.from({ length: 100 }, (_, index) => runtime.apply(index % 2 ? "waiting" : "completion", { ...DEFAULT_SETTINGS }));
    await Promise.all(pending);
    await until(() => lines.at(-1)?.includes('"waiting"') ?? false);
    assert.equal(sockets.length, 1);
    assert.ok(lines.length < 10, `coalesced ${lines.length} writes`);
    sockets[0].destroy();
    await until(() => sockets.length === 2 && lines.length >= 3);
    assert.equal(JSON.parse(lines.at(-1)!).display, "waiting");
    await runtime.stop();
    await until(() => sockets.every((socket) => socket.destroyed));
  } finally {
    await runtime.stop();
    for (const socket of sockets) socket.destroy();
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }
});

test("stopping during helper materialization cannot launch a late helper", { skip: process.platform !== "win32" }, async () => {
  let release!: () => void;
  let building = false;
  const runtime = new NativeCursorRuntime({
    pipeName: `OpenCodeCursorTest-stop-${process.pid}-${Date.now()}`,
    materialize: async () => {
      building = true;
      await new Promise<void>((resolve) => { release = resolve; });
      return { hash: "test", directory: "missing", executable: "C:\\definitely-missing-cursor-helper.exe" };
    },
  });
  const starting = runtime.start({ ...DEFAULT_SETTINGS });
  await until(() => building);
  await runtime.stop();
  release();
  await starting;
  assert.equal(runtime.getStatus(), "Disconnected");
});

test("a failed helper build is not re-run until the helper sources change", async () => {
  const root = await mkdtemp(join(tmpdir(), "wormscursor-materialize-"));
  const native = join(root, "src", "WormsCursor.OpenCodeOverlay");
  await mkdir(native, { recursive: true });
  await writeFile(join(root, "global.json"), "{}");
  await writeFile(join(native, "WormsCursor.OpenCodeOverlay.csproj"), "<Project />");
  await writeFile(join(native, "Program.cs"), "// v1");
  let builds = 0;
  const build = async () => { builds++; throw new Error("compile error"); };
  const errors: unknown[] = [];
  const consoleError = console.error;
  console.error = (...args: unknown[]) => { errors.push(args); };
  try {
    await assert.rejects(materializeNativeHelper({ root, build }), /compile error/);
    await assert.rejects(materializeNativeHelper({ root, build }), /compile error/);
    assert.equal(builds, 1);
    assert.equal(errors.length, 1);
    await writeFile(join(native, "Program.cs"), "// v2");
    await assert.rejects(materializeNativeHelper({ root, build }), /compile error/);
    assert.equal(builds, 2);
  } finally {
    console.error = consoleError;
    await rm(root, { recursive: true, force: true });
  }
});

test("editing shared Core source linked into the helper rebuilds it", async () => {
  const root = await mkdtemp(join(tmpdir(), "wormscursor-materialize-"));
  const native = join(root, "src", "WormsCursor.OpenCodeOverlay");
  const core = join(root, "src", "WormsCursor.Core");
  await mkdir(native, { recursive: true });
  await mkdir(core, { recursive: true });
  await writeFile(join(root, "global.json"), "{}");
  await writeFile(join(native, "WormsCursor.OpenCodeOverlay.csproj"),
    '<Project><ItemGroup><Compile Include="../WormsCursor.Core/Shared.cs" Link="Core/Shared.cs" /></ItemGroup></Project>');
  await writeFile(join(native, "Program.cs"), "// helper");
  await writeFile(join(core, "Shared.cs"), "// v1");
  let builds = 0;
  const build = async (_project: string, directory: string) => {
    builds++;
    await writeFile(join(directory, "OpenCodeCursorOverlay.exe"), "");
  };
  try {
    const first = await materializeNativeHelper({ root, build });
    assert.equal((await materializeNativeHelper({ root, build })).hash, first.hash);
    assert.equal(builds, 1);
    await writeFile(join(core, "Shared.cs"), "// v2");
    const second = await materializeNativeHelper({ root, build });
    assert.notEqual(second.hash, first.hash);
    assert.equal(builds, 2);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
