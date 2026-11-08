import assert from "node:assert/strict";
import test from "node:test";
import { OpenCodeController, type CursorContext } from "../opencode/controller.ts";
import { recorder, show } from "./fakes.ts";

function context(get: CursorContext["session"]["get"] = async () => ({})): CursorContext & { subscribed: number; aborted: boolean } {
  const ctx = {
    subscribed: 0,
    aborted: false,
    location: { directory: "C:\\test" },
    session: { get },
    event: {
      subscribe: ({ signal }: { signal: AbortSignal }) => {
        ctx.subscribed++;
        return (async function* () {
          await new Promise<void>((resolve) => signal.addEventListener("abort", () => { ctx.aborted = true; resolve(); }, { once: true }));
        })();
      },
    },
  };
  return ctx;
}

function controller(get?: CursorContext["session"]["get"]) {
  const { links, factory } = recorder();
  const ctx = context(get);
  const plugin = new OpenCodeController(ctx, factory);
  return { plugin, link: links[0], ctx };
}

const status = (sessionID: string, type: string) => ({ type: "session.status", data: { sessionID, status: { type } } });

test("forms and permissions are blocked-on-you per session until their own replies", async () => {
  const { plugin, link } = controller();
  await plugin.receive({ type: "session.created", data: { sessionID: "a" } });
  await plugin.receive({ type: "session.created", data: { sessionID: "b" } });
  await plugin.receive({ type: "form.created", data: { form: { sessionID: "a", id: "q" } } });
  await plugin.receive({ type: "permission.asked", data: { sessionID: "b", id: "p" } });
  await plugin.receive({ type: "form.replied", data: { sessionID: "a", id: "q" } });
  assert.deepEqual(show(link.sent), ["awaiting_user:a:form:q", "awaiting_user:b:permission:p", "input_answered:a:form:q"]);
  assert.deepEqual(show(link.snapshot()), ["awaiting_user:b:permission:p"]);
  await plugin.receive({ type: "permission.replied", data: { sessionID: "b", requestID: "p" } });
  await plugin.receive({ type: "form.cancelled", data: { sessionID: "a", id: "never-asked" } }); // unknown: nothing
  assert.deepEqual(show(link.sent).slice(3), ["input_answered:b:permission:p"]);
  assert.ok(link.sent.every((m) => m.tool === "opencode" && m.project === "C:\\test"));
  await plugin.stop();
});

test("a subagent's prompts are reported under its root conversation, and answered there", async () => {
  const { plugin, link } = controller();
  await plugin.receive({ type: "session.created", data: { sessionID: "root" } });
  await plugin.receive({ type: "session.created", data: { sessionID: "child", parentID: "root" } });
  await plugin.receive({ type: "session.created", data: { sessionID: "grandchild", parentID: "child" } });
  await plugin.receive({ type: "permission.asked", data: { sessionID: "grandchild", id: "p" } });
  await plugin.receive({ type: "form.created", data: { form: { sessionID: "child", id: "f" } } });
  assert.deepEqual(show(link.snapshot()), ["awaiting_user:root:permission:p,form:f"]);
  await plugin.receive({ type: "permission.replied", data: { sessionID: "grandchild", requestID: "p" } });
  await plugin.receive({ type: "form.replied", data: { sessionID: "child", id: "f" } });
  assert.deepEqual(show(link.sent), ["awaiting_user:root:permission:p", "awaiting_user:root:form:f",
    "input_answered:root:permission:p", "input_answered:root:form:f"]);
  await plugin.stop();
});

test("an answer goes to the root its prompt opened under, even after the hierarchy is learned", async () => {
  let fail = true;
  const { plugin, link } = controller(async () => {
    if (fail) throw new Error("server busy");
    return { parentID: "root" };
  });
  await plugin.receive({ type: "permission.asked", data: { sessionID: "x", id: "p" } }); // lookup fails: x is its own root
  fail = false;
  await plugin.receive(status("x", "busy")); // now x is known to be root's subagent
  await plugin.receive({ type: "permission.replied", data: { sessionID: "x", requestID: "p" } });
  assert.deepEqual(show(link.sent), ["awaiting_user:x:permission:p", "input_answered:x:permission:p"]);
  await plugin.stop();
});

