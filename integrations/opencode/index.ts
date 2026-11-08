import { Plugin } from "@opencode/plugin";
import { CursorController } from "./controller.ts";
import { NativeCursorRuntime } from "./host.ts";
import { readSettings } from "./state.ts";

export default Plugin.define({
  id: "local.opencode-cursor",
  setup(context) {
    if (process.platform !== "win32") return;
    const controller = new CursorController(context, new NativeCursorRuntime(), readSettings(context.options));
    controller.start();
    return () => controller.stop();
  },
});
