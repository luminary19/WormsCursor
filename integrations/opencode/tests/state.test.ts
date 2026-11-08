import assert from "node:assert/strict";
import test from "node:test";
import { CursorState, DEFAULT_SETTINGS, readSettings } from "../state.ts";

test("new empty sessions stay hidden; work completion expires once and clears on new work", () => {
  const state = new CursorState();
  state.created("root");
  state.settled("root", 0, 30000);
  assert.equal(state.display(0), "hidden");
  state.started("root");
  state.settled("root", 1, 30000);
  state.settled("root", 2, 30000);
  assert.equal(state.nextExpiry(2), 30001);
  assert.equal(state.display(30000), "completion");
  assert.equal(state.display(30001), "hidden");
  state.started("root");
  assert.equal(state.display(100), "hidden");
});

test("loading mid-turn preserves the first live completion and deduplicates idle events", () => {
  const state = new CursorState();
  state.classify("existing");
  state.settled("existing", 100, 30000);
  assert.equal(state.display(101), "completion");
  state.settled("existing", 200, 30000);
  assert.equal(state.nextExpiry(200), 30100);
  state.interrupted("existing");
  state.settled("existing", 300, 30000);
  assert.equal(state.display(300), "hidden");
});

test("waiting sessions dominate completions, preserving independent requests", () => {
  const state = new CursorState();
  state.classify("a");
  state.started("a");
  state.settled("a", 0, 30000);
  state.waiting("b", "form:one");
  state.waiting("b", "permission:two");
  state.waiting("b", "form:one");
  assert.equal(state.display(1), "waiting");
  state.replied("b", "form:one");
  assert.equal(state.display(1), "waiting");
  state.replied("b", "permission:two");
  assert.equal(state.display(1), "completion");
});

test("busy updates and idle cannot erase unanswered questions", () => {
  const state = new CursorState();
  state.classify("root");
  state.started("root");
  state.waiting("root", "form:q");
  state.started("root");
  state.settled("root", 0, 30000);
  assert.equal(state.display(1), "waiting");
  state.replied("root", "form:q");
  assert.equal(state.display(1), "hidden");
});

test("child and unclassified completions stay hidden, child prompts still notify", () => {
  const state = new CursorState();
  state.classify("child", "root");
  state.started("child");
  state.settled("child", 0, 30000);
  state.started("unknown");
  state.settled("unknown", 0, 30000);
  assert.equal(state.display(0), "hidden");
  state.waiting("child", "permission:p");
  assert.equal(state.display(0), "waiting");
  state.remove("child");
  assert.equal(state.display(0), "hidden");
});

test("settings retain Pi defaults and reject invalid values", () => {
  assert.deepEqual(readSettings({}), DEFAULT_SETTINGS);
  assert.deepEqual(readSettings({ enabled: false, tokenSize: 24, lingerSeconds: 3600 }), { enabled: false, tokenSize: 24, lingerSeconds: 3600 });
  assert.deepEqual(readSettings({ enabled: "yes", tokenSize: -1, lingerSeconds: NaN }), DEFAULT_SETTINGS);
});
