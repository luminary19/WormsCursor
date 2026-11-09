# AgentsCursor

A Windows tray app that shows a **bouncing agent-waiting token**: whenever an AI coding agent
(Claude Code, Codex, OpenCode or Pi) is blocked on your decision or has finished its turn, its tool logo
appears and bounces — following the mouse pointer (below, above or beside it), or
pinned to a screen corner. Each agent's icon is its logo, one of three built-in mascots, or an SVG the
user added. It ships as a Velopack app (Setup.exe / Portable.zip) that updates
from GitHub Releases (About → Check for updates). No main window: the tray icon (WinForms) opens one
WPF control panel, built with WPF-UI to look like Windows 11 Settings.

> History: this started life as a *Worms 3D*-style cursor theme (rotating arrow + animated system
> cursors via `SetSystemCursor`). That whole cursor-theming engine was removed; the only thing kept
> and made the headline feature is the agent-waiting token, now drawn as a standalone overlay so it
> no longer requires theming any system cursor. The old code is recoverable from git history.
> It was then called **WormsCursor** until 0.8.2 and renamed **AgentsCursor** (exe, namespaces,
> `%LocalAppData%` folder, pipe, mutex, Run value, Velopack packId). The GitHub repository and this
> checkout's folder keep the old name. The only code that still says WormsCursor is the repo URL
> and the migration from it: `SettingsStore.Load` reading the old settings file while there is no
> new one, the Run value (`TrayApplicationContext.MigrateAutostartFromWormsCursor`), and the registrar
> honouring an old `.wormscursor.bak` backup (its hooks are simply outdated connections, refreshed on
> startup).

## How it works (the core idea)

The token is drawn by an **owner-drawn layered overlay window** (`NotifierOverlay`), not by touching
the system cursor — so it shows over whatever the real cursor is, with no `SetSystemCursor` and
nothing to restore on exit. The window is transparent, click-through, no-activate and top-most, and
painted with `UpdateLayeredWindow` (true per-pixel alpha). Its animation thread runs
while at least one agent is waiting; idle, the thread parks and the token is painted away, so
a quiet tray burns no CPU.

Which agents are "waiting" is fed in over one per-user named pipe (`AgentsCursor.<user>`), which
`AgentPipeServer` serves to many clients at once. Claude Code and Codex fire
`AgentsCursor.exe hook …` from their lifecycle hooks: a throwaway process that writes one normalised
JSON event and exits. The OpenCode and Pi plugins (`integrations/`) keep **one persistent connection
per host** and open it with an `attach` line, which makes it a **lease**: their sessions are owned by
the connection, and when it closes (`LeaseReleased`, raised after its last message)
`AgentActivity.Release` drops exactly those sessions, so a killed Pi or OpenCode clears at once. The
plugins never launch the tray; they retry with backoff and replay their blocked state on reconnect.
`AgentActivity` tracks which sessions need you; the tray pushes that set into the overlay. The pipe is
`CurrentUserOnly`, duplex (Bun, OpenCode's runtime, can't write to an inbound-only pipe), capped at 64
connections and 64 KiB per line.

**Per-app focus suppression.** While a program on `CursorSettings.SuppressedPrograms` (default
`dota2.exe`) owns the foreground window — fullscreen, borderless or windowed — the overlay treats it
exactly like "nothing waiting": token hidden, animation thread parked. `ForegroundWatcher` (Core) learns
about focus changes from an out-of-context `EVENT_SYSTEM_FOREGROUND` WinEvent hook on its own
message-pumping thread (no polling) and resolves the window to an exe name with
`PROCESS_QUERY_LIMITED_INFORMATION` (works for elevated / anti-cheat processes). It only reports *what*
is in front; the overlay decides with the pure `ProgramSuppression.Matches`, so a settings edit
re-evaluates at once. Agents keep being tracked meanwhile; when focus moves on and something still
waits, the token comes back with its entrance.

