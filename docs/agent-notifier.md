# Agent Notifier — design

> Feature branch: `feat/agent-notifier`. Status: MVP built (pipe + `hook` verb, `AgentActivity`,
> dangling worm-charms, register/unregister/status UI for Claude Code + Codex). Not yet
> live-tested against a real agent session, and per-event pulses (turn-complete pop / error tint)
> are still TODO.

## What & why

AI coding agents (Claude Code, Codex, Cursor, …) fire hooks on lifecycle events
("thinking started", "awaiting your action", "turn complete"). WormsCursor already lives in
your peripheral vision as the cursor — so it's the perfect ambient notifier. When one or more
agents need you, the cursor **grows dangling charms that hang and swing on the existing
pendulum physics**, one per waiting agent. No toast spam, no extra window; the answer to "do
any of my agents need me, and how many?" is always in the corner of your eye.

This is *not* a separate daemon (as an early spec imagined). The already-running tray app —
which is guaranteed single-instance — **is** the daemon.

## Three decisions (locked)

1. **Transport: named pipe, uniform `command` hook for every tool.** Each tool runs
   `WormsCursor.exe hook --tool <t> …`; that invocation normalises the payload and writes one
   JSON line to `\\.\pipe\WormsCursor.<user>`, where the running tray app is listening, then
   exits 0. No HTTP listener, no "POST to myself". This extends the existing single-instance
   idea (which already uses a named `EventWaitHandle`) into a real message pipe. The `hook`
   verb is **not** a second app instance — it short-circuits before any tray/engine init.
2. **Coverage: all themed cursors.** The indicator is an overlay layer composited onto every
   cursor the engine themes (only themed ones — a cursor left as the Windows default can't
   carry it).
3. **Visual: dangling worm-charms on the existing pendulum.** Count = how many agents wait.
   Woven into the same swinging string as the Help "?" / busy loader. Keeps the WOW.

## Architecture

```
Claude Code ─┐  command hook: WormsCursor.exe hook --tool claude-code   (stdin JSON)
Codex CLI ───┤  notify = [..., "hook", "--tool", "codex"]               (argv JSON)
Cursor ──────┘  .cursor/hooks.json command                              (stdin JSON)
                         │ normalise → 1 JSON line
                         ▼
              \\.\pipe\WormsCursor.<user>   (named pipe; server in the tray app)
                         │
                         ▼
              AgentActivity (Core, UI-free)  ── needs-you set, count, pulses
                         │ engine.SetWaitingCount(n) + pulse
                         ▼
              CursorEngine  ── dangling worm-charms on the pendulum (all themed cursors)
```

### 1. Transport & process model

- **`hook` verb** (`Program.cs`, very top of `Main`, *before* `VelopackApp.Build().Run()` and
  the single-instance guard): if `args[0] == "hook"`, read the tool's payload (stdin for
  Claude/Cursor, last argv for Codex), normalise to the wire format, connect the pipe with a
  <500 ms timeout, write one line, exit 0. **Fail-silent**: any error → log to
  `%LocalAppData%\WormsCursor\bridge.log` and exit 0. Never block or crash the agent.
- **Pipe server** (`AgentPipeServer`, App): a background `NamedPipeServerStream`
  (`PipeName = "WormsCursor." + Environment.UserName`, message mode, loop accepting one client
  at a time) owned by `TrayApplicationContext`. Its lifetime is **independent of the engine**
  (like `_keyHook`) so it survives `ApplySettings`' stop/restart. Each received line →
  `AgentActivity.Report(...)` marshalled onto the UI thread via the existing `_marshal`
  control.
- The existing `EventWaitHandle` "show preferences" signal can later fold into this pipe, but
  MVP leaves it untouched.

### 2. Normalised wire format (hook → pipe)

One JSON object per line:

```json
{ "tool": "claude-code", "event": "awaiting_user", "rawEvent": "Notification",
  "sessionId": "abc123", "project": "/abs/path", "message": "Permission needed", "ts": "ISO-8601" }
```

`event` ∈ `thinking_started | awaiting_user | turn_complete | tool_use | error`.
Per-tool mapping (verified against live docs):

| normalised        | Claude Code                                   | Codex                 | Cursor                |
|-------------------|-----------------------------------------------|-----------------------|-----------------------|
| `thinking_started`| `UserPromptSubmit`                            | — (none)              | `sessionStart`/`preToolUse` |
| `awaiting_user`   | `Notification` (`notification_type` = `permission_prompt`/`idle_prompt`) | — (none) | `beforeShellExecution` |
| `turn_complete`   | `Stop`                                        | `agent-turn-complete` | `stop`                |
| `tool_use`        | `PreToolUse`/`PostToolUse`                     | —                     | `preToolUse`/`postToolUse` |
| `error`           | `StopFailure`/`PostToolUseFailure`            | —                     | `postToolUseFailure`  |

Claude Code stdin gives `hook_event_name`, `session_id`, `cwd`, `notification_type`, `message`.
Codex argv JSON gives `type`, `thread-id`, `cwd`, `last-assistant-message`. Codex has **only**
`agent-turn-complete` — no native "awaiting"; we don't fake it.

### 3. State model — `AgentActivity` (Core, UI-free)

A thread-safe tracker of which agent sessions currently **need the user**.

- key = `tool + ":" + sessionId` (sessionId may be null → key by tool+project).
- **needs-you set.** Add on `awaiting_user`, `turn_complete`, `error` (agent finished / is
  blocked → ball is in your court). Remove on `thinking_started` and `tool_use` (the agent is
  actively working again → you must have responded).
