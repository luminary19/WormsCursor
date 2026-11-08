# WormsCursor

A Windows tray app that shows a **bouncing agent-waiting token**: whenever an AI coding agent
(Claude Code or Codex) is blocked on your decision or has finished its turn, its tool logo
appears and bounces — either hanging off the mouse pointer and following it, or
pinned to a screen corner. It ships as a Velopack app (Setup.exe / Portable.zip) that updates
from GitHub Releases (About → Check for updates). No main window: the tray icon (WinForms) opens one
WPF control panel, built with WPF-UI to look like Windows 11 Settings.

> History: this started life as a *Worms 3D*-style cursor theme (rotating arrow + animated system
> cursors via `SetSystemCursor`). That whole cursor-theming engine was removed; the only thing kept
> and made the headline feature is the agent-waiting token, now drawn as a standalone overlay so it
> no longer requires theming any system cursor. The old code is recoverable from git history.

## How it works (the core idea)

The token is drawn by an **owner-drawn layered overlay window** (`NotifierOverlay`), not by touching
the system cursor — so it shows over whatever the real cursor is, with no `SetSystemCursor` and
nothing to restore on exit. The window is transparent, click-through, no-activate and top-most, and
painted with `UpdateLayeredWindow` (true per-pixel alpha). Its animation thread runs
while at least one agent is waiting; idle, the thread parks and the token is painted away, so
a quiet tray burns no CPU.

Which agents are "waiting" is fed in by the same hook → named-pipe plumbing as before:
`WormsCursor.exe hook …` (fired by the agent's lifecycle hooks) writes one normalised JSON event to
the running tray over a per-user named pipe; `AgentActivity` tracks which sessions need you; the tray
pushes that set into the overlay.

**Per-app focus suppression.** While a program on `CursorSettings.SuppressedPrograms` (default
`dota2.exe`) owns the foreground window — fullscreen, borderless or windowed — the overlay treats it
exactly like "nothing waiting": token hidden, animation thread parked. `ForegroundWatcher` (Core) learns
about focus changes from an out-of-context `EVENT_SYSTEM_FOREGROUND` WinEvent hook on its own
message-pumping thread (no polling) and resolves the window to an exe name with
`PROCESS_QUERY_LIMITED_INFORMATION` (works for elevated / anti-cheat processes). It only reports *what*
is in front; the overlay decides with the pure `ProgramSuppression.Matches`, so a settings edit
re-evaluates at once. Agents keep being tracked meanwhile; when focus moves on and something still
waits, the token comes back with its entrance. The OpenCode helper does the same with the same Core
source (see below).

Codex desktop and CLI use `%CODEX_HOME%/hooks.json` through the same shared engine hooks.
Codex runs Windows hooks through PowerShell (`<shell> -NoProfile -Command <commandWindows>`), so the
registered `commandWindows` is `& '<exe>' hook --tool codex` — a bare quoted path is a PowerShell
parse error. Keep asynchronous question state separate from ordinary tool activity: a tool returning
does not mean the user answered.

Every "blocked on you" state must come from a hook event that names its owner and has a matching
"resolved" event: Claude `PermissionRequest` and `PreToolUse(AskUserQuestion)` carry the asking
agent's `agent_id` and name the call they block (`tool_use_id`, or tool name + input hash since
`PermissionRequest` has no `tool_use_id`): that call's `PostToolUse`/`PostToolUseFailure`, or the
agent's `PostToolBatch` (a denied call has no Post event), resolves it, never a parallel call finishing
first. Codex's `PermissionRequest` input doesn't match its call, so it names no call and the agent's
next tool event resolves it. Claude hooks are registered in exec form (`command` = exe, `args`), never
a shell string. `Elicitation` carries no `agent_id`, so
it is keyed by its own id (`mcp_server_name` + `elicitation_id`) and resolved by `ElicitationResult`.
Never derive a blocked state from Claude's `Notification` — it has no `agent_id` (verified in the
2.1.x binary); only its `idle_prompt` reminder is used, as a finished nudge. The OpenCode 2 integration has an independent shared helper, based on the
existing Pi cursor extension; do not route it through throwaway WormsCursor hook processes.

## Projects (`src/`)

### `WormsCursor.Core` — the engine (UI-agnostic, GDI+ only, **no WinForms/WPF**)
The reusable heart: token drawing + the agent-activity model. Hostable from a tray app, console, or
tests.
- `NotifierRenderer.cs` — `DrawToken(g, settings, TokenFace, ...)`: draws one tool logo at a given
  centre/size/swing angle, with the yellow "?" `AttentionBadge` on its top-right when that session is
  blocked on you, and a "+N" pill (tucked beside the badge) when other sessions wait. Pure GDI+; the
  host decides where it lives and how it bounces. One renderer per drawing thread.
- `TokenFace.cs` — `TokenFace.For(Waiting)`: the pure selection of what the token shows: the first
  blocked session's tool (else the first session's), `Blocked`, and `Others` (the "+N").
