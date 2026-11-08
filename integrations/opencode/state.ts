export type DisplayMode = "hidden" | "waiting" | "completion";

export interface CursorSettings {
  enabled: boolean;
  tokenSize: number;
  lingerSeconds: number;
}

export const DEFAULT_SETTINGS: Readonly<CursorSettings> = Object.freeze({
  enabled: true,
  tokenSize: 64,
  lingerSeconds: 30,
});

export function readSettings(options: Record<string, unknown>): CursorSettings {
  const integer = (key: string, minimum: number, maximum: number, fallback: number) => {
    const value = options[key];
    return typeof value === "number" && Number.isInteger(value) && value >= minimum && value <= maximum ? value : fallback;
  };
  return {
    enabled: typeof options.enabled === "boolean" ? options.enabled : DEFAULT_SETTINGS.enabled,
    tokenSize: integer("tokenSize", 24, 256, DEFAULT_SETTINGS.tokenSize),
    lingerSeconds: integer("lingerSeconds", 1, 3600, DEFAULT_SETTINGS.lingerSeconds),
  };
}

interface SessionState {
  parentID?: string;
  classified: boolean;
  busy: boolean | undefined;
  waits: Set<string>;
  completionUntil: number;
}

/** One aggregate per OpenCode location; the native helper aggregates all locations. */
export class CursorState {
  private readonly sessions = new Map<string, SessionState>();

  private session(id: string): SessionState {
    let state = this.sessions.get(id);
    if (!state) {
      state = { classified: false, busy: undefined, waits: new Set(), completionUntil: 0 };
      this.sessions.set(id, state);
    }
    return state;
  }

  classify(id: string, parentID?: string): void {
    const state = this.session(id);
    state.classified = true;
    state.parentID = parentID;
    if (parentID) state.completionUntil = 0;
  }

  created(id: string, parentID?: string): void {
    const existing = this.sessions.has(id);
    this.classify(id, parentID);
    if (!existing) this.session(id).busy = false;
  }

  isClassified(id: string): boolean { return this.sessions.get(id)?.classified ?? false; }

  started(id: string): void {
    const state = this.session(id);
    state.busy = true;
    state.completionUntil = 0;
  }

  settled(id: string, now: number, lingerMs: number): void {
    const state = this.session(id);
    // Both session.status(idle) and session.idle describe the same transition.
    // The host bus emits live transitions, so an unknown session may have started
    // before this plugin loaded. Its first idle still counts; duplicates do not.
    if (state.busy === false) return;
    state.busy = false;
    state.completionUntil = state.classified && !state.parentID && state.waits.size === 0 ? now + lingerMs : 0;
  }

  waiting(id: string, request: string): void {
    const state = this.session(id);
    state.waits.add(request);
    state.completionUntil = 0;
  }

  replied(id: string, request: string): void { this.sessions.get(id)?.waits.delete(request); }
  interrupted(id: string): void {
    const state = this.session(id);
    state.busy = false;
    state.waits.clear();
    state.completionUntil = 0;
  }
  remove(id: string): void { this.sessions.delete(id); }
  clear(): void { this.sessions.clear(); }

  display(now: number): DisplayMode {
    let complete = false;
    for (const state of this.sessions.values()) {
      if (state.waits.size > 0) return "waiting";
      if (state.completionUntil > now) complete = true;
    }
    return complete ? "completion" : "hidden";
  }

  nextExpiry(now: number): number | undefined {
    let next = Infinity;
    for (const state of this.sessions.values()) {
      if (state.completionUntil > now) next = Math.min(next, state.completionUntil);
    }
    return next === Infinity ? undefined : next;
  }
}
