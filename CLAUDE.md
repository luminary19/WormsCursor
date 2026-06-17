# WormsCursor

A Windows tray app that shows a **bouncing agent-waiting token**: whenever an AI coding agent
(Claude Code) is blocked on your decision or has finished its turn, a small tool logo (Claude's
pixel critter) appears and bounces — either hanging off the mouse pointer and following it, or
pinned to a screen corner. It ships as a Velopack app (Setup.exe / Portable.zip) that auto-updates
from GitHub Releases. No main window — everything lives in the system tray.

> History: this started life as a *Worms 3D*-style cursor theme (rotating arrow + animated system
> cursors via `SetSystemCursor`). That whole cursor-theming engine was removed; the only thing kept
> and made the headline feature is the agent-waiting token, now drawn as a standalone overlay so it
> no longer requires theming any system cursor. The old code is recoverable from git history.

## How it works (the core idea)

The token is drawn by an **owner-drawn layered overlay window** (`NotifierOverlay`), not by touching
the system cursor — so it shows over whatever the real cursor is, with no `SetSystemCursor` and
nothing to restore on exit. The window is transparent, click-through, no-activate and top-most, and
painted with `UpdateLayeredWindow` (true per-pixel alpha). It only animates (a ~60 fps WinForms
timer) while at least one agent is waiting; idle, the timer is stopped and the token painted away, so
a quiet tray burns no CPU.

Which agents are "waiting" is fed in by the same hook → named-pipe plumbing as before:
`WormsCursor.exe hook …` (fired by the agent's lifecycle hooks) writes one normalised JSON event to
the running tray over a per-user named pipe; `AgentActivity` tracks which sessions need you; the tray
pushes that set into the overlay.

## Projects (`src/`)

### `WormsCursor.Core` — the engine (UI-agnostic, GDI+ only, **no WinForms/WPF**)
The reusable heart: token drawing + the agent-activity model. Hostable from a tray app, console, or
tests.
- `NotifierRenderer.cs` — `DrawToken(...)`: draws one tool logo (+ a "+N" badge when several wait) at
  a given centre/size/swing angle. Pure GDI+; the host decides where it lives and how it bounces.
- `AgentLogos.cs` / `SvgPath.cs` — the baked vector tool logos (Claude critter, OpenAI knot) parsed
  from SVG path data and rasterised to cached sprites; `SvgPath` is the tiny path-data parser.
- `AgentActivity.cs` — tracks which agent sessions currently **need the user** (thread-safe), exposes
  `WaitingTools`/`WaitingCount`, raises `WaitingCountChanged`, and sweeps stale sessions after a TTL.
- `AgentEventMessage.cs` — the normalised wire format (one JSON line per event) + the
  raw-event → `AgentEventKind` mapping.
- `CursorSettings.cs` — the persisted, tunable settings: token `Size`, `Placement`
  (`Cursor`/`Corner`), `Corner`, the notifier enable + linger timeout, `OutlineColor`. (Name kept for
  continuity; it's now just the notifier's settings.)
- `SettingsStore.cs` — tolerant JSON load / atomic save in `%LocalAppData%\WormsCursor\settings.json`
  (survives app-folder updates); enums stored as readable names.

### `WormsCursor.App` — the tray app (WinForms, `net8.0-windows`, AssemblyName `WormsCursor`)
- `Program.cs` — entry point. The `hook` verb short-circuits **first** (throwaway bridge, no UI),
  then `VelopackApp.Build().Run()`, then a single-instance guard, then the tray context.
- `NotifierOverlay.cs` — the layered overlay window + animation loop. Cursor mode: a springy pendulum
  that hangs the token off the pointer and follows it (+ a gentle idle bob). Corner mode: a fixed
  corner with a continuous "ball" bounce. DPI-aware sizing; per-pixel alpha via `UpdateLayeredWindow`.
- `TrayApplicationContext.cs` — owns the tray icon, the `NotifierOverlay`, the pipe server + sweep
  timer, autostart + update menu items. The "Enabled" item is the master on/off (it flips
  `AgentNotifierEnabled`). Reloads the real cursor scheme once on startup (self-heal for anyone
  upgrading from the cursor-theming version that may have left a themed cursor behind).
- `PreferencesForm.cs` — the live editor: a token preview (dark/light split), token-size slider,
  placement (cursor/corner) + corner picker, autostart, an **Agent settings…** button, and
  update/version/links.
- `AgentHooksForm.cs` — the "Agent notifications" dialog: enable + linger timeout + a live preview
  that fakes a waiting count, plus per-tool hook **Register/Unregister** status.
- `AgentPipeServer.cs` — the named-pipe server that `hook` invocations write to (own thread).
- `AgentHookBridge.cs` — the `hook` verb: normalises a tool's stdin/argv payload → one pipe line;
  **fail-silent** (sub-½ s pipe timeout, errors to `bridge.log`, always exits 0).
- `Services/AgentHookRegistrar.cs` — writes/removes WormsCursor's `hook` in each tool's config
  (backup + merge, never clobber).
- `Services/UpdateService.cs` — Velopack `UpdateManager`; no-ops cleanly for dev builds run from `bin\`.
- `SingleInstance.cs`, `Autostart.cs` (per-user `HKCU\…\Run`), `ChangelogForm.cs`, `Assets/`.

## Build & run

```bash
dotnet build WormsCursor.sln -c Release      # build everything
dotnet run --project src/WormsCursor.App     # run the tray app (dev)
```

.NET 8 SDK, pinned by `global.json`. The app is `net8.0-windows`, PerMonitorV2 DPI.

To exercise the notifier without a real agent: **Preferences → Agent settings… → Preview**, or fire a
hook by hand — `echo '{"hook_event_name":"Notification","notification_type":"permission_prompt"}' |
WormsCursor.exe hook --tool claude-code` (the tray must be running).

## Releasing

Pushing a `vX.Y.Z` tag triggers `.github/workflows/release.yml` (Velopack pack → GitHub Release). Use
the **`release` skill** (`.claude/skills/release/`) for the step-by-step (version bump + CHANGELOG
roll + tag + push). `CHANGELOG.md` follows Keep a Changelog with one section per tag; the release
notes are extracted from the matching section. Auto-update wiring lives in `Services/UpdateService.cs`.

## Conventions & notes

- **Core stays UI-free** — only `System.Drawing`. Keep WinForms out of it.
- Several patterns (settings store, Velopack packaging, delta-on-release) deliberately **mirror the
  sibling PowerLink project**; comments call this out.
- Settings live in `%LocalAppData%\WormsCursor\` (not next to the exe) so updates don't wipe them.
  JSON load is tolerant; additive fields don't need a schema bump, and an old settings file (with the
  now-removed cursor-theming fields) loads cleanly — the extra fields are ignored.
- `tools/generate-icon.py` builds the tray/app icon.