- `WaitingCount` = size of the set. This is the number the cursor shows.
- **Pulses**: a transient one-shot per event for animation accents — `turn_complete` → a worm
  "pop", `error` → red tint. Exposed as an `event Action<AgentPulse>`.
- **TTL sweep**: evict sessions with no event for ~30 min so a session that never sends a
  closing event doesn't wedge the count. Codex (turn-complete-only, no clearing event) relies
  on this TTL + clears on its next turn-complete; documented limitation, not a fake state.

`AgentActivity` lives in the tray context (survives engine restarts); on each change it calls
`engine.SetWaitingCount(n)` and forwards pulses. On engine restart the tray re-pushes the
current count.

### 4. Rendering — dangling worm-charms (Core)

New `NotifierRenderer.ComposeCharms(g, layout, settings, count, bobX, bobY, stringDeg, accent)`:
draws up to N little worm-bob charms hanging at the pendulum bob, fanned on short sub-strings,
swinging with `stringDeg`. Count encoding: 1 charm per waiting agent up to a cap (~3 visible);
beyond the cap a small numeral on the lowest bob ("3+"). Reuses the fill/outline style, the
`No`-cursor red for the `error` accent, and a pop spring (same shape as click-pop) for the
`turn_complete` accent.

Engine integration (`CursorEngine`):
- add `volatile int _waitingCount` + `SetWaitingCount(int)` (mirrors the `_ibeamKick` pattern)
  and a small pulse queue.
- a second pendulum bob (or reuse `ringCX/ringCY/helpAngleDeg`) so the charms coexist with the
  busy ring on the busy cursor.
- a single `ApplyCharms(Bitmap bmp)` helper composites the charms onto each cursor bitmap right
  before `MakeCursor`, gated on `_waitingCount > 0` — uniform across all kinds.
- **Arrow path**: when `_waitingCount > 0`, route the arrow through the live-render branch each
  `fgRender` tick (the existing `popActive` branch, generalised) instead of the cheap
  pre-baked frames; composite charms there. `count == 0` → unchanged cheap frames (idle tray
  still burns no CPU).

### 5. Settings / UI

- `CursorSettings` (additive, tolerant-load convention): `AgentNotifierEnabled` (default true),
  `AgentNotifierCap` (max charms before numeral, default 3), maybe `AgentNotifierAccent` color.
  Add each to `CopyFrom`.
- `PreferencesForm`: a small group in the right column near the feedback toggles — "Show
  waiting-agent charms" + cap. (Same live-edit/working-copy pattern as existing toggles.)
- `TrayApplicationContext`: a **"Set up agent hooks…"** menu item between "Preferences…" and
  "Check for updates…", opening an installer dialog; owns + starts/stops the pipe server.
- **Live preview** in that dialog: a "Preview on cursor" row (count picker + *Show worms* / *Clear*)
  that calls `engine.SetWaitingCount(n)` directly so you can see the charms on the real cursor
  without an agent. `Clear`, closing the dialog, or a real agent event ends the preview and
  restores the genuine count (`SetWaitingCount(_agents.WaitingCount)`).

### 6. Installer — `WormsCursor.exe hook`-registration

A small dialog (and/or `hook install --tool <t>`) that, with **backup + merge (never
overwrite)**, writes the hook into each tool's config using the absolute exe path
(`Environment.ProcessPath`, the pattern already in `Autostart.cs`):

- **Claude Code** → `~/.claude/settings.json` `hooks` (`UserPromptSubmit`, `Notification`,
  `Stop`) as `type: "command"`, `command: "<abs> hook --tool claude-code"`.
- **Codex** → `~/.codex/config.toml` `notify = ["<abs>", "hook", "--tool", "codex"]` (root key,
  above any tables — TOML gotcha).
- **Cursor** → project `.cursor/hooks.json` (per-project; offer a copy-paste snippet).
- **Kiro** → GUI-only; provide a copy-paste command, no auto-write.

## Phasing

- **MVP**: pipe server + `hook` verb; Claude Code (stdin) + Codex (argv); `AgentActivity`;
  worm-charm indicator on all themed cursors; installer for Claude Code + Codex; `bridge.log`.
- **Later**: Cursor + Kiro install; richer Claude events (`PermissionRequest`, `SubagentStop`,
  `TeammateIdle`, `TaskCompleted`); per-cursor opt-out; phone push (ntfy/webhook); throttling.

## Gotchas

- **Fail-silent is sacred** — the `hook` verb must never hang or non-zero-exit; <500 ms pipe
  timeout; all errors swallowed to `bridge.log`.
- **Minimal PATH** — hooks run with a stripped PATH; always register the **absolute** exe path.
- **Cold start** — `WormsCursor.exe hook` pays .NET startup (~100-250 ms) per event; fine for
  fire-and-forget. Short-circuit before Velopack/single-instance to keep it lean.
- **Notifier lifetime ≠ engine lifetime** — keep the pipe server out of the engine
  stop/restart cycle (own it like `_keyHook`).
- **Hotspot** — charms live in the canvas padding; never move the hotspot.

## Verification

- Unit-test `AgentActivity` state transitions + TTL (pure Core, no UI).
- `echo '{...}' | WormsCursor.exe hook --tool claude-code` → pipe → count changes; with no tray
  running it must still exit 0 silently.
- Wire a real Claude Code `Stop`/`Notification` hook locally and watch the cursor sprout/clear
  charms; confirm idle CPU stays ~0 at `count == 0`.
