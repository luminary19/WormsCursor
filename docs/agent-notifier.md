# Agent Notifier — historical design

> This is an archived design note. The current implementation also replaces the old Codex `notify`
> integration with shared desktop/CLI lifecycle hooks, tracks asynchronous questions, and reports
> OpenCode and Pi through plugins that keep a connection to the tray (see "Current transport" below).
> Use [README.md](../README.md) and the [plugin guide](../integrations/README.md) for current
> behavior and setup.

> **Superseded (rendering only).** This doc describes the original design where the notifier was
> composited onto the *themed system cursors* by `CursorEngine`. The cursor-theming engine has since
> been **removed**, and the token is now drawn by a standalone transparent overlay window
> (`NotifierOverlay`) that follows the cursor *or* pins to a screen corner — no `SetSystemCursor`
> involved. The **transport, wire format, `AgentActivity` state model, and hook registration below are
> still accurate**; only the "Rendering"/engine-integration parts (and "Coverage: all themed cursors")
> are historical. See `CLAUDE.md` / `README.md` for the current architecture.

> Feature branch: `feat/agent-notifier`. Status: MVP built (pipe + `hook` verb, `AgentActivity`,
> per-tool **logo** charms — Claude Code / Codex — that hang on the same pendulum as the busy ring /
> help "?" (same spot, same swing), register/unregister/status UI + a live preview). Not yet
> live-tested against a real agent session; per-agent free (non-overlapping) physics for multiples
> and per-event pulses (turn-complete pop / error tint) are still TODO.

## What & why

AI coding agents (Claude Code, Codex, Cursor, …) fire hooks on lifecycle events
("thinking started", "awaiting your action", "turn complete"). AgentsCursor already lives in
your peripheral vision as the cursor — so it's the perfect ambient notifier. When one or more
agents need you, the cursor **grows dangling charms that hang and swing on the existing
pendulum physics**, one per waiting agent. No toast spam, no extra window; the answer to "do
any of my agents need me, and how many?" is always in the corner of your eye.

This is *not* a separate daemon (as an early spec imagined). The already-running tray app —
which is guaranteed single-instance — **is** the daemon.

## Three decisions (locked)

1. **Transport: named pipe, uniform `command` hook for every tool.** Each tool runs
   `AgentsCursor.exe hook --tool <t> …`; that invocation normalises the payload and writes one
   JSON line to `\\.\pipe\AgentsCursor.<user>`, where the running tray app is listening, then
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
Claude Code ─┐  command hook: AgentsCursor.exe hook --tool claude-code   (stdin JSON)
Codex CLI ───┤  notify = [..., "hook", "--tool", "codex"]               (argv JSON)
Cursor ──────┘  .cursor/hooks.json command                              (stdin JSON)
                         │ normalise → 1 JSON line
                         ▼
              \\.\pipe\AgentsCursor.<user>   (named pipe; server in the tray app)
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
  `%LocalAppData%\AgentsCursor\bridge.log` and exit 0. Never block or crash the agent.
- **Pipe server** (`AgentPipeServer`, App): a background `NamedPipeServerStream`
  (`PipeName = "AgentsCursor." + Environment.UserName`, message mode, loop accepting one client
  at a time) owned by `TrayApplicationContext`. Its lifetime is **independent of the engine**
  (like `_keyHook`) so it survives `ApplySettings`' stop/restart. Each received line →
  `AgentActivity.Report(...)` marshalled onto the UI thread via the existing `_marshal`
  control.
- The existing `EventWaitHandle` "show preferences" signal can later fold into this pipe, but
  MVP leaves it untouched.

> **Current transport** (supersedes the one-client-at-a-time server above): `AgentPipeServer` serves
> up to 64 connections at once (`PipeOptions.CurrentUserOnly`, duplex because Bun can't write to an
> inbound-only pipe), always keeping a listening instance, so a persistent connection never delays a
> hook. Lines are capped at 64 KiB (a longer one drops that connection). Two kinds of client share it:
> the throwaway `hook` bridge (Claude Code, Codex: one line, then exit) and the OpenCode and Pi
> plugins, which hold one connection per host and open it with `{"tool":"pi","event":"attach"}`.
> That makes the connection a **lease**: its messages reach the tray tagged with it, the sessions they
> report are owned by it (`AgentActivity.Report(..., owner)`), and when it closes the server raises
> the release after its last message and `AgentActivity.Release(owner)` drops exactly those sessions,
> so a killed Pi or OpenCode clears at once. The plugins never start the tray: they retry with
> backoff and replay their blocked state on every connect.

### 2. Normalised wire format (hook → pipe)

One JSON object per line:

```json
{ "tool": "claude-code", "event": "awaiting_user", "rawEvent": "Notification",
  "sessionId": "abc123", "project": "/abs/path", "message": "Permission needed", "ts": "ISO-8601" }
```

