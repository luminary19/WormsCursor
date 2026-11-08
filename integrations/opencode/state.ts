import type { WireMessage } from "../shared/wire.ts";

interface SessionState {
  /** The parent session; null for a root, undefined while (or if) the hierarchy is unknown. */
  parent: string | null | undefined;
  /** undefined until the first status is seen (the session may have started before the plugin loaded). */
  busy: boolean | undefined;
}

interface Prompt {
  /** The root session the prompt was reported under; its answer goes to the same root. */
  root: string;
  /** The session that asked (a subagent's own session, or the root itself). */
  session: string;
}

/**
 * What one OpenCode location reports to AgentsCursor, as pure state: every event in, the messages
 * to send out. Child sessions are OpenCode's subagents, so everything is reported under the **root**
 * session: one conversation is one token entry, as Claude's subagents share their parent's session.
 *
 * - A permission or form opens a prompt under the asking session's root (a session whose hierarchy
 *   couldn't be looked up counts as its own root, so a prompt is never lost). Its answer is sent under
 *   the root that opened it, whatever is learned later.
 * - Only a root's busy -> idle transition (its run succeeding or failing) is a finished turn, and only when its hierarchy is known and
 *   none of its prompts is open: a subagent finishing is not your move, and an unknown session never
 *   claims to be finished.
 * - Interrupting a root (or deleting it) ends it, like Codex's Interrupt; for a subagent, its own open
 *   prompts are answered.
 */
export class OpenCodeState {
  private readonly project: string;
  private readonly sessions = new Map<string, SessionState>();
  private readonly prompts = new Map<string, Prompt>();

  constructor(project: string) {
    this.project = project;
  }

  private session(id: string): SessionState {
    let state = this.sessions.get(id);
    if (!state) {
      state = { parent: undefined, busy: undefined };
      this.sessions.set(id, state);
    }
    return state;
  }

  /** A session that was just created: its hierarchy is known, and it hasn't worked yet. */
  created(id: string, parent?: string): void {
    const existing = this.sessions.has(id);
    this.classify(id, parent);
    if (!existing) this.session(id).busy = false;
  }

  /** Records a session's parent (none: it is a root). */
  classify(id: string, parent?: string): void {
    this.session(id).parent = parent ?? null;
  }

  /** Whether the session's parent is still unknown. */
  isClassified(id: string): boolean {
    return this.sessions.get(id)?.parent !== undefined;
  }

  /** The parent of a classified session (undefined for a root or an unknown session). */
  parentOf(id: string): string | undefined {
    return this.sessions.get(id)?.parent ?? undefined;
  }

  /** The root of `id`'s conversation, as far as it is known: an unknown link counts as a root. */
  rootOf(id: string): string {
    let current = id;
    const seen = new Set<string>();
    for (;;) {
      const parent = this.sessions.get(current)?.parent;
      if (!parent || seen.has(parent)) return current;
      seen.add(current);
      current = parent;
    }
  }

  started(id: string): WireMessage[] {
    const state = this.session(id);
    const wasBusy = state.busy;
    state.busy = true;
    if (wasBusy === true || this.rootOf(id) !== id) return [];
    return [this.message("thinking_started", id)];
  }

  settled(id: string): WireMessage[] {
    const state = this.session(id);
    const wasIdle = state.busy === false;
    state.busy = false;
    // execution.succeeded/failed, session.status(idle) and session.idle can all describe the same
    // transition: only the first counts.
    if (wasIdle || state.parent !== null || this.hasPrompts(id)) return [];
    return [this.message("turn_complete", id)];
  }

  asked(key: string, session: string): WireMessage[] {
    const root = this.rootOf(session);
    this.prompts.set(key, { root, session });
    return [this.message("awaiting_user", root, [key])];
  }

  /** A prompt was answered (or withdrawn). One asked before this plugin loaded is unknown here, as it is to the tray. */
  answered(key: string): WireMessage[] {
    const prompt = this.prompts.get(key);
    if (!prompt) return [];
    this.prompts.delete(key);
    return [this.message("input_answered", prompt.root, [key])];
  }

  interrupted(id: string): WireMessage[] {
    this.session(id).busy = false;
    return this.end(id);
  }

  removed(id: string): WireMessage[] {
    const messages = this.end(id);
    this.sessions.delete(id);
    return messages;
  }

  /** The location is shutting down: every root it knows ends, and everything is forgotten. */
  shutdown(): WireMessage[] {
    const roots = new Set<string>();
    for (const [id, state] of this.sessions) if (state.parent === null) roots.add(id);
    for (const prompt of this.prompts.values()) roots.add(prompt.root);
    this.sessions.clear();
    this.prompts.clear();
    return [...roots].map((root) => this.message("session_end", root));
  }

  /** What a (re)connected tray must learn: one `awaiting_user` per root with open prompts. */
  snapshot(): WireMessage[] {
    const byRoot = new Map<string, string[]>();
    for (const [key, prompt] of this.prompts) byRoot.set(prompt.root, [...(byRoot.get(prompt.root) ?? []), key]);
    return [...byRoot].map(([root, keys]) => this.message("awaiting_user", root, keys));
  }

  // A root (or a session counted as its own root) ends with all its prompts; a subagent's own
  // prompts are answered.
  private end(id: string): WireMessage[] {
    if (this.rootOf(id) === id) {
      for (const [key, prompt] of this.prompts) if (prompt.root === id) this.prompts.delete(key);
      return [this.message("session_end", id)];
    }
    const answered: WireMessage[] = [];
    for (const [key, prompt] of this.prompts) {
      if (prompt.session !== id) continue;
      this.prompts.delete(key);
      answered.push(this.message("input_answered", prompt.root, [key]));
    }
    return answered;
  }

  private hasPrompts(root: string): boolean {
    for (const prompt of this.prompts.values()) if (prompt.root === root) return true;
    return false;
  }

  private message(event: WireMessage["event"], sessionId: string, inputIds?: string[]): WireMessage {
    return { tool: "opencode", event, sessionId, project: this.project, ...(inputIds ? { inputIds } : {}) };
  }
}
