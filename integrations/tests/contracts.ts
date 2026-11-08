// Type-level contract checks (run by `npm run typecheck`, never executed): the structural interfaces
// the plugins code against must match the installed Pi and OpenCode SDKs, so an SDK change that would
// break a plugin fails the type check instead of failing silently at runtime.
import type {
  AgentSettledEvent, ExtensionAPI, ExtensionContext, SessionShutdownEvent, SessionStartEvent,
  UIPromptEndEvent, UIPromptStartEvent,
} from "@earendil-works/pi-coding-agent";
import type { OpenCodeEvent } from "@opencode/client";
import type { Plugin } from "@opencode/plugin";
import type { CursorContext, CursorEvent } from "../opencode/controller.ts";
import type { PiContext, PiLike, PiPromptEvent, PiSessionEvent, PiSettledEvent } from "../pi/controller.ts";

type Assert<T extends true> = T;
type Extends<A, B> = [A] extends [B] ? true : false;

// ---- Pi 1.3: what Pi hands the handlers is what they read, and the factory takes Pi's real API.
export type PiApi = Assert<Extends<ExtensionAPI, PiLike>>;
export type PiContextShape = Assert<Extends<ExtensionContext, PiContext>>;
export type PiSessionStart = Assert<Extends<SessionStartEvent, PiSessionEvent>>;
export type PiSessionShutdown = Assert<Extends<SessionShutdownEvent, PiSessionEvent>>;
export type PiSettled = Assert<Extends<AgentSettledEvent, PiSettledEvent>>;
export type PiPromptStart = Assert<Extends<UIPromptStartEvent, PiPromptEvent>>;
export type PiPromptEnd = Assert<Extends<UIPromptEndEvent, PiPromptEvent>>;

// ---- OpenCode 2.2 V2: the plugin context, the event names handled, and the data each one reads.
export type OpenCodeContext = Assert<Extends<Plugin.Context, CursorContext>>;
export type OpenCodeEvents = Assert<Extends<OpenCodeEvent, CursorEvent>>;

type Data<T extends OpenCodeEvent["type"]> = Extract<OpenCodeEvent, { type: T }>["data"];
export type SessionCreated = Assert<Extends<Data<"session.created">, { sessionID: string; parentID?: string }>>;
export type SessionStatus = Assert<Extends<Data<"session.status">, { sessionID: string; status: { type: string } }>>;
export type SessionIdle = Assert<Extends<Data<"session.idle">, { sessionID: string }>>;
export type ExecutionStarted = Assert<Extends<Data<"session.execution.started">, { sessionID: string }>>;
export type ExecutionSucceeded = Assert<Extends<Data<"session.execution.succeeded">, { sessionID: string }>>;
export type ExecutionFailed = Assert<Extends<Data<"session.execution.failed">, { sessionID: string }>>;
export type ExecutionInterrupted = Assert<Extends<Data<"session.execution.interrupted">, { sessionID: string }>>;
export type SessionDeleted = Assert<Extends<Data<"session.deleted">, { sessionID: string }>>;
export type SessionArchived = Assert<Extends<Data<"session.archived">, { sessionID: string }>>;
export type PermissionAsked = Assert<Extends<Data<"permission.asked">, { id: string; sessionID: string }>>;
export type PermissionReplied = Assert<Extends<Data<"permission.replied">, { requestID: string; sessionID: string }>>;
export type FormCreated = Assert<Extends<Data<"form.created">, { form: { id: string; sessionID: string } }>>;
export type FormReplied = Assert<Extends<Data<"form.replied">, { id: string; sessionID: string }>>;
export type FormCancelled = Assert<Extends<Data<"form.cancelled">, { id: string; sessionID: string }>>;
export type LocationShutdown = Assert<Extends<"location.shutdown", OpenCodeEvent["type"]>>;
