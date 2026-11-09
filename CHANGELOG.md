# Changelog

Notable changes to AgentsCursor (called WormsCursor up to 0.8.2). Roughly follows
[Keep a Changelog](https://keepachangelog.com/); version numbers match the git tags and
GitHub releases. The release workflow pulls the matching section into each release's notes.

## Unreleased

### Added
- **Mascots that move.** Every mascot now has a small idle loop every few seconds: Claude Code waves,
  shakes its party hat while the confetti twinkles, or blinks and sinks behind its ledge to peek;
  Codex's buddy blinks and rolls its googly eyes, Spin twirls and wobbles to a stop, Sleepy's z's drift
  off; OpenCode's buddy blinks its cursor eyes and taps a foot, Brackets flexes, Stack hops; Pi
  marches, its shades glint, and its pie steams. They spend most of the time still, and stand still
  with **Motion** set to Still or Windows' animation effects off. **Animate mascots** on the
  **Customization** page turns them off; the previews and the gallery show them moving. Logos and your
  own icons don't move.
- **Analytics, the control panel's new home page.** See your **current** and **longest streak**
  (days in a row an agent needed you), your **typical response** time over the last 30 days (the
  median time from the yellow "?" appearing to the moment you answered; an approved call's own run
  time never counts, and where an agent doesn't say when you answered, as with a Codex approval, the
  wait isn't timed), a GitHub-style **activity** calendar of up to a year (hover a day for "Needed you
  12 times on Sat, Nov 8"), and a ring of **who needed you** in the last 30 days.
  The tray's double-click, **Preferences…** and a second launch all open it. It reads a small local
  history kept in `%LocalAppData%\AgentsCursor\history\`: one line per token with the agent, needs you
  or done, when it came and went and how it ended. No prompts, projects, paths or session ids, and
  nothing older than a year.
- **Hidden during meetings and screen sharing.** The token now stays out of sight while you present,
  share your screen or are on camera, and comes back when that ends. It goes by what Windows itself
  reports: presentation mode, duplicated displays (a projector), an app capturing the screen through
  Windows' capture API, or the camera. The microphone alone doesn't count, so Discord, game voice chat
  or dictation holding it for hours never hides the token. While it is hiding the token, **Apps** says
  what it noticed (say, *Zoom is using the camera*). Turn on **Show during meetings and screen
  sharing** at the top of **Apps** to keep the token anyway. The checks run only while a token would be
  on screen, and cost next to nothing.
- **Sounds, if you want them.** **Play sounds** on the **Sound and Display** page plays a short, soft
  chime when an agent starts waiting on you: one for **Needs you**, another for **Done**, each picked
  from four built-in chimes (or none), with a play button to hear it and its own **Volume** (the system
  volume is left alone). Off by default. A burst of agents finishing together is one chime, and no
  chime plays while the token is hidden.
- **Calmer motion.** **Motion** on the **Sound and Display** page picks how the token moves:
  **Bouncy** (as before), **Calm** (a softer spring, half the swing and hop) or **Still** (it just
  follows the pointer, or sits in its corner). With Windows' animation effects off it is always Still.
  A token that isn't moving isn't redrawn, and a Still token in a corner uses no CPU at all.
  **Reset display** (was *Reset size and position*) now puts the motion back too.
- **Pick each agent's look, mascots included.** The new **Customization** page lets every agent wear its own
  logo, one of three mascots drawn for it, or a picture of your own. Claude Code can wave, party or
  play peekaboo; Codex comes as a googly-eyed buddy, mid-spin or dozing off; OpenCode's mark gets a
  face, bracket arms or a stack of windows; Pi strolls, wears 8-bit shades or turns into a whole
  pie. **Add SVG…** takes your own file (up to 256 KB, 24 per agent): it's cleaned before it's kept,
  so nothing in it can run or fetch anything from elsewhere, and if it can't be used the page says why
  and the agent keeps its icon; removing one of your icons can be undone. The status clouds and the "+N" sit on every icon, and the page previews
  both looks over dark and light. **Reset all icons to the originals** puts everything back.
- **Above or beside the pointer, not just below.** **Position** now offers **Below the pointer** (the
  hanging token you know), **Above the pointer** (floating like a balloon on a string) and **Beside
  the pointer** (trailing off the arrow like a tooltip), plus the four corners. Near the edge of a
  screen the token flips to the other side or slides along the edge, so it never leaves the screen,
  and it never gets clipped however hard it swings. The **Sound and Display** page's preview now shows a pointer
  gliding about with the token following it the way you chose.
- **Pi and OpenCode, in the same token.** The **Agents** page now connects **OpenCode** and **Pi**
  next to Claude Code and Codex, with the same card for all four. Their plugins ship with AgentsCursor
  (`integrations\` next to the exe), and they report into the one tray, so Pi and OpenCode get
  everything the others have: the yellow **"?"** when they need your answer (an OpenCode permission or
  form; in Pi, any prompt an extension shows during a run, such as a permission or a question), the
  green check when a turn is done, the "+N", the linger timeout, the on/off switch and the
  hide-while-using list. A subagent's prompt shows on its parent conversation. When Pi or OpenCode
  quits, or is killed while waiting on you, its token clears at once. The plugins never start
  AgentsCursor: if it isn't running they wait quietly and catch up when it starts. Pi needs version
  1.3 or later; the plugin replaces the standalone pi-cursor extension, so remove that one.
- **Connections stay current by themselves.** When AgentsCursor starts, it updates in place every
  connection you made that points at another copy of it or an older setup (a moved app, the rename
  from WormsCursor, a dev build), and leaves the tools you didn't connect alone. If a tool then needs a
  step of its own, a notification says so and its card shows it, for example approving the changed
  Codex hooks under `/hooks` (also in Codex sessions Orca starts).
- **A new control panel.** **Preferences…** now opens one window in the style of Windows 11
  Settings: Mica, rounded cards, switches, and it follows your Windows light or dark mode as it
  changes, or stays light or dark if you pick that under **About → Theme**. A sidebar holds six pages:
  **Analytics** (the home page, above), **Sound and Display** (the on/off switch, size, position, motion, sounds and a live animated
  preview), **Customization** (each agent's look), **Agents** (Connect / Update / Disconnect for each tool with a clear status, how long the
  token stays after an agent is done, and **Preview on screen**), **Apps** (the apps that hide the
  token, with their icons and names when they're open) and **About** (version, updates, *What's new*,
  the theme, *Start with Windows*, links).
  Every change applies the moment you make it, so there is no OK, Apply or Cancel, and an edit in one
  place can never undo one made elsewhere (the tray's **Enabled**, say). It replaces the separate
  Preferences, *Agent settings*, *Add a program* and *What's new* windows, works fully from the
  keyboard, and stays sharp at any display scale. On a narrow window the sidebar folds to icons, with
  a menu button to unfold it.
- **Check for updates works.** **About → Check for updates** looks for a newer release, downloads it
  and restarts into it. A copy built from source says that updates come with the released app.
- **The token stays out of your games.** While a program on the new **Hide the token while using**
  list (**Preferences… → Apps**) is in front, the token stays hidden. This works whether the app
  runs fullscreen, borderless or windowed. Dota 2 is on the list by default. **Add an app** offers
  the apps that are open now, or takes a typed exe name, and **Remove** takes one off. Your agents are
  still tracked meanwhile, and the token comes back when you switch away while one is still waiting.
  While hidden it uses no CPU: AgentsCursor learns about focus changes from Windows instead of checking
  repeatedly.

- **You can tell "it needs an answer" from "it's done".** When an agent is blocked on you (a
  permission prompt, an `AskUserQuestion`, an MCP elicitation, a Codex approval or question, or an
  OpenCode permission or form), its logo now wears a fluffy yellow cloud with a **"?"** at the
  top-right. When it simply finished its turn, the cloud is green with a **check**. When several
  sessions wait, one that is blocked on you is the logo shown. This works for Claude Code, Codex,
  OpenCode and Pi alike, and the clouds are drawn as vector shapes, so they look the same on every
  machine.

### Changed
- **Type an exact token size.** Double-click the size (for example "96 px") on the **Sound and Display** page to
  type a number; Enter or clicking away applies it (kept within 48-192 px), Esc cancels.
- **Double-click the tray icon to open the control panel.** It used to switch the token on and off;
  that stays one right-click away as **Enabled**.
- **A quieter control panel.** The pages drop their explanatory lines, so each setting is just its
  name and its control. The timing setting now reads **Time until a finished agent's token clears**.
- **WormsCursor is now AgentsCursor.** The new name says what it does: it shows when your AI agents
  need you. The app, its exe (`AgentsCursor.exe`), its tray icon tooltip and its settings folder
  (`%LocalAppData%\AgentsCursor\`) all carry the new name. What you had carries over: until
  AgentsCursor saves settings of its own, it reads them from `%LocalAppData%\WormsCursor\` (the old
  folder is left alone), and if WormsCursor started with Windows, AgentsCursor now does instead. Hooks
  connected by WormsCursor are updated in place when AgentsCursor starts, not added twice (for Codex,
  approve the changed hooks with `/hooks`). The GitHub repository keeps its old name, so links and
  updates still come from the same place.
- **The OpenCode plugin's own helper and settings are gone.** OpenCode used to draw its token with a
  separate helper program and had its own size, linger and on/off options; it now reports to
  AgentsCursor like the other agents, and all of that comes from AgentsCursor's settings (its linger
  is AgentsCursor's, 20 s by default, instead of the plugin's 30 s). An OpenCode config that still
  lists the old plugin shows as *Needs update* and is updated when AgentsCursor starts.
- **The "+N" count is now a small pill in the logo's top-right corner.** It tucks in beside the
  status cloud, so the two never overlap. That corner faces the cursor, so the count can't swing out
  of view. In a top screen corner the token now sits a little further from the edge, which leaves
  room for the cloud while it bounces.
- **It's now just the agent-waiting notifier — the cursor theming is gone.** The token that
  shows when an AI agent needs you no longer rides on a themed system cursor, so you don't have to
  enable any of the *Worms*-style cursors to see it. It's drawn by its own transparent, click-through
  overlay (true per-pixel alpha via `UpdateLayeredWindow`) that touches no system state — nothing to
  restore on exit, and it's invisible/zero-cost when no agent is waiting.
- **The token bounces, and you choose where.** On the **Sound and Display** page pick a position next to the
  pointer (it follows on a springy string, with a gentle idle bob) or a **screen corner** (it sits in
  the corner you choose and bounces in place). Plus a **size** slider and a live preview.
  **Reset display** puts those back. Settings saved by this version name the
  position differently, so an older AgentsCursor reading them starts from its defaults. The DPI of the monitor it's shown on is honoured, so the token keeps a
  consistent size across mixed-DPI setups — which also retires the old mixed-DPI cursor flicker.
- **The bounce is fluid.** The token animates on a dedicated high-resolution thread
  (`timeBeginPeriod(1)` + real elapsed delta-time) instead of a coarse UI timer, so the motion is
  smooth and time-correct even while the control panel is busy — no stutter.
- **The token clears the instant the agent resumes, not at the timeout.** AgentsCursor now registers
  Claude's `PreToolUse`/`PostToolUse` hooks, so approving a permission prompt (which fires no
  `UserPromptSubmit`) clears the token as soon as the agent does its next thing, instead of lingering
  until the linger timeout. The trade-off is one short fail-silent hook process per tool call during an
  active turn. **Update the registration on Preferences… → Agents** ("Needs update" → **Update**)
  and restart your agent session to pick up the new events.

### Fixed
- **A failed update no longer closes AgentsCursor.** If a download fails its checksum or another update
  is already running, **About** now says so and offers **Try again**, instead of the tray icon and the
  token disappearing.
- **Claude Code hooks work on Windows without Git Bash.** Claude Code runs a hook command through
  PowerShell there, which rejected the command WormsCursor registered, and a failing hook blocked
  every tool call. The hooks now start AgentsCursor directly, with no shell. The **Agents** page shows
  Claude Code as *Needs update*: **Update** it and restart your Claude Code sessions.
- **An approval stays on screen until you answer it.** When Claude ran several tools at once and one
  needed your approval, another one finishing first cleared the token while Claude was still waiting
  on you. Each approval now clears only when its own tool runs, or when Claude moves on after you
  deny it.
- **Codex hooks work on Windows again.** Codex runs Windows hook commands in PowerShell, which
  rejected the command WormsCursor registered (a quoted exe path followed by arguments), so every
  Codex hook failed and the token never showed for Codex. The command now uses PowerShell's call
  operator. The **Agents** page shows Codex as *Needs update*: **Update** it, approve the
  changed hooks in Codex (`/hooks`), and restart open Codex sessions.
- **The token knows when an agent is blocked on you, and who asked.** Every "waiting for your
  answer" state now comes from a Claude Code hook that says which agent asked and has a matching
  "answered" event: permission requests (`PermissionRequest`), questions (`AskUserQuestion`), and MCP
  server requests for input (`Elicitation` / `ElicitationResult`). A question from a subagent no
  longer gets filed under the main agent, and a question from the main agent is no longer cleared by
  background subagents that keep working. Claude's `Notification` hook is now used only for its
  "still waiting for your input" reminder. The **Agents** page shows Claude Code as *Needs
  update* (it also does when the registered hook points at a different exe, such as
  `WormsCursor.exe`): **Update** it to pick this up.
- The token no longer pops up when Claude Code hands work to background subagents. Spawning them
  ends Claude's turn at once, but it picks back up by itself when they report in, so it isn't your
  move yet. The token now waits for the real finish.
- A permission prompt in one subagent is no longer cleared by another subagent that keeps working.
  Approvals are now tracked per agent, and Claude Code's come from its `PermissionRequest` hook.
  **Update Claude Code** on the **Agents** page (it shows *Needs update*) to pick this up.
- The tray app no longer crashes when the control panel's preview and the live token draw at the
  same time, and a failed animation frame is logged to `bridge.log` and retried instead of ending the app.
- A read-only or locked `settings.json` no longer crashes the app. A settings file that doesn't parse
  is no longer overwritten with defaults: before AgentsCursor next saves, it copies the file to
  `settings.json.corrupt-<time>`, and if that copy fails it doesn't save at all. Trailing commas and
  `//` comments in it are now accepted.
- Register / Unregister keeps your own hook commands even when they share a matcher group with
  AgentsCursor's. The backup (`.agentscursor.bak`, or the `.wormscursor.bak` WormsCursor already
  made) always holds your original file, and a malformed Claude Code `settings.json` shows *Can't
  read its config* instead of being partly overwritten.
- *Start with Windows* only shows as on when the startup entry launches this copy of AgentsCursor, so
  an entry left by an old or dev build no longer hides behind a switch that looks on.
- `AgentsCursor.exe register|unregister` now exits non-zero and says why when it fails (including an
  unknown `--tool`).

### Removed
- The whole cursor-theming engine: the rotating arrow/hand, the animated busy/help/crosshair/
  I-beam/resize/move/unavailable cursors, click & typing feedback, the preview grid + per-cursor
  toggles + Showtime, and the `WormsCursor.Preview` showcase tool. The hook → pipe plumbing, the tray,
  autostart and single-instance are unchanged.

## 0.8.2 - 2026-06-16

### Fixed
- **The agent notifier now registers its hooks where Claude Code actually reads them.** If you
  run Claude Code with a `CLAUDE_CONFIG_DIR` set (a custom config location), WormsCursor was
  writing its lifecycle hooks into `~/.claude/settings.json` — a file Claude never reads in that
  setup — so the waiting-agent logo never appeared. Registration now targets
  `%CLAUDE_CONFIG_DIR%\settings.json` when the variable is set, falling back to
  `~/.claude/settings.json` otherwise, and the **Agent settings…** status reads the same file.
  Re-register from **Preferences → Agent settings…** to move an existing registration to the
  right place.

## 0.8.1 - 2026-06-15

### Fixed
- **The agent logo now clears on `/clear` and `/exit`.** If the Claude Code hooks had been
  registered by an older build (before the session-lifecycle events were added), ending a
  session left the logo hanging until the linger timeout instead of clearing at once.
  **Preferences → Agent settings…** now detects an out-of-date registration and shows
  **"Update available" → "Re-register"**; refreshing it restores the full event set so the
  logo disappears the moment a session ends.

## 0.8.0 - 2026-06-15

### Added
- **Agent notifier — a waiting AI agent's logo on your cursor.** When an AI coding agent
  (Claude Code) is waiting on you, the cursor sprouts a small tool-logo charm that hangs and
  swings on the same pendulum as the busy ring / help "?", with a frameless **"+N"** when more
  than one agent waits at once — an ambient, peripheral-vision nudge, no toast and no extra
  window. Set it up in **Preferences → Agent settings…**: register Claude Code (WormsCursor
  writes a `hook` into `~/.claude/settings.json` with backup-and-merge, never overwriting your
  own hooks), toggle the logo, and choose how long a stuck logo lingers before it clears itself.
  The logo appears when the agent is blocked on you (a permission/idle prompt) or finishes its
  turn, and clears the moment you reply or the session ends — a hard-killed agent's logo lingers
  until the timeout, since no exit hook fires.

### Changed
- **Lower idle CPU on the animated cursors.** The engine now skips its per-frame work when
  nothing's happening: a settled resize / I-beam / move / "unavailable" cursor reuses a cached
  frame instead of re-compositing, and the spring/clock physics is skipped entirely when an idle
  pointer sits on a still mouse — plus a pass that cut per-frame allocations and Win32 calls across
  the whole render path. Purely internal; the cursors look and animate exactly as before.

## 0.7.3 - 2026-06-13

### Changed
- **The I-beam typing bounce has its own on/off toggle.** "Click feedback" now covers just
  the pointer + crosshair squash & pop; the text I-beam's hop/shiver is a separate checkbox so
  it can be turned off on its own. Typing feedback is also calmer — a gentler hop and a small
  rate limit between keystrokes — so typing fast no longer "recoils" like a machine gun. The
  keyboard hook behind it is now installed only while the I-beam toggle is on.

## 0.7.2 - 2026-06-13

### Added
- **"What's new" changelog in the app**: a *What's new* link in Preferences opens a dialog
  that lists the published releases (newest first) and shows the selected version's notes,
  fetched from the GitHub Releases API (each release body is its CHANGELOG section). The
  notes get light markdown formatting; if the fetch fails (offline / rate-limited / dev
  build) it falls back to a *View on GitHub* button. After an update, the new version's
  notes pop up once automatically.

## 0.7.1 - 2026-06-13

### Added
- **Per-cursor on/off in Preferences**: every tile in the cursor preview now has a
  checkbox (all ticked by default). Untick one to leave that cursor as the Windows
  default instead of theming it — WormsCursor simply stops touching that slot. Disabled
  tiles are shown faded so it's clear what's off. **Showtime** cycles only the enabled
  cursors, and the setting persists with the rest of your preferences.
- **Click feedback** (Preferences, on by default): the pointer and the crosshair do a
  quick "squash & pop" while a mouse button is held — a tactile press-and-release — and
  the text I-beam hops and shivers slightly side-to-side as you type. The effects also
  play in the Preferences demo (Test cursor / hover-to-try / Showtime). One checkbox
  toggles it. Mouse
  state is polled (no mouse hook); the
  typing hop uses a low-level keyboard hook that reads only *that* a key fired (never
  which — nothing is logged), installed only while the feature is on.

## 0.7.0 - 2026-06-13

### Added
- **Showtime mode in Preferences**: a hands-free demo for recording. After a 3-2-1
  lead-in (time to start your capture) it forces each test cursor on screen in turn
  (1.5s each), looping until you click **Stop** or close the dialog — so the whole set
  can be recorded reacting to mouse movement without clicking through the *Test cursor*
  combo by hand. Move the mouse while it runs so the motion-driven animations play; the
  dropdown follows along (disabled) to label the live cursor, and the button doubles as
  Stop.

## 0.6.1 - 2026-06-03

### Added
- **Wider, two-column Preferences layout**: the cursor preview is now a **7×2 grid** (matching
  the README sheet) instead of 5 columns, and the controls below sit in two columns — sliders
  on the left, colours + the *Test cursor* combo on the right — so the window is wide-and-short
  rather than a tall stack. The colour buttons now show their hex value in a contrasting ink so
  a black/white swatch still reads as a colour picker.
- **Hover-to-try in the Preferences preview**: moving the mouse over a cursor tile borrows
  that cursor onto your real pointer (live), and the tile empties to a dashed pocket so it's
  clear where it went. Moving off the grid hands the pointer back to whatever the *Test
  cursor* combo has selected. Like the *Test cursor* combo, the on-screen cursor reflects the
  currently **applied** appearance — click **Apply** to preview unsaved size/colour edits.

### Fixed
- The Preferences window now rescales when dragged between monitors with different display
  scaling (e.g. 150% ↔ 200%). It's a hand-coded form that never set `AutoScaleMode`, so
  WinForms didn't re-scale it on a DPI change (despite the app being PerMonitorV2) and it
  looked cut off on the other monitor; it now uses `AutoScaleMode.Font`.

### Known issues
- **Animated cursors flicker on mixed-DPI multi-monitor setups** (e.g. a 125% screen next to a
  150% one). The animated cursors re-install via `SetSystemCursor` every frame; on a monitor
  whose scale differs from the one the cursor was created for, Windows re-composites the global
  cursor on each swap, which flashes. Static cursors (the rotating arrow/hand at rest) are
  unaffected. Workaround: match both monitors' scale, or enable Pointer trails (shortest). A
  proper fix needs the animated cursors drawn in our own overlay instead of `SetSystemCursor`.

## 0.5.0 - 2026-06-03

### Added
- **Crosshair / precision cursor** (`OCR_CROSS`): a reticle — centre dot, four axis ticks
  and a slowly-rotating broken ring — whose ticks "breathe" and spread outward (recoil)
  when you move fast, then settle.
- **Text / I-beam cursor** (`OCR_IBEAM`): a flexible beam — the bottom is rigid but the
  top sways opposite to motion on a soft underdamped spring, so it wobbles like jelly as
  you move and settles afterwards.
- **Resize cursors** (`OCR_SIZEWE` / `SIZENS` / `SIZENWSE` / `SIZENESW`) and the **move
  cursor** (`OCR_SIZEALL`): stretched-taffy double-arrows — drag along the axis and the
  shaft necks thin while the heads fly apart on an underdamped spring, then blob back. Move
  crosses a horizontal and a vertical taffy arrow into a 4-way glyph whose arms stretch with
  motion along each axis.
- **Unavailable cursor** (`OCR_NO`): a red circle-with-slash whose ring is a jelly blob — it
  deforms into an egg along the direction of travel and wobbles back to round when you stop.
- **Alternate-select** (`OCR_UP`) is themed with the same rotating arrow as the normal pointer.
  WormsCursor now covers **all 14 standard system cursors**.
- **`WormsCursor.Preview`** project: renders a labelled showcase of every themed cursor to
  PNG (a dark sheet plus a transparent one) for the README / docs, used in the new
  **Cursors** section of the README.

### Changed
- Preferences preview now shows **every** cursor in a single neutral 5-column grid, drawn at
  **real size (never scaled)**; the window is sized once to fit and the render is debounced so
  dragging the size slider stays smooth (was a side-by-side dark/light strip of just arrow + hand).
- The help cursor's **"?"** is now hand-drawn — a rounded hook plus a separate round dot —
  instead of a font glyph, for cleaner rounding and a correctly-aligned, smaller dot.

### Fixed
- Animated cursors (busy, app-starting, help, crosshair, text) now actually animate while
  on screen in real apps, not just under the Preferences "Test cursor". `SetSystemCursor`
  destroys the handle you pass it, so the on-screen-detection check was comparing against
  a dead handle and never matched; it now matches the live system handle (`LoadCursor`).

## 0.4.0 - 2026-06-02

### Added
- **Busy / progress cursors** are now themed to match. *App-starting* (`OCR_APPSTARTING`)
  is the rotating arrow plus a spinning comet ring that hangs off its tail on a springy
  string under gravity — it swings out as you move and settles straight down when you
  stop. *Wait* (`OCR_WAIT`) is the same ring centred on the pointer (spin only, no
  physics, so you can always see where you're pointing). They animate only while actually
  on screen (checked via `GetCursorInfo`), so an idle tray uses no CPU.
- The **help cursor** (`OCR_HELP`) is themed too: the rotating arrow with a "?" that hangs
  upside-down off the tail on the same pendulum string, swinging to the sides as you move.
- Preferences: a **Test cursor** control that forces a chosen cursor (arrow, hand, wait,
  app-starting, help) on screen, so you can preview the animated cursors on demand; it
  clears automatically when the dialog closes.
- Preferences: an **Apply** button that commits edits to the live cursor without closing
  the dialog (so you can tune size/colour and watch the test cursor update). "Check for
  updates" moved down beside the version to make room for it.

## 0.3.0 - 2026-06-02

### Added
- The **hand / link cursor** (`OCR_HAND`) now rotates to follow movement too. It's drawn
  the same way as the arrow — a filled silhouette plus a pen-stroked outline, finger
  separators and knuckle creases — so it shares the arrow's fill/outline colour, size and
  outline thickness.

### Changed
- Outline thickness now scales every hand line together (outline + finger creases),
  exactly 1:1 with the arrow, and the slider is capped at 4 px (beyond that the lines just
  merge). At thickness 0 the hand is a bare fill, like the arrow.
- Preferences: corner radius is now labelled **arrow only** (it never applied to the hand).

### Removed
- The hand's source SVG and its bake script (`tools/`); `HandShape.cs` is now the baked
  source of truth (recoverable from git history if the shape ever needs regenerating).

## 0.2.1 - 2026-06-02

### Fixed
- Release deltas: `vpk download` now fetches the previous release into the same
  directory `vpk pack` writes to, so updates ship a small delta package instead of the
  full payload every time.

### Changed
- Preferences: the **Fill** and **Outline** colour pickers now share a single row,
  side by side.

### Added
- This CHANGELOG, plus a release step that extracts the matching section into the
  GitHub release notes.

## 0.2.0 - 2026-06-02

### Added
- The arrow icon is embedded in the Velopack installer and updater
  (`Setup.exe` / `Update.exe`) instead of the Velopack default.

### Changed
- Preferences footer split over two lines: version + update status on top, the repo
  link on its own line below (no more truncated URL).

## 0.1.0 - 2026-06-02

Initial release — a Worms 3D-style rotating cursor for Windows, packaged as a tray app.

### Added
- Cursor engine (`WormsCursor.Core`, UI-agnostic): the system arrow rotates to follow
  mouse-movement direction, with jitter-resistant aiming and smoothly animated turns.
- System-tray app (WinForms, no main window): Enabled toggle, double-click to toggle.
- Live Preferences dialog: cursor size, fill/outline colour, outline thickness and
  corner radius, with a side-by-side dark/light preview and a Defaults reset.
- Settings saved as JSON in `%LocalAppData%\WormsCursor\` (persist across restarts and
  updates).
- "Start with Windows" autostart via a per-user `HKCU\…\Run` entry (no admin).
- Single instance only: launching a second time opens Preferences instead of starting a
  duplicate.
- Cursor restored on quit and on crash, and re-checked on every launch (self-heal);
  `tools/RestoreCursor.ps1` as a manual fallback.
- Monochrome arrow app/tray icon (white glyph + black frame), generated by
  `tools/generate-icon.py`.
- Velopack installer + auto-update; "Check for updates" in the tray and in Preferences.
- CI build workflow and a tag-triggered release workflow producing `Setup.exe` and
  `Portable.zip`.
- Demo video embedded in the README, and an MIT license.