test("a root's busy -> idle is a finished turn, once; a subagent finishing is silent", async () => {
  const { plugin, link } = controller();
  await plugin.receive({ type: "session.created", data: { sessionID: "root" } });
  await plugin.receive({ type: "session.idle", data: { sessionID: "root" } }); // new and empty: nothing
  await plugin.receive({ type: "session.created", data: { sessionID: "child", parentID: "root" } });
  await plugin.receive({ type: "session.execution.started", data: { sessionID: "root" } });
  await plugin.receive(status("root", "busy"));
  await plugin.receive(status("child", "busy"));
  await plugin.receive(status("child", "idle"));
  await plugin.receive({ type: "session.idle", data: { sessionID: "child" } });
  await plugin.receive(status("root", "idle"));
  await plugin.receive({ type: "session.idle", data: { sessionID: "root" } }); // the same transition
  assert.deepEqual(show(link.sent), ["thinking_started:root", "turn_complete:root"]);
  await plugin.stop();
});

test("a root idling with a subagent's prompt still open stays blocked, not finished", async () => {
  const { plugin, link } = controller();
  await plugin.receive({ type: "session.created", data: { sessionID: "root" } });
  await plugin.receive({ type: "session.created", data: { sessionID: "child", parentID: "root" } });
  await plugin.receive(status("root", "busy"));
  await plugin.receive({ type: "permission.asked", data: { sessionID: "child", id: "p" } });
  await plugin.receive(status("root", "idle"));
  assert.deepEqual(show(link.sent), ["thinking_started:root", "awaiting_user:root:permission:p"]);
  await plugin.stop();
});

test("an existing session's hierarchy is looked up once; unknown hierarchy never claims finished", async () => {
  let lookups = 0;
  const { plugin, link } = controller(async ({ sessionID }) => { lookups++; return sessionID === "child" ? { parentID: "parent" } : {}; });
  await plugin.receive(status("child", "busy"));
  await plugin.receive(status("child", "idle"));
  await plugin.receive({ type: "session.idle", data: { sessionID: "child" } });
  assert.equal(lookups, 2); // the child, then its parent (which has no parent of its own)
  assert.deepEqual(link.sent, []);

  const failing = controller(async () => { throw new Error("offline"); });
  await failing.plugin.receive(status("mystery", "busy"));
  await failing.plugin.receive(status("mystery", "idle"));
  assert.deepEqual(show(failing.link.sent), ["thinking_started:mystery"]);
  await plugin.stop();
  await failing.plugin.stop();
});

test("a run ends with execution succeeded or failed: finished, once, also when status and idle follow", async () => {
  // The sequence a real `opencode run` produced: no session.created for the session (it predates the
  // subscription), and no status or idle event at all.
  let lookups = 0;
  const { plugin, link } = controller(async () => { lookups++; return {}; });
  await plugin.receive({ type: "session.execution.started", data: { sessionID: "ses_1" } });
  await plugin.receive({ type: "session.step.started", data: { sessionID: "ses_1" } });
  await plugin.receive({ type: "session.execution.succeeded", data: { sessionID: "ses_1" } });
  await plugin.receive(status("ses_1", "idle"));
  await plugin.receive({ type: "session.idle", data: { sessionID: "ses_1" } });
  await plugin.receive({ type: "session.execution.started", data: { sessionID: "ses_1" } });
  await plugin.receive({ type: "session.execution.failed", data: { sessionID: "ses_1", error: { message: "x" } } });
  assert.deepEqual(show(link.sent), ["thinking_started:ses_1", "turn_complete:ses_1", "thinking_started:ses_1", "turn_complete:ses_1"]);
  assert.equal(lookups, 1);
  await plugin.stop();
});

test("a root session already running when the plugin loads reports its first idle", async () => {
  const { plugin, link } = controller();
  await plugin.receive({ type: "session.idle", data: { sessionID: "already-running" } });
  assert.deepEqual(show(link.sent), ["turn_complete:already-running"]);
  await plugin.stop();
});