**Why the token is hidden is one decision.** `TokenVisibility.Decide` (Core, pure) returns the
`HideReason`, checked in order: `NothingWaiting`, `TurnedOff`, `AppInFront`, `Meeting`, else shown. The
overlay is the only caller (in `Reevaluate`, from the UI thread or a watcher's thread), parks when it
isn't shown, and publishes the result as `NotifierOverlay.Visibility` + `VisibilityChanged`: the sounds
read it (no chime for a hidden token) and the Apps page shows a `Meeting` reason under its switch.
Add a new reason there, never as a separate check somewhere else.

**Meetings and screen sharing.** Unless `CursorSettings.ShowDuringMeetings` (off by default), a
meeting hides the token. `MeetingSignals` (Core, Win32, read-only) reads: `SHQueryUserNotificationState`
= `QUNS_PRESENTATION_MODE`; `QueryDisplayConfig(QDC_DATABASE_CURRENT)` topology = clone (Win+P
Duplicate); and the capability consent store
(`HKCU\...\CapabilityAccessManager\ConsentStore\{microphone,webcam,graphicsCaptureProgrammatic,graphicsCaptureWithoutBorder}`,
a key per package family, and per exe under `NonPackaged` with `#` for `\`), where
`LastUsedTimeStop` = 0 means in use (the data behind Windows' privacy indicators; Windows 11 records
Windows.Graphics.Capture use there too). `MeetingDetection.Detect` (pure) decides: presenting, then
duplicated, then screen capture, camera, microphone; capture and camera count after
`DeviceSettle` (1 s, so a screenshot's milliseconds-long capture never does), the microphone after
`MicrophoneSettle` (10 s, so a short dictation doesn't). A "stop = 0" entry only counts while a copy
of that app runs that started before the use did: entries left by an app killed mid-call or by a
shutdown during one stay "in use" forever (the dev machine had four, the oldest ten months old), and
counting them would hide the token for good. Limits: any app holding the microphone counts (game voice
chat, an always-on voice app); Desktop Duplication capture isn't recorded by Windows; a full-screen
slideshow on one extended display isn't presentation mode. `MeetingWatcher` only runs while
`TokenVisibility.NeedsMeetingCheck` (something waits, on, no listed app in front, rule on): it checks
once on `Watch(true)` (on the caller's thread, about 13 ms) and then every second on a timer; off, it
runs nothing. A failing check counts as no meeting and is logged once.

**Sounds.** Off by default (`SoundsEnabled`). `SoundCues` (Core, pure) picks the chime when the
waiting set changes: `Arrivals` are sessions (`WaitingAgent.Session`, `AgentActivity`'s key) that
weren't waiting or went finished -> blocked; a blocked arrival plays `NeedsYouSound`, else
`DoneSound`; nothing while the token isn't shown; one chime per `Burst` (1 s). The tray calls it in
`OnWaitingChanged` right after pushing the set to the overlay (so `Visibility` is current) and hands
the result to `ChimePlayer`. The chimes are `Assets/Sounds/*.wav` (embedded as `Sounds/<name>.wav`),
synthesised by `tools/generate-chimes.py` (16-bit mono 44.1 kHz, under 1 s, -3 dBFS peak).
`ChimeAudio` scales a copy of the samples by `Gain(volume)` (squared), so the system volume is never
touched, and `ChimePlayer` plays it with `PlaySound(SND_MEMORY | SND_ASYNC)` from a thread-pool thread
(never the UI or animation thread), keeping the buffer alive until the next chime replaces it.

**Analytics history.** A **wait** is a session waiting in one state, from when it starts until it stops
or switches between blocked and finished. `AgentActivity` raises `WaitEnded(WaitRecord)` once per
ended wait (outside its lock, on the reporting thread): blocked ends `Answered` (any event that clears
it: the call going ahead or denied, an answer, a new prompt, the turn ending) unless the session ended;
finished ends `Resumed`, or `Expired` when swept; `SessionEnded`, `Release` and `EndAll` (the tray
stopping, in `StopEverything`) end it `Ended`. The tray hands each record to `WaitHistory`, which only
queues it; one background task appends `{"tool","kind","start","end","outcome"}` lines to
`%LocalAppData%\AgentsCursor\history\yyyy-MM.jsonl` (by UTC start month), ending a torn last line
before appending; `Load` parses line by line (a bad line is skipped and logged once), keeps the current
month + `RetainedMonths` (12) before it, and the writer deletes older month files. Nothing else is
stored (no session id, project, path or prompt); previews never touch `AgentActivity`, so they aren't
recorded. `ActivityStats` (pure) turns the records into the page's numbers, by **local calendar day of
the start** (`TimeZoneInfo` per instant, so DST never moves a wait): daily counts (all waits);
current streak (consecutive active days up to today, or up to yesterday while today has none yet);
longest streak (latest on a tie); **average response time** = the mean `Duration` (end - start) of the
`Blocked` waits that ended `Answered` and started in the last 30 days (today + 29), i.e. from the
moment the session started needing you (its token appeared, or would have while hidden) to the event
that answered it, excluding done waits and blocked waits that ended `Ended`; formatted "42 s",
"3 m 10 s", "1 h 5 m"; per-agent counts of all waits in the same 30 days, in `IconCatalog.Tools`
order then other tools by name; heat levels by quartile of the shown active days (the busiest is
always 4); `Percentages` by largest remainder (sum 100).

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
2.1.x binary); only its `idle_prompt` reminder is used, as a finished nudge.

Pi and OpenCode report through the plugins, never through throwaway hook processes, with the same
blocked / finished / abort semantics as the hooks (see `integrations/README.md` for the tables). Pi:
only an interactive TUI runtime reports (in-process subagents have no UI; their approvals are prompts
in the parent TUI); `ui_prompt_start`/`ui_prompt_end` during a run are blocked (a prompt while idle is
ignored), `agent_settled` is finished, an aborted settle is `session_end` (like Codex's Interrupt).
Requires Pi 1.3+. OpenCode: everything is reported under the **root** session (a child session is a
subagent), each answer under the root its prompt opened under; a root's run ending
(`session.execution.succeeded`/`failed`, or status idle / `session.idle`, first one counts) is
finished. A real `opencode run` emits no status or idle event, only the execution result.

Everything after the event mapping is shared by all four agents: the token, badge and "+N"
(`TokenFace`), the linger TTL, the per-agent icon, the Agents card and its states, the startup
refresh, and focus suppression. The README's "four agents side by side" table lists where they
differ, and each difference is forced by what the tool reports: Claude runs no hook on Escape (Codex
`Interrupt`, Pi's aborted settle and OpenCode's `session.execution.interrupted` all end the session);
hook tools can't clear on a kill or replay when the tray starts late (the plugins' leases and
snapshots do); only Pi can tell a headless run (its `ctx.mode`), so `claude -p`, `codex exec` and
`opencode run` show a done token. Codex's `SessionStart` is a `tool_use` (not a `thinking_started`
like Claude's) because a resumed Codex thread can still have open asynchronous questions.

## Projects (`src/`)

### `AgentsCursor.Core` — the engine (UI-agnostic, GDI+ only, **no WinForms/WPF**)
The reusable heart: token drawing + the agent-activity model. Hostable from a tray app, console, or
tests.
- `NotifierRenderer.cs` — `DrawToken(g, settings, TokenFace, ...)`: draws one tool logo at a given
  centre/size/swing angle, with a `StatusBadge` cloud on its top-right (a yellow "?" when that session
  is blocked on you, a green check when it finished), and a "+N" pill (tucked beside the badge) when
  other sessions wait. `DrawIcon(...)` draws just the icon, with no badge, for the panel's cards and
  tiles. Pure GDI+; the host decides where it lives and how it bounces. One renderer per drawing thread.
- `TokenFace.cs` — `TokenFace.For(Waiting)`: the pure selection of what the token shows: the first
  blocked session's tool (else the first session's), `Blocked`, and `Others` (the "+N").
- `StatusBadge.cs` — the status badge (`Place` + `Draw(..., WaitingState)`): one fluffy cloud shape
  (overlapping puffs filled as a single winding path, wider than tall, scalloped all round), flat yellow
  with a "?" for blocked and flat green with a check for finished, inside the same two-tone edge the
  icons get (dark outline, light halo) so it matches the flat mascots. All vector, stateless, so it is thread-safe, drawn on every agent's icon. Its
  `Overhang` is what the corner placement allows for at the top edge.
- `IconCatalog.cs` / `IconSource.cs` / `TokenSprites.cs` + `Icons/<tool>/<variant>.svg` — the token
  icons. Per agent: its `original` logo (Claude critter, OpenAI knot, OpenCode's compact mark,
  pi-cursor's Powerline pi generated once from its font into paths) and three mascots
  (claude-code: waving, party, peek; codex: buddy, spin, sleepy; opencode: buddy, brackets, stack;
  pi: walker, shades, pie). Mascot style rules: the brand colours and silhouette, flat fills (Pi keeps
  its 12-column palette; its pixel variants were generated so each 10-unit column has pi-cursor's
  colour), faces in one language (dark ink eyes on light bodies, white eyes on Codex's dark knot, which
  gets a solid face plate because the rim fills the knot's gaps), nothing finer than 1/24 of the
  bounds, aspect 0.6-1.6, at most 8 KB, and nothing important at the top-right where the badge sits
  (the critter waves with its left arm). Each mascot's viewBox must hold every frame of its loop with
  room to spare (the probe that measures it clips at the viewBox). Rendered with SVG.NET (`Svg`, MS-PL) into cached sprites per
  renderer: fitted by the icon's visible pixels (not its viewBox), with the same two-tone contrast rim
  for every icon (the alpha mask dilated: a dark band, then a light one; at most 3 px x DPI; a hole
  inside the icon takes whichever band contrasts with the icon). `NotifierRenderer.IconShape` lets a
  host size icons of different shapes alike. `Sprite.Visual` is what the badge
  and the "+N" anchor to. An icon that can't be loaded or drawn falls back to the agent's original
  (logged once per icon). SVG.NET never fetches external images, elements or entities
  (`TokenSprites.EnsureConfigured`). Unknown tools get a grey dot. `IconChoices` normalises the
  per-agent choice (`CursorSettings.Icons`).
- `MascotMotion.cs` — the mascots' idle loops. `MascotLoops.For(iconId)` gives a built-in mascot's
  `MascotLoop` (originals and added icons: none): a few seconds long, sampled at its `Fps`, moving
  `MascotPart`s, each an SVG element by `id` with a pivot and `MascotKey`s (`PartPose`: move, turn,
  scale about the pivot, absolute opacity; `Ease` Smooth / Linear / Step for pixel-art pose swaps). A
  loop starts at rest (each part where its file draws it; a part hidden in the file has opacity 0, e.g.
  a confetti bit or the next z) and is at rest most of the time; a conveyor (Sleepy's z's, Pie's steam)
  ends the loop with its copies swapped so the wrap is seamless. `FrameAt(clock)` is pure: each sample is
  drawn as the first sample with the same poses (every rest sample is frame 0), so hosts cache one
  picture per pose. `MascotLoops.Animate(settings, animationEffects)` = `AnimateMascots` unless the
  effective motion is Still. Hosts pass a `mascotClock` (null = rest) to `DrawToken`/`DrawIcon`: the
  overlay its own `_t` (from the token's appearance, on the animation thread; nothing new runs while
  nothing waits), `TokenPreview` its frame clock, the Customization page's tiles a clock of their own.
  `TokenSprites` keeps a strip of sprites per icon and size, a frame built the first time it is drawn,
  every frame fitted by the **union** of the loop's visible bounds (measured once per process), so the
  size and `Sprite.Visual`, which the badge and "+N" anchor to, never change between frames (or with the
  switch); the rim's hole colour comes from the rest frame so it can't flip mid-loop; animated strips
  are capped at `AnimatedBudget` (48 MB) per renderer, least recently drawn dropped first.
- `CustomIconStore.cs` / `SvgSanitizer.cs` / `SvgReferences.cs` — added icons:
  `%LocalAppData%\AgentsCursor\icons\<tool>\<hash>.svg` holds the sanitised text, named by the first
  64 bits of its SHA-256 (the id is `custom:<hash>`), written to a temp name then moved, never
  rewritten, with the upload's tidied file name kept beside it (shown under the tile); at most 24 per
  agent; an icon must draw something at 64 px within 2 s. Removing the icon an agent shows sets that
  agent back to its original first; `Restore` undoes a removal (same id, name, place and selection).
  The sanitiser rejects >256 KB, malformed XML, a non-SVG root, >5,000 elements or depth >64,
  `@import` and any `url()` outside the file; a DOCTYPE is skipped unread (entities never expand) and
  an `<svg>` root without a namespace is read as SVG; it removes `script`/`foreignObject`/`iframe`/
  `audio`/`video`, `on*` attributes and every link but `#fragment` (and inline raster `data:` on
  `<image>`). `SvgReferences` then reads the file as SVG.NET will draw it and refuses references that
  loop, nest deeper than 64 once followed, or would draw more than 20,000 elements (SVG.NET follows
  references unbounded: a loop overflows the stack and kills the process). `IconSource` re-sanitises
  on every load. `IconSource.Custom` is the store (tests point it at a temp folder). The store lives
  in Core, not the App, because the renderer (Core) reads it.
- `AgentActivity.cs` — tracks which agent sessions currently **need the user** (thread-safe), exposes
  `Waiting` (one `WaitingAgent(Tool, State, Session)` per session, `State` = `WaitingState.Blocked` when an
  approval/question/elicitation is pending, `Finished` when the turn just ended) and `WaitingCount`,
  raises `WaitingChanged`, and sweeps finished sessions after a TTL (blocked ones are never swept).
  A session reported over a plugin connection records it as its owner; `Release(owner)` drops them.
  Raises `WaitEnded` per ended wait; `EndAll` ends everything (see "Analytics history").
- `WaitRecord.cs` (`WaitRecord`, `WaitEnd`), `WaitHistory.cs` (the store), `ActivityStats.cs` (the
  metrics) — the Analytics history, described above.
- `AgentEventMessage.cs` — the normalised wire format (one JSON line per event) + the
  raw-event → `AgentEventKind` mapping.
- `CursorSettings.cs` — the persisted, tunable settings: token `Size`, `Placement`
  (`BelowPointer`/`Corner`/`AbovePointer`/`BesidePointer`, explicit values so the old numbers keep
  their meaning), `Corner`, `Icons` (tool -> icon id, read-only and replaced on edit; `IconFor`), the
  notifier enable + linger timeout, `OutlineColor`, and
  `SuppressedPrograms`, `Motion` (`MotionStyle`), the sounds (`SoundsEnabled`, `NeedsYouSound`,
  `DoneSound` as `Chime`, `SoundVolume` 0-100), `ShowDuringMeetings` and `AnimateMascots`, plus the control panel's
  `Theme` (`ThemePreference` System/Light/Dark).
  (Name kept for continuity; it's now just the notifier's settings.)
  `SuppressedPrograms` is a read-only list normalized on every assignment and replaced (never mutated)
  on edit, which is what keeps a `Clone()` (member-wise) snapshot independent. `ResetAppearance` /
  `HasDefaultAppearance()` cover the look only (size, placement, corner, outline, icons). `Version` 2:
  `SettingsStore` migrates a v1 `"Placement": "Cursor"` to `"BelowPointer"` before deserialising (an
  older app reading a v2 file treats the new names as corrupt and uses defaults).
- `LiveSettings.cs` — the **single owner** of the running settings and the only write path: every edit
  (panel or tray) is a `Change(s => …)` that touches just its own fields, then normalizes, saves and
  raises `Changed`. Nobody edits a copy and writes it back, so no editor can revert another's change
  with a stale snapshot. `Current` is a snapshot for reading; `Live` is the instance the overlay reads.
- `TokenMotion.cs` — the token's motion as pure functions, shared by the overlay and the panel's
  preview, in the `MotionProfile` of the chosen `MotionStyle`: Bouncy (the numbers below), Calm (k 60,
  c 9, half the swing, hop and bob, 0.7x the pace) or Still (infinite stiffness = rigid: the token sits
  exactly at its rest point, upright, no hop, bob or corner bounce). `MotionProfile.For(style,
  animationEffects)` makes every style Still while Windows' animation effects
  (`SPI_GETCLIENTAREAANIMATION`) are off. Every pointer position is one tether: a spring (Bouncy: k 130,
  c 3, the old pendulum's) toward
  pointer + `RestFor(placement)` (below `(0, 22dpi + (0.5 + badge overhang)t)`, so the token, badge
  and "+N" hang clear of the arrow; above `(0, -.62t)`; beside `(14dpi + .5t, 18dpi + .3t)`; below and
  above are each other's mirror, so a flip never lands under the arrow), a taut string at rest length
  + 0.30t, tilt measured from the rest direction (so upright at rest) and clamped by `MaxSwingFor`
  (±80° below and above, ±35° beside), idle bob along the string, entrance hop back along it. Near the
  edges of the pointer's monitor `Edges` mirrors the rest per axis (flips as soon as the token would
  leave, back only with 0.25t to spare), `KeepOnScreen` slides it along an edge it would still
  overhang (an axis with no offset, a corner), and the work area is a wall the token stops at.
  `CornerBounce` for corners. `TokenCanvas` sizes the overlay's square canvas for any tilt plus badge,
  pill and rim.
- `SettingsStore.cs` — tolerant JSON load / atomic save in `%LocalAppData%\AgentsCursor\settings.json`
  (survives app-folder updates); enums stored as readable names. `Load` never touches a file (a
  corrupt one is copied aside by the next `Save`). `Load` owns the carry-over from the rename: while
  the AgentsCursor file doesn't exist, it reads `%LocalAppData%\WormsCursor\settings.json` (read-only;
  the first save writes the new file), and a corrupt new file never falls back to the old one.
- `ProgramSuppression.cs` — pure: normalize program names (trim, path → file name, add ".exe",
  case-insensitive dedupe) and match a program against the list. Unit-tested.
- `WindowPrograms.cs` — Win32: window/process → exe name, and the visible top-level windows the
  "Add a program" picker lists.
- `ForegroundWatcher.cs` — the event-driven foreground tracker described above.
- `TokenVisibility.cs`, `MeetingDetection.cs`, `MeetingSignals.cs`, `MeetingWatcher.cs` — why the
  token is hidden, and the meeting detection, described above.
- `SoundCues.cs` (`Chime`, when a chime plays) and `ChimeAudio.cs` (volume) — the sounds, described above.

### `AgentsCursor.App` — the tray app (WinForms tray + WPF windows, `net8.0-windows`, AssemblyName `AgentsCursor`)
- `Program.cs` — entry point. The `hook` verb short-circuits **first** (throwaway bridge, no UI),
  then `VelopackApp.Build().Run()`, then a single-instance guard, then the tray context.
- `NotifierOverlay.cs` — the layered overlay window + animation loop. Pointer positions run
  `TokenMotion.Step` with the rest flipped/slid for the work area of the monitor under the pointer;
  corner mode is a fixed corner with a continuous "ball" bounce. The window is re-centred on the token
  every frame (`TokenCanvas.Side`), so no swing or offset clips; a placement change re-seeds the
  motion. DPI-aware sizing; per-pixel alpha via `UpdateLayeredWindow`.
  Owns a `ForegroundWatcher` and a `MeetingWatcher`, decides with `TokenVisibility.Decide` and stays
  hidden + parked unless it says shown; publishes `Visibility`. Each frame moves with
  `MotionProfile.For(Motion, animation effects)`; the Windows setting is re-read on every re-evaluation
  and on `SystemEvents.UserPreferenceChanged`.
- `TrayApplicationContext.cs` — owns the tray icon (Enabled / Preferences… / Exit), the
  `NotifierOverlay`, the pipe server + sweep timer, the `LiveSettings`, and the one control panel
  window (created on first use, kept for the app's lifetime; it is the panel's `IControlPanelHost`).
  Every settings change lands in `OnSettingsChanged` (linger TTL, menu check, overlay refresh).
  Owns the `ChimePlayer` and `SoundCues` (chimes on new waiting sessions, see "Sounds"), and the
  `WaitHistory` (fed by `AgentActivity.WaitEnded`; `StopEverything` ends the open waits with
  `EndAll` and then disposes it, which writes what is queued).
  The "Enabled" item is the master on/off. On startup it moves WormsCursor's Start with Windows entry
  over, then (posted, after the pipe is up) runs `AgentHookRegistrar.RefreshConnected`: every
  connection that is ours but Outdated is re-registered in place, nothing else is touched, and one
  balloon lists the steps the refreshed tools need (Codex: re-approve under `/hooks`, also in Orca);
  clicking it opens the Agents page. It keeps `ChangedThisRun` / `RefreshErrors` for the cards. Reloads
  the real cursor scheme once on startup (self-heal for anyone upgrading from the cursor-theming
  version).
- `ControlPanel/` — the WPF control panel (WPF-UI, instant-apply, no OK/Cancel):
  - `WpfHost.cs` — creates the one `System.Windows.Application` (`OnExplicitShutdown`, no `Run`) with
    WPF-UI's `ThemesDictionary` + `ControlsDictionary`; the WinForms loop pumps WPF too.
  - `ControlPanelWindow.xaml` — `FluentWindow` (Mica) with its `TitleBar` in its own row above a
    `NavigationView` sidebar (Analytics, Sound and Display, Customization, Agents, Apps, and About in
    the footer; `PanelSection` names them; Analytics is the home page: the panel first opens on it, and
    the tray's double-click, Preferences… and a second launch open it there) fed prebuilt pages through an
    `INavigationViewPageProvider`. The title bar is deliberately not handed to the NavigationView
    (that indents it for a back button and pushes the page down); its padding lines the app icon and
    name up with the sidebar items' icons and labels. The theme follows `CursorSettings.Theme`:
    System uses `SystemThemeWatcher` (live), Light/Dark apply directly; both go through
    `ApplicationThemeManager`, so WpfHost's opaque text brushes are re-derived. Closing hides it;
    the sidebar folds to icons below 860 DIPs; each page opens scrolled to the top.
  - `AnalyticsPage` — three stat tiles (Current streak / Longest streak with their dates, Average
    response "Last 30 days"; the number large, the unit small; each tile's automation name reads it
    all), "Activity" (`ActivityHeatmap`) and "Agents" (`AgentDonut` with the 30-day total in the hole,
    and a legend row per agent: colour, logo, name, count, share). It loads + summarises on a
    background task when shown and when `WaitHistory.Recorded` fires while shown; empty: "Nothing yet.
    Agents show up here once one needs you." (or "No agent needed you in the last 30 days.").
    `ActivityHeatmap` is an owner-drawn `FrameworkElement`: a column per week (the culture's first day
    on top, today in the last column), 26 to 53 weeks to fill the width at ~24 DIPs a column, pitch,
    gap and origin snapped to device pixels; month names over the week holding the 1st, Mon/Wed/Fri,
    "Less … More"; hover or keyboard focus (arrows: a week left/right, a day up/down, Home/End) shows
    "12 tokens on Sat, Nov 8" as a tooltip and as its UIA name. `AgentDonut` draws the ring (2 DIP gaps,
    a hovered segment lifts, tooltip "Codex: 52 (24%)"). `ChartColors`: per theme, a GitHub green ramp
    for the heatmap; agents in Claude's and Pi's brand hues, Codex blue and OpenCode aqua (their marks
    are monochrome, which can't carry identity); ring-adjacent pairs pass a colour-blind check in both
    themes. Charts repaint on `ApplicationThemeManager.Changed`.
  - `SoundAndDisplayPage` — first a preview (260 DIPs high, half dark, half light) where a drawn pointer
    glides across both and the token follows it the chosen way, kept inside the preview
    (`TokenPreview` with `Roam`: Core renderer + `TokenMotion` → WPF bitmap on the UI thread, its own
    renderer); then the master switch, size, position (below / above / beside the pointer, a
    separator, the four corners; picking a pointer position keeps the last corner), Motion (Bouncy /
    Calm / Still; disabled showing Still, with a tooltip, while Windows' animation effects are off),
    and "Reset size and position" (`ResetSizeAndPosition`; icons keep their own reset). Then Sounds:
    "Play sounds", and only while it is on, "Needs you" and "Done" (a play button + the chime box,
    `IControlPanelHost.PlayChime`) and Volume.
  - `CustomizationPage` — a preview of the selected agent's icon ("Done", and "Needs you" with the "?" and a
    "+1"; the two stack when they don't fit across), agent tiles (a `UniformGrid`, 4 per row, 2 when
    narrow), and that agent's gallery: built-ins, "Your icons" (each with its file name; the remove
    "x" shows on hover or keyboard focus, and removing offers Undo), "Add SVG…" (sanitise + validate +
    store on a background task with a ProgressRing; dimmed with a tooltip when the agent has 24), and
    "Reset all icons to the originals". A refusal ("That file couldn't be used" + the reason +
    "<Agent> keeps its current icon.") and Undo show inline under "Your icons", in place of the hint.
    Under the preview, "Animate mascots" (off, disabled, with a tooltip while Motion is Still or
    Windows' animation effects are off); while it applies and the page is visible, the mascot tiles
    step through their loops from `CompositionTarget.Rendering`, each frame drawn once by the page's own
    renderer (`ToolLogo.Render` with a clock) and kept by its `IconTile`.
    Tiles are radio groups (`ChoiceTile` style): Tab reaches the selected one, the arrows move the
    choice, the focus ring shows only after keyboard input; tiles update in place so focus survives a
    change.
  - `AgentsPage` + `ToolRow` — one card per tool (Claude Code, Codex, OpenCode, Pi), all with the
    same anatomy: status Connected / Needs update / Not connected / Can't read its config, buttons
    Connect / Update (+ Disconnect) / Disconnect / Show file, and after a change made this run the
    tool's `AfterChange` step. Also the linger timeout and "Preview on screen" (fakes blocked agents
    on the real overlay).
  - `AppsPage` — at the top, "Show during meetings and screen sharing" (its second line says what
    was detected while a meeting hides the token, from `IControlPanelHost.TokenVisibility`); then the
    hide-while-using list (Remove per row, empty state), an inline "Add an app"
    picker (open windows or a typed file name), Restore the default list.
  - `AboutPage` — version, Check for updates → download → restart (`UpdateService`), What's new
    (release notes via `ReleaseNotesFormat`, a pure, tested Markdown reader), Theme, Start with
    Windows, links.
  - `IControlPanelHost`, `PanelStyles.xaml` (also the `ChoiceTile` / `AddTile` gallery styles),
    `SettingHeader`, `ToolLogo` (a tool's icon as a WPF image; the Agents cards always show the
    original), `PanelAssets`.
- `AgentPipeServer.cs` — the named-pipe server: an async accept loop serving hook and plugin
  connections at once, plugin leases, `LeaseReleased` (see "How it works").
- `AgentHookBridge.cs` — the `hook` verb: normalises a tool's stdin/argv payload → one pipe line;
  **fail-silent** (sub-½ s pipe timeout, errors to `bridge.log`, always exits 0).
- `Services/AgentHookRegistrar.cs` — the one entry point that connects each tool (`Tools`, `GetState`,
  `Register`, `Unregister`, `RefreshConnected`, `PluginDir`), backup once + merge, never clobber.
  `HooksJson.cs` (Claude Code + Codex hooks), `PiExtensions.cs` (Pi `settings.json` `extensions`:
  the absolute `<exe dir>\integrations\pi`), `OpenCodePlugins.cs` + `JsoncEdit.cs` (OpenCode
  `opencode.jsonc` `plugins`: the bare `file:` URL of `<exe dir>\integrations\opencode`, edited by
  splicing text so comments and formatting stay byte-identical). Ours is found by a marker that
  survives a moved exe (`hook --tool <id>`, or a plugin folder whose `package.json` is named
  `agentscursor-<id>`; a folder merely named `integrations\<id>` is the user's), so another copy's entry
  reads as Outdated. The exe path is taken as spelled on disk (`OnDisk`), so a launch with other casing
  doesn't make every connection Outdated. **Configs are written in place** (`File.WriteAllText`, never a renamed temp
  file): Orca's OpenCode overlay `%APPDATA%\orca\opencode-config-overlays\<hash>\opencode.jsonc` is a
  hard link to the user's config, and a rename would break it. Config folders come from the tray's own
  environment (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `OPENCODE_CONFIG_DIR`, `PI_CODING_AGENT_DIR`), so a
  tray started from a terminal inside Orca (whose `CODEX_HOME` and `OPENCODE_CONFIG_DIR` are Orca's)
  would connect, and refresh at startup, Orca's copies instead of the user's.
- `Services/UpdateService.cs` — Velopack `UpdateManager`: check, download, apply-and-restart, release
  notes; updating is unavailable (and says so) for dev builds run from `bin\`.
- `Services/ChimePlayer.cs` — plays the embedded chimes, volume-scaled, off the UI thread (see "Sounds").
- `SingleInstance.cs` (a second launch opens the panel and hands it foreground rights),
  `Autostart.cs` (per-user `HKCU\…\Run` behind the `IStartupEntries` seam; `MigrateLegacy` moves
  WormsCursor's Run value to ours, only if it was on), `Assets/`.

### `integrations/` — the Pi and OpenCode plugins (TypeScript)

`shared/tray-client.ts` (`TrayClient`: the persistent connection, attach + snapshot on every connect,
backoff 1 s → 30 s, never launches anything, nothing before `start()`, sockets and timers unref'd) and
`shared/wire.ts`; `pi/` (`createPiExtension`, all state per registration, never module-level) and
`opencode/` (`OpenCodeController` + the pure `OpenCodeState`; plugin id `agentscursor.opencode`). The
plugins code against structural interfaces (`PiLike`, `CursorContext`); `tests/contracts.ts` checks
them against the installed Pi 1.3 and OpenCode 2.2 SDK types, so drift fails `npm run typecheck`.
`Integrations.props` (imported by the App and the tests csproj) copies the runtime files next to the
exe as `integrations\{shared,pi,opencode}\`, so bin, publish and Velopack ship them; Pi runs them
through jiti and OpenCode (Bun) supplies `@opencode/plugin`, so no `node_modules` ships. Use the
installed SDKs as the event contract (the local OpenCode source checkout can be older). See
`integrations/README.md`.

## Build & run

```powershell
dotnet build AgentsCursor.sln -c Release      # build everything
dotnet run --project src/AgentsCursor.App     # run the tray app (dev)
```

.NET 8 SDK, pinned by `global.json`. The app is `net8.0-windows`, PerMonitorV2 DPI.

To exercise the notifier without a real agent: **Preferences… → Agents → Preview on screen**, or fire a
hook by hand from Git Bash — `echo '{"hook_event_name":"PermissionRequest","session_id":"t","tool_name":"Bash"}' |
AgentsCursor.exe hook --tool claude-code` (the tray must be running). Not from Windows PowerShell 5.1:
its pipe often doesn't reach a WinExe's stdin, so the bridge reads nothing and silently sends nothing.

Tests: `dotnet run --project tests/AgentsCursor.HookTests -c Release` (registrar tests run only on
temp homes via `HomeOverride`, which also ignores `CLAUDE_CONFIG_DIR`/`CODEX_HOME`/
`PI_CODING_AGENT_DIR`/`OPENCODE_CONFIG_DIR`; pipe tests use private pipe names), and in
`integrations/`: `npm test` + `npm run typecheck`. A dev build and the installed app each re-point
the connections at themselves when they start (the startup refresh).

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
- **Every panel page sets `Foreground="{DynamicResource TextFillColorPrimaryBrush}"`.** Text in a
  page inherits from the page, not the window, and WPF-UI's `Page` style gives it a brush whose
  colour is resolved once, so without this the page titles and section headings keep the first
  theme's colour (black after switching to dark).
- **Settings edits go through `LiveSettings.Change`**, touching only the fields being edited; never
  write back a whole copy. UI controls compare against the live value before changing it, so the
  page re-reading the settings into its controls is a no-op.
- Several patterns (settings store, Velopack packaging, delta-on-release) deliberately **mirror the
  sibling PowerLink project**; comments call this out.
- Settings live in `%LocalAppData%\AgentsCursor\` (not next to the exe) so updates don't wipe them.
  JSON load is tolerant; additive fields don't need a schema bump, and an old settings file (with the
  now-removed cursor-theming fields) loads cleanly — the extra fields are ignored.
- `tools/generate-icon.py` builds the tray/app icon; `tools/generate-chimes.py` synthesises the chimes
  (pure standard library; rerun it and commit the WAVs after changing a chime).
