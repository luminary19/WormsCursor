// AgentsCursor's OpenCode plugin: reports when OpenCode is blocked on you or has finished, so the
// AgentsCursor tray shows OpenCode's token. Connect it from AgentsCursor's Agents page (it adds this
// folder to opencode.jsonc `plugins`); the tray owns the token's look, timing and switch.
import { Plugin } from "@opencode/plugin";
import { OpenCodeController } from "./controller.ts";

export default Plugin.define({
  id: "agentscursor.opencode",
  setup(context) {
    if (process.platform !== "win32") return;
    const controller = new OpenCodeController(context);
    controller.start();
    return () => controller.stop();
  },
});
