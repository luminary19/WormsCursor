import assert from "node:assert/strict";
import test from "node:test";
import { addPlugin } from "../scripts/register.ts";

const config = "C:\\config\\opencode.jsonc";
const plugin = "C:\\repo\\integrations\\opencode";

test("registration preserves JSONC comments, other plugins and settings", () => {
  const source = '{\n  // keep my settings\n  "theme": "aura",\n  "plugins": ["some-plugin",],\n}\n';
  const next = addPlugin(source, config, plugin);
  assert.ok(next.includes("// keep my settings"));
  assert.ok(next.includes('"theme": "aura"'));
  assert.ok(next.includes('"some-plugin"'));
  assert.ok(next.includes("file:///C:/repo/integrations/opencode"));
  assert.equal(addPlugin(next, config, plugin), next);
});

test("configured options and alternative local path spelling stay unchanged", () => {
  const source = '{"plugins":[{"package":"C:/repo/integrations/opencode","options":{"tokenSize":32}}]}';
  assert.equal(addPlugin(source, config, plugin), source);
});

test("invalid configuration is rejected instead of replaced", () => {
  assert.throws(() => addPlugin("{ broken", config, plugin), /valid JSONC/);
  assert.throws(() => addPlugin('{"plugins":{}}', config, plugin), /must be an array/);
});
