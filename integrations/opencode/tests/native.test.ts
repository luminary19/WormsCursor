import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { createConnection } from "node:net";
import test from "node:test";
import { materializeNativeHelper, NativeCursorRuntime } from "../host.ts";
import { DEFAULT_SETTINGS } from "../state.ts";

const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
async function accepting(pipeName: string): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = createConnection(`\\\\.\\pipe\\${pipeName}`);
    const timer = setTimeout(() => { socket.destroy(); resolve(false); }, 200);
    socket.once("connect", () => { clearTimeout(timer); socket.end(); resolve(true); });
    socket.once("error", () => { clearTimeout(timer); resolve(false); });
  });
}

test("real native helper shares clients, survives one disconnect, exits after the last", { skip: process.platform !== "win32", timeout: 70000 }, async () => {
  const helper = await materializeNativeHelper();
  const pipeName = `OpenCodeCursorNative-${process.pid}-${Date.now()}`;
  const child = spawn(helper.executable, ["--pipe", pipeName], { windowsHide: true, stdio: "ignore" });
  const exited = once(child, "exit");
  const options = { pipeName, helperArguments: ["--pipe", pipeName], materialize: async () => helper };
  const first = new NativeCursorRuntime(options);
  const second = new NativeCursorRuntime(options);
  try {
    const deadline = Date.now() + 5000;
    while (!await accepting(pipeName)) {
      if (Date.now() > deadline) throw new Error("helper did not start");
      await delay(30);
    }
    await first.start({ ...DEFAULT_SETTINGS });
    await second.start({ ...DEFAULT_SETTINGS });
    assert.equal(first.getStatus(), "Connected");
    assert.equal(second.getStatus(), "Connected");
    await first.stop();
    await delay(900);
    assert.equal(child.exitCode, null);
    assert.equal(await accepting(pipeName), true);
    await second.stop();
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      const result = await Promise.race([exited, new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error("helper did not exit")), 5000);
      })]);
      assert.equal(result[0], 0);
    } finally { clearTimeout(timer); }
  } finally {
    await first.stop();
    await second.stop();
    if (child.exitCode === null) child.kill();
  }
});