`event` ∈ `thinking_started | awaiting_user | turn_complete | tool_use | error | session_end |
input_requested | input_answered`; `agentId` names the subagent (absent = main agent) and `inputIds`
names the items an event opens or answers. `tool` is `claude-code`, `codex`, `opencode` or `pi`; the
plugins also send the connection-level `attach` (never an event kind). Pi and OpenCode map their own
events onto these (Pi: `ui_prompt_start`/`ui_prompt_end` during a run, `agent_settled`; OpenCode:
`permission.*`, `form.*`, the root session's run starting and ending), as tabled in
[integrations/README.md](../integrations/README.md). Current mapping (Claude Code 2.1.x, Codex 0.162.x; the
Cursor column of the original design was never shipped):

| normalised        | Claude Code                                   | Codex                 |
|-------------------|-----------------------------------------------|-----------------------|
| `thinking_started`| `UserPromptSubmit` / `SessionStart`           | `UserPromptSubmit`    |
| `awaiting_user`   | `PermissionRequest`, `PreToolUse` of `AskUserQuestion` (both keyed by `agent_id`); `Elicitation` (keyed by its id in `inputIds`) | `PermissionRequest`, `PreToolUse` of `request_user_input` / `request_permissions` |
| `turn_complete`   | `Stop` (unless background subagents are running), `Notification` `idle_prompt` | `Stop` |
| `tool_use`        | `PreToolUse` / `PostToolUse` / `PostToolUseFailure` | `PreToolUse` / `PostToolUse`, `SessionStart` |
| `error`           | `StopFailure`                                 | —                     |
| `session_end`     | `SessionEnd` (/exit, Ctrl+C, /clear, logout)  | `SessionEnd`, `Interrupt` |
| `input_requested` | —                                             | `PostToolUse` of `request_user_input_async` |
| `input_answered`  | `ElicitationResult` (same id as its `Elicitation`) | `UserPromptSubmit` carrying question replies |

Claude's `Notification` payload has no `agent_id` (its hook input is built without the agent's tool
context), so it is never used for a blocked state. Neither do `Elicitation`/`ElicitationResult`
carry one; they are matched by `mcp_server_name` + `elicitation_id` instead. Codex runs Windows hooks
in PowerShell, so its `commandWindows` is registered as `& '<exe>' hook --tool codex`.

Claude has **no** hook for "user started typing" or window focus, so `SessionEnd` (on exit) and
`UserPromptSubmit`/`SessionStart` (on return) are the signals that clear a waiting indicator.

Claude Code stdin gives `hook_event_name`, `session_id`, `cwd`, `notification_type`, `message`.
Codex argv JSON gives `type`, `thread-id`, `cwd`, `last-assistant-message`. Codex has **only**
`agent-turn-complete` — no native "awaiting"; we don't fake it.

### 3. State model — `AgentActivity` (Core, UI-free)

> **Current model** (supersedes the bullets below): each session holds a timed *finished* notice
> (turn complete / idle reminder / error) plus three sticky sets — prompts keyed by blocked agent id,
> prompts keyed by their own item id (elicitations), and pending asynchronous questions. An agent's
> tool activity clears only its own prompt; an item prompt clears only on its answer. `Waiting`
> reports one `WaitingAgent(Tool, State)` per session, `State` = `Blocked` while any sticky set is
> non-empty, else `Finished`. Only finished sessions are swept by the TTL.

A thread-safe tracker of which agent sessions currently **need the user**.

