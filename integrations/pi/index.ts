// AgentsCursor's Pi extension: reports when Pi is blocked on you or has finished, so the AgentsCursor
// tray shows Pi's token. Connect it from AgentsCursor's Agents page (it adds this folder to Pi's
// settings.json `extensions`); the tray owns the token's look, timing and switch.
import { createPiExtension, type PiLike } from "./controller.ts";

export default function agentsCursor(pi: PiLike): void {
  if (process.platform !== "win32") return;
  createPiExtension(pi);
}