test("interrupting a root ends it; interrupting a subagent answers its own prompts", async () => {
  const { plugin, link } = controller();
  await plugin.receive({ type: "session.created", data: { sessionID: "root" } });
  await plugin.receive({ type: "session.created", data: { sessionID: "child", parentID: "root" } });
  await plugin.receive({ type: "permission.asked", data: { sessionID: "child", id: "c" } });
  await plugin.receive({ type: "permission.asked", data: { sessionID: "root", id: "r" } });
  await plugin.receive({ type: "session.execution.interrupted", data: { sessionID: "child", reason: "user" } });
  assert.deepEqual(show(link.snapshot()), ["awaiting_user:root:permission:r"]);
  await plugin.receive({ type: "session.execution.interrupted", data: { sessionID: "root", reason: "user" } });
  assert.deepEqual(show(link.sent).slice(2), ["input_answered:root:permission:c", "session_end:root"]);
  assert.deepEqual(link.snapshot(), []);
  await plugin.stop();
});

test("deleting or archiving a root ends it; deleting a subagent answers its prompts", async () => {
  const { plugin, link } = controller();
  await plugin.receive({ type: "session.created", data: { sessionID: "root" } });
  await plugin.receive({ type: "session.created", data: { sessionID: "child", parentID: "root" } });
  await plugin.receive({ type: "form.created", data: { form: { sessionID: "child", id: "f" } } });
  await plugin.receive({ type: "session.deleted", data: { sessionID: "child" } });
  await plugin.receive({ type: "session.archived", data: { sessionID: "root" } });
  assert.deepEqual(show(link.sent), ["awaiting_user:root:form:f", "input_answered:root:form:f", "session_end:root"]);
  await plugin.stop();
});

test("location shutdown ends every known root", async () => {
  const { plugin, link } = controller();
  await plugin.receive({ type: "session.created", data: { sessionID: "one" } });
  await plugin.receive({ type: "session.created", data: { sessionID: "two" } });
  await plugin.receive({ type: "session.created", data: { sessionID: "sub", parentID: "two" } });
  await plugin.receive({ type: "location.shutdown", data: {} });
  assert.deepEqual(show(link.sent), ["session_end:one", "session_end:two"]);
  assert.deepEqual(link.snapshot(), []);
  await plugin.stop();
});

test("another location cannot add to or clear this location's state", async () => {
  const { plugin, link } = controller();
  await plugin.receive({ type: "form.created", location: { directory: "C:/TEST" }, data: { form: { sessionID: "a", id: "q" } } });
  await plugin.receive({ type: "location.shutdown", location: { directory: "C:/other" }, data: {} });
  await plugin.receive({ type: "permission.asked", location: { directory: "C:/other" }, data: { sessionID: "b", id: "p" } });
  assert.deepEqual(show(link.sent), ["awaiting_user:a:form:q"]);
  await plugin.stop();
});

test("the snapshot carries one awaiting_user per root with all its open prompts", async () => {
  const { plugin, link } = controller();
  for (const [id, parentID] of [["r1", undefined], ["r2", undefined], ["c1", "r1"]] as const)
    await plugin.receive({ type: "session.created", data: { sessionID: id, parentID } });
  await plugin.receive({ type: "permission.asked", data: { sessionID: "r1", id: "a" } });
  await plugin.receive({ type: "form.created", data: { form: { sessionID: "c1", id: "b" } } });
  await plugin.receive({ type: "permission.asked", data: { sessionID: "r2", id: "c" } });
  assert.deepEqual(show(link.snapshot()), ["awaiting_user:r1:permission:a,form:b", "awaiting_user:r2:permission:c"]);
  await plugin.stop();
});

test("starting connects and subscribes; stopping aborts the subscription and closes, once", async () => {
  const { plugin, link, ctx } = controller();
  plugin.start();
  plugin.start();
  assert.equal(link.starts, 1);
  assert.equal(ctx.subscribed, 1);
  await plugin.stop();
  await plugin.stop();
  assert.equal(ctx.aborted, true);
  assert.equal(link.stops, 1);
});

test("stopping during a hierarchy lookup sends nothing afterwards", async () => {
  let release!: (value: { parentID?: string }) => void;
  const { plugin, link } = controller(() => new Promise((resolve) => { release = resolve; }));
  const pending = plugin.receive({ type: "permission.asked", data: { sessionID: "late", id: "p" } });
  await plugin.stop();
  release({});
  await pending;
  assert.deepEqual(link.sent, []);
  assert.equal(link.stops, 1);
});