- key = `tool + ":" + sessionId` (sessionId may be null → key by tool+project).
- **needs-you set.** Add on `awaiting_user`, `turn_complete`, `error` (agent finished / is
  blocked → ball is in your court). Remove on `thinking_started` and `tool_use` (the agent is
  actively working again → you must have responded). `session_end` drops the session entirely (the
  agent exited, so there's nothing left to wait on — the logo clears at once, not at the TTL).
- `WaitingCount` = size of the set. This is the number the cursor shows.
- **Pulses**: a transient one-shot per event for animation accents — `turn_complete` → a worm
  "pop", `error` → red tint. Exposed as an `event Action<AgentPulse>`.
- **TTL sweep**: evict sessions with no event for the linger timeout (configurable,
  `AgentNotifierTimeoutSeconds`, default **60 s**) so a session that never sends a closing event
  doesn't wedge the count. The tray sweeps every 5 s so the timeout is honoured promptly. `Ttl` is
  a live-settable property pushed from the prefs UI. Codex (turn-complete-only, no clearing event)
  relies on this TTL + clears on its next turn-complete; documented limitation, not a fake state.

`AgentActivity` lives in the tray context (survives engine restarts); on each change it calls
`engine.SetWaitingCount(n)` and forwards pulses. On engine restart the tray re-pushes the
current count.

### 4. Rendering — bare logo charms on the ring/help pendulum (Core)

`NotifierRenderer.DrawCharms(g, settings, tools, bobX, bobY, swingDeg)`: draws a single waiting
tool's **bare logo** (no tile, no string) centred on the bob and tilted by `swingDeg`. The key
decision: it hangs on the **exact same pendulum and at the exact same spot as the busy ring / help
"?"**, with the exact same swing — as if the logo were simply painted onto those elements, an extra
thing hanging there. The tools list (one tool id per waiting agent) comes from
`AgentActivity.WaitingTools`. Count encoding: **always exactly one logo** (the first waiting tool);
when several wait, a small frameless **"+N"** superscript (N = the rest) sits at its top-right. There
is deliberately never a fan of logos — it stays legible on the tiny cursor canvas.

> **Current rendering** (supersedes the count encoding above): `TokenFace.For(AgentActivity.Waiting)`
> picks the logo, preferring a session that is blocked on you. A blocked session's logo carries a
> yellow "?" `AttentionBadge` on its top-right; a plain logo means the turn just finished. The "+N"
> is a small pill in the same corner, tucked beside the badge.

> **Current icons** (supersede `SvgPath`/`AgentLogos` below): each agent's logo is an SVG embedded in
> Core (`Icons/<tool>/original.svg`: the Claude critter, the OpenAI knot, OpenCode's compact mark and
> pi-cursor's 12-column Powerline pi), rendered with SVG.NET by `TokenSprites`: fitted by its visible
> pixels, given a generic contrast rim (its alpha mask dilated, light for a dark icon, dark for a light
> one), and cached per size. The badge and the pill anchor to the icon's visible bounds.

Logos were baked from each tool's SVG into vector geometry by `SvgPath` (a tiny path-data parser,
mirroring `HandShape`) and held by `AgentLogos` (`claude-code` → the orange pixel critter; the
Codex/OpenAI knot is baked but hidden from the UI until it can be tested); they scale crisply to any
cursor size and get a thin auto-contrast rim (light vs. dark brand colour) so a bare logo still
reads on any background — the same outline-under/fill-over trick the rest of the cursor set uses.
Unknown/future tools fall back to a dot. Per-event accents (`turn_complete` pop, `error` red) remain
TODO.

Engine integration (`CursorEngine`):
- add `volatile string[] _waitingTools` + `SetWaitingAgents(IReadOnlyList<string>)` (mirrors the
  `_ibeamKick` pattern) — one tool id per waiting agent, so each charm knows its logo.
- **two anchors, by cursor kind** (same world-space spring, different pivot):
  - `CharmsTail` — the **arrow & hand rotate** to follow movement, so their logo hangs off the
    *tail* (`ringCX/ringCY`, `swingDeg = helpAngleDeg - 180`) exactly like the "?". Used by the
    arrow live-render path, the hand, the help cursor, and the app-starting (with-arrow) spinner.
  - `CharmsBelow` — the cursors that **don't rotate** (I-beam, crosshair, resize, move, unavailable,
    the no-arrow wait spinner) hang the logo straight *below the hotspot* on a second hotspot-anchored
    pendulum (`belowCX/belowCY`, `belowDrop ≈ 0.42·sz`), so it never appears to orbit them — it just
    hangs under and swings as the cursor translates.
- `ApplyCharms(bmp, bobX, bobY, swingDeg)` composites the logos right before `MakeCursor`, gated on
  `_waitingTools.Length > 0` — uniform across all kinds, only the anchor differs.
- **Arrow path**: when agents wait, route the arrow through the live-render branch each `fgRender`
  tick (the existing `popActive` branch, generalised) instead of the cheap pre-baked frames;
  composite charms there. Empty → unchanged cheap frames (idle tray still burns no CPU).

### 5. Settings / UI

- `CursorSettings` (additive, tolerant-load convention): `AgentNotifierEnabled` (default true),
  `AgentNotifierTimeoutSeconds` (logo linger timeout, default 60, clamped 30–1800), maybe
  `AgentNotifierAccent` color. Add each to `Normalize` (not to `CopyPreferencesFrom`, which only
  carries the fields Preferences edits).
- `AgentHooksForm`: the display section — "Show the agent logo" toggle + a "Clear a stuck logo
  after:" minutes picker (stored as seconds). (Same live-edit/working-copy pattern as existing toggles.)
- `TrayApplicationContext`: a **"Set up agent hooks…"** menu item between "Preferences…" and
  "Check for updates…", opening an installer dialog; owns + starts/stops the pipe server.
- **Live preview** in that dialog: a "Preview on cursor" row (count picker + *Show worms* / *Clear*)
  that calls `engine.SetWaitingCount(n)` directly so you can see the charms on the real cursor
  without an agent. `Clear`, closing the dialog, or a real agent event ends the preview and
  restores the genuine count (`SetWaitingCount(_agents.WaitingCount)`).

### 6. Installer — `AgentsCursor.exe hook`-registration

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
- **Cold start** — `AgentsCursor.exe hook` pays .NET startup (~100-250 ms) per event; fine for
  fire-and-forget. Short-circuit before Velopack/single-instance to keep it lean.
- **Notifier lifetime ≠ engine lifetime** — keep the pipe server out of the engine
  stop/restart cycle (own it like `_keyHook`).
- **Hotspot** — charms live in the canvas padding; never move the hotspot.

## Verification

- Unit-test `AgentActivity` state transitions + TTL (pure Core, no UI).
- `echo '{...}' | AgentsCursor.exe hook --tool claude-code` → pipe → count changes; with no tray
  running it must still exit 0 silently.
- Wire a real Claude Code `Stop`/`Notification` hook locally and watch the cursor sprout/clear
  charms; confirm idle CPU stays ~0 at `count == 0`.
