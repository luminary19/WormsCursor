import assert from "node:assert/strict";
import test from "node:test";
import { CursorController, type CursorContext } from "../controller.ts";
import type { CursorRuntime } from "../host.ts";
import { DEFAULT_SETTINGS, type DisplayMode } from "../state.ts";

class Runtime implements CursorRuntime {
  displays: DisplayMode[] = [];
  starts = 0;
  stops = 0;
  async start() { this.starts++; }
  async apply(display: DisplayMode) { this.displays.push(display); }
  async stop() { this.stops++; }
}

function context(get: CursorContext["session"]["get"] = async () => ({})): CursorContext {
  return { location: { directory: "C:\\test" }, session: { get }, event: { async *subscribe({ signal }) {
    await new Promise<void>((resolve) => { signal.addEventListener("abort", () => resolve(), { once: true }); });
  } } };
}

test("V2 forms and permissions aggregate across sessions and interrupt clears waits", async () => {
  const runtime = new Runtime();
  const controller = new CursorController(context(), runtime, { ...DEFAULT_SETTINGS });
  await controller.receive({ type: "form.created", data: { form: { sessionID: "a", id: "q" } } });
  await controller.receive({ type: "permission.asked", data: { sessionID: "b", id: "p" } });
  await controller.receive({ type: "form.replied", data: { sessionID: "a", id: "q" } });
  assert.equal(runtime.displays.at(-1), "waiting");
  await controller.receive({ type: "session.execution.interrupted", data: { sessionID: "b" } });
  assert.equal(runtime.displays.at(-1), "hidden");
  await controller.stop();
});

test("existing session hierarchy is checked once and child completions are suppressed", async () => {
  const runtime = new Runtime();
  let lookups = 0;
  const controller = new CursorController(context(async () => { lookups++; return { parentID: "parent" }; }), runtime, { ...DEFAULT_SETTINGS });
  await controller.receive({ type: "session.status", data: { sessionID: "child", status: { type: "busy" } } });
  await controller.receive({ type: "session.status", data: { sessionID: "child", status: { type: "idle" } } });
  await controller.receive({ type: "session.idle", data: { sessionID: "child" } });
  assert.equal(lookups, 1);
  assert.ok(runtime.displays.every((display) => display === "hidden"));
  await controller.stop();
});

test("cleanup during metadata lookup cannot resurrect runtime state", async () => {
  const runtime = new Runtime();
  let release!: (value: {}) => void;
  const controller = new CursorController(context(() => new Promise((resolve) => { release = resolve; })), runtime, { ...DEFAULT_SETTINGS });
  await controller.receive({ type: "session.execution.started", data: { sessionID: "root" } });
  const finishing = controller.receive({ type: "session.idle", data: { sessionID: "root" } });
  await controller.stop();
  release({});
  await finishing;
  assert.deepEqual(runtime.displays, ["hidden"]);
  assert.equal(runtime.stops, 1);
});

test("root completion timer expires and plugin cleanup aborts the subscription", async () => {
  const runtime = new Runtime();
  const controller = new CursorController(context(), runtime, { ...DEFAULT_SETTINGS, lingerSeconds: 0.01 });
  controller.start();
  await controller.receive({ type: "session.created", data: { sessionID: "root" } });
  await controller.receive({ type: "session.execution.started", data: { sessionID: "root" } });
  await controller.receive({ type: "session.idle", data: { sessionID: "root" } });
  assert.equal(runtime.displays.at(-1), "completion");
  await new Promise((resolve) => setTimeout(resolve, 30));
  assert.equal(runtime.displays.at(-1), "hidden");
  await controller.stop();
  assert.equal(runtime.starts, 1);
  assert.equal(runtime.stops, 1);
});

test("disabled plugin does not open a native runtime", async () => {
  const runtime = new Runtime();
  const controller = new CursorController(context(), runtime, { ...DEFAULT_SETTINGS, enabled: false });
  controller.start();
  assert.equal(runtime.starts, 0);
  await controller.stop();
});

test("another location cannot clear or add this location's attention state", async () => {
  const runtime = new Runtime();
  const controller = new CursorController(context(), runtime, { ...DEFAULT_SETTINGS });
  await controller.receive({ type: "form.created", location: { directory: "C:/TEST" }, data: { form: { sessionID: "a", id: "q" } } });
  await controller.receive({ type: "location.shutdown", location: { directory: "C:/other" } });
  assert.equal(runtime.displays.at(-1), "waiting");
  await controller.receive({ type: "form.replied", location: { directory: "C:/test" }, data: { sessionID: "a", id: "q" } });
  await controller.receive({ type: "permission.asked", location: { directory: "C:/other" }, data: { sessionID: "b", id: "p" } });
  assert.equal(runtime.displays.at(-1), "hidden");
  await controller.stop();
});

test("a root session already running when the plugin loads notifies on its first idle", async () => {
  const runtime = new Runtime();
  const controller = new CursorController(context(), runtime, { ...DEFAULT_SETTINGS });
  await controller.receive({ type: "session.idle", data: { sessionID: "already-running" } });
  assert.equal(runtime.displays.at(-1), "completion");
  await controller.stop();
});
