import assert from "node:assert/strict";
import test from "node:test";
import { createPiExtension, type PiContext, type PiLike } from "../pi/controller.ts";
import { recorder, show } from "./fakes.ts";

type Handler = (event: unknown, ctx: PiContext) => unknown;

/** A stand-in for Pi's extension API: it records the handlers and lets the test fire events. */
class FakePi {
  private readonly handlers = new Map<string, Handler[]>();
  readonly api = {
    on: (event: string, handler: Handler) => {
      this.handlers.set(event, [...(this.handlers.get(event) ?? []), handler]);
      return () => {};
    },
  } as unknown as PiLike;

  async emit(event: string, payload: unknown = {}, ctx: PiContext = tui()): Promise<void> {
    for (const handler of this.handlers.get(event) ?? []) await handler(payload, ctx);
  }
}

function tui(sessionId = "pi-1", overrides: Partial<PiContext> = {}): PiContext {
  return { mode: "tui", hasUI: true, cwd: "C:\\work", sessionManager: { getSessionId: () => sessionId }, ...overrides };
}

async function started(sessionId = "pi-1") {
  const pi = new FakePi();
  const { links, factory } = recorder();
  createPiExtension(pi.api, factory);
  await pi.emit("session_start", { reason: "startup" }, tui(sessionId));
  return { pi, links, link: links[0] };
}

test("only an interactive TUI session connects; print, json, rpc and UI-less (subagent) runtimes never do", async () => {
  for (const ctx of [tui("a", { mode: "print", hasUI: false }), tui("b", { mode: "json", hasUI: false }),
    tui("c", { mode: "rpc", hasUI: true }), tui("d", { mode: "tui", hasUI: false })]) {
    const pi = new FakePi();
    const { links, factory } = recorder();
    createPiExtension(pi.api, factory);
    await pi.emit("session_start", { reason: "startup" }, ctx);
    await pi.emit("agent_start");
    await pi.emit("ui_prompt_start", { kind: "select" });
    await pi.emit("agent_settled", { aborted: false });
    assert.equal(links.length, 0, ctx.mode);
  }
});

test("a prompt during a run is blocked-on-you until it ends; the settled run is finished", async () => {
  const { pi, link } = await started();
  assert.equal(link.starts, 1);
  await pi.emit("agent_start");
  await pi.emit("agent_start"); // repeats are idempotent
  await pi.emit("ui_prompt_start", { kind: "select", title: "Allow bash?" });
  assert.deepEqual(show(link.snapshot()), ["awaiting_user:pi-1:ui:1"]);
  await pi.emit("ui_prompt_end", { kind: "select" });
  assert.deepEqual(link.snapshot(), []);
  await pi.emit("ui_prompt_start", { kind: "custom" });
  await pi.emit("ui_prompt_end", { kind: "custom" });
  await pi.emit("agent_settled", { aborted: false });
  assert.deepEqual(show(link.sent), [
    "thinking_started:pi-1", "awaiting_user:pi-1:ui:1", "input_answered:pi-1:ui:1",
    "awaiting_user:pi-1:ui:2", "input_answered:pi-1:ui:2", "turn_complete:pi-1",
  ]);
  assert.ok(link.sent.every((m) => m.tool === "pi" && m.project === "C:\\work"));
});

test("a prompt while Pi is idle (from a command you typed) is ignored", async () => {
  const { pi, link } = await started();
  await pi.emit("ui_prompt_start", { kind: "select" });
  await pi.emit("ui_prompt_end", { kind: "select" });
  assert.deepEqual(link.sent, []);
  await pi.emit("agent_start");
  await pi.emit("agent_settled", { aborted: false });
  await pi.emit("ui_prompt_start", { kind: "confirm" }); // after the run settled
  assert.deepEqual(show(link.sent), ["thinking_started:pi-1", "turn_complete:pi-1"]);
});

test("a run aborted with Escape ends the session (like Codex's Interrupt), even with a prompt open", async () => {
  const { pi, link } = await started();
  await pi.emit("agent_start");
  await pi.emit("ui_prompt_start", { kind: "input" });
  await pi.emit("agent_settled", { aborted: true });
  assert.deepEqual(show(link.sent), ["thinking_started:pi-1", "awaiting_user:pi-1:ui:1", "session_end:pi-1"]);
  assert.deepEqual(link.snapshot(), []);
});

test("shutdown (quit, reload, new, resume, fork) ends the session and closes; the next start opens a new connection", async () => {
  const { pi, links } = await started("one");
  await pi.emit("session_shutdown", { reason: "reload" });
  assert.deepEqual(show(links[0].sent), ["session_end:one"]);
  assert.equal(links[0].stops, 1);
  await pi.emit("session_start", { reason: "reload" }, tui("two"));
  assert.equal(links.length, 2);
  assert.equal(links[1].starts, 1);
  await pi.emit("agent_start");
  assert.deepEqual(show(links[1].sent), ["thinking_started:two"]);
  assert.deepEqual(show(links[0].sent), ["session_end:one"]); // the old connection hears nothing more
});

test("each registration keeps its own state (Pi may share the module between runtimes)", async () => {
  const first = await started("first");
  const second = await started("second");
  await first.pi.emit("agent_start");
  await first.pi.emit("ui_prompt_start", { kind: "select" });
  assert.deepEqual(show(first.link.snapshot()), ["awaiting_user:first:ui:1"]);
  assert.deepEqual(second.link.snapshot(), []);
  await second.pi.emit("ui_prompt_start", { kind: "select" }); // idle there
  assert.deepEqual(second.link.sent, []);
});