- `AttentionBadge.cs` — the "?" badge (`Place` + `Draw`): a vector glyph, stateless, so it is
  thread-safe. Compiled into the OpenCode helper too, so both overlays draw the identical badge. Its
  `Overhang` is what the corner placement allows for at the top edge.
- `AgentLogos.cs` / `SvgPath.cs` — the baked vector tool logos (Claude critter, OpenAI knot) parsed
  from SVG path data and rasterised to cached sprites; `SvgPath` is the tiny path-data parser.
- `AgentActivity.cs` — tracks which agent sessions currently **need the user** (thread-safe), exposes
  `Waiting` (one `WaitingAgent(Tool, State)` per session, `State` = `WaitingState.Blocked` when an
  approval/question/elicitation is pending, `Finished` when the turn just ended) and `WaitingCount`,
  raises `WaitingChanged`, and sweeps finished sessions after a TTL (blocked ones are never swept).
- `AgentEventMessage.cs` — the normalised wire format (one JSON line per event) + the
  raw-event → `AgentEventKind` mapping.
- `CursorSettings.cs` — the persisted, tunable settings: token `Size`, `Placement`
  (`Cursor`/`Corner`), `Corner`, the notifier enable + linger timeout, `OutlineColor`, and
  `SuppressedPrograms`. (Name kept for continuity; it's now just the notifier's settings.)
  `SuppressedPrograms` is a read-only list normalized on every assignment and replaced (never mutated)
  on edit, which is what keeps a `Clone()` (member-wise) snapshot independent. `ResetAppearance` /
  `HasDefaultAppearance()` cover the look only (size, placement, corner, outline).
- `LiveSettings.cs` — the **single owner** of the running settings and the only write path: every edit
  (panel or tray) is a `Change(s => …)` that touches just its own fields, then normalizes, saves and
  raises `Changed`. Nobody edits a copy and writes it back, so no editor can revert another's change
  with a stale snapshot. `Current` is a snapshot for reading; `Live` is the instance the overlay reads.
- `TokenMotion.cs` — the token's self-driven motion as pure functions of time (corner bounce, idle
  bob), shared by the overlay and the control panel's preview.
- `SettingsStore.cs` — tolerant JSON load / atomic save in `%LocalAppData%\WormsCursor\settings.json`
  (survives app-folder updates); enums stored as readable names. `Load` (the owning tray app) moves a
  corrupt file aside; `Read` (another process, i.e. the OpenCode helper) never touches the file.
- `ProgramSuppression.cs` — pure: normalize program names (trim, path → file name, add ".exe",
  case-insensitive dedupe) and match a program against the list. Unit-tested.
- `WindowPrograms.cs` — Win32: window/process → exe name, and the visible top-level windows the
  "Add a program" picker lists.
- `ForegroundWatcher.cs` — the event-driven foreground tracker described above.

### `WormsCursor.App` — the tray app (WinForms tray + WPF windows, `net8.0-windows`, AssemblyName `WormsCursor`)
- `Program.cs` — entry point. The `hook` verb short-circuits **first** (throwaway bridge, no UI),
  then `VelopackApp.Build().Run()`, then a single-instance guard, then the tray context.
- `NotifierOverlay.cs` — the layered overlay window + animation loop. Cursor mode: a springy pendulum
  that hangs the token off the pointer and follows it (+ a gentle idle bob). Corner mode: a fixed
  corner with a continuous "ball" bounce. DPI-aware sizing; per-pixel alpha via `UpdateLayeredWindow`.
  Owns a `ForegroundWatcher` and stays hidden + parked while a suppressed program is in front.
- `TrayApplicationContext.cs` — owns the tray icon (Enabled / Preferences… / Exit), the
  `NotifierOverlay`, the pipe server + sweep timer, the `LiveSettings`, and the one control panel
  window (created on first use, kept for the app's lifetime; it is the panel's `IControlPanelHost`).
  Every settings change lands in `OnSettingsChanged` (linger TTL, menu check, overlay refresh,
  status). The "Enabled" item is the master on/off. Reloads the real cursor scheme once on startup
  (self-heal for anyone upgrading from the cursor-theming version).
- `ControlPanel/` — the WPF control panel (WPF-UI, instant-apply, no OK/Cancel):
  - `WpfHost.cs` — creates the one `System.Windows.Application` (`OnExplicitShutdown`, no `Run`) with
    WPF-UI's `ThemesDictionary` + `ControlsDictionary`; the WinForms loop pumps WPF too.
  - `ControlPanelWindow.xaml` — `FluentWindow` (Mica, `SystemThemeWatcher` for live light/dark), a
    `NavigationView` sidebar (Token, Agents, Apps, About) fed prebuilt pages through an
    `INavigationViewPageProvider`, and the at-a-glance status in the pane header. Closing hides it;
    the sidebar folds to icons below 960 DIPs; each page opens scrolled to the top.
  - `TokenPage` — master switch, live animated preview (`TokenPreview`: Core renderer → WPF bitmap
    on the UI thread, its own renderer, both faces over dark and light), size, position, corner.
  - `AgentsPage` + `ToolRow` — per-tool hook cards (Register / Update / Unregister, status from the
    registrar), the linger timeout, and "Preview on screen" (fakes blocked agents on the real overlay).
  - `AppsPage` — the hide-while-using list (Remove per row, empty state), an inline "Add a program"
    picker (open windows or a typed exe name), Restore the default list.
  - `AboutPage` — version, Check for updates → download → restart (`UpdateService`), What's new
    (release notes via `ReleaseNotesFormat`, a pure, tested Markdown reader), Start with Windows, links.
  - `IControlPanelHost`, `PanelStatus` (pure status wording, tested), `PanelStyles.xaml`,
    `SettingHeader`, `ToolLogo`, `PanelAssets`.
- `AgentPipeServer.cs` — the named-pipe server that `hook` invocations write to (own thread).
- `AgentHookBridge.cs` — the `hook` verb: normalises a tool's stdin/argv payload → one pipe line;
  **fail-silent** (sub-½ s pipe timeout, errors to `bridge.log`, always exits 0).
- `Services/AgentHookRegistrar.cs` — writes/removes WormsCursor's `hook` in each tool's config
  (backup + merge, never clobber).
- `Services/UpdateService.cs` — Velopack `UpdateManager`: check, download, apply-and-restart, release
  notes; updating is unavailable (and says so) for dev builds run from `bin\`.
- `SingleInstance.cs` (a second launch opens the panel and hands it foreground rights),
  `Autostart.cs` (per-user `HKCU\…\Run`), `Assets/`.

### `WormsCursor.OpenCodeOverlay` and `integrations/opencode`

The native helper retains Pi's per-client pipe aggregation, singleton ownership, no-client shutdown,
monitor-rate waitable timer, frame coalescing, cached logo, and persistent DIB surface. It sleeps while
hidden and uses the official compact OpenCode mark. Display `waiting` (a permission or form pending)
bakes Core's `AttentionBadge` into the cached logo; `completion` shows the plain mark. The V2 plugin subscribes to `event.data` lifecycle
events and form/permission events, using one persistent pipe connection per plugin host. Build
materializations are immutable and content-addressed under `artifacts/opencode/`.

The helper compiles in Core's `CursorSettings`, `SettingsStore`, `ProgramSuppression`,
`WindowPrograms` and `ForegroundWatcher` as linked source (`<Compile Include="../WormsCursor.Core/…">`,
no DLL reference). `FocusSuppression.cs` hides its token while a suppressed program is in front,
reading the list from the tray's `settings.json` via `SettingsStore.Read` and re-reading it on a focus
change only when the file's write time moved. `host.ts` hashes those linked files into the content
address, so editing them rebuilds the helper.

Use the installed OpenCode V2 SDK as the event contract: the local OpenCode source checkout can be
older. See `integrations/opencode/README.md` for setup and tests.

## Build & run

```powershell
dotnet build WormsCursor.sln -c Release      # build everything
dotnet run --project src/WormsCursor.App     # run the tray app (dev)
```

.NET 8 SDK, pinned by `global.json`. The app is `net8.0-windows`, PerMonitorV2 DPI.

To exercise the notifier without a real agent: **Preferences… → Agents → Preview on screen**, or fire a
hook by hand — `echo '{"hook_event_name":"PermissionRequest","session_id":"t","tool_name":"Bash"}' |
WormsCursor.exe hook --tool claude-code` (the tray must be running).

## Releasing

Pushing a `vX.Y.Z` tag triggers `.github/workflows/release.yml` (Velopack pack → GitHub Release). Use
the **`release` skill** (`.claude/skills/release/`) for the step-by-step (version bump + CHANGELOG
roll + tag + push). `CHANGELOG.md` follows Keep a Changelog with one section per tag; the release
notes are extracted from the matching section. Auto-update wiring lives in `Services/UpdateService.cs`.

## Conventions & notes

- **Core stays UI-free** — only `System.Drawing`. Keep WinForms and WPF out of it.
- **Tray in WinForms, windows in WPF.** The tray icon/menu and the overlay are WinForms; every window
  is WPF + WPF-UI (`UseWPF` + `UseWindowsForms`). The WinForms/GDI+ namespaces are removed from the
  implicit usings, so each file says which stack it uses (WinForms files import `System.Windows.Forms`
  / `System.Drawing` explicitly; WPF files alias GDI+ as `Gdi`). WPF windows shown from the WinForms
  loop need `ElementHost.EnableModelessKeyboardInterop`. New windows reuse `PanelStyles.xaml`.
- **Settings edits go through `LiveSettings.Change`**, touching only the fields being edited; never
  write back a whole copy. UI controls compare against the live value before changing it, so the
  page re-reading the settings into its controls is a no-op.
- Several patterns (settings store, Velopack packaging, delta-on-release) deliberately **mirror the
  sibling PowerLink project**; comments call this out.
- Settings live in `%LocalAppData%\WormsCursor\` (not next to the exe) so updates don't wipe them.
  JSON load is tolerant; additive fields don't need a schema bump, and an old settings file (with the
  now-removed cursor-theming fields) loads cleanly — the extra fields are ignored.
- `tools/generate-icon.py` builds the tray/app icon.
