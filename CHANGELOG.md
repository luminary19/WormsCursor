# Changelog

Notable changes to WormsCursor. Roughly follows
[Keep a Changelog](https://keepachangelog.com/); version numbers match the git tags and
GitHub releases. The release workflow pulls the matching section into each release's notes.

## 0.5.0 - 2026-06-02

### Added
- **Crosshair / precision cursor** (`OCR_CROSS`): a reticle — centre dot, four axis ticks
  and a slowly-rotating broken ring — whose ticks "breathe" and spread outward (recoil)
  when you move fast, then settle.
- **Text / I-beam cursor** (`OCR_IBEAM`): a flexible beam — the bottom is rigid but the
  top sways opposite to motion on a soft underdamped spring, so it wobbles like jelly as
  you move and settles afterwards.
- **`WormsCursor.Preview`** project: renders a labelled showcase of every themed cursor to
  PNG (a dark sheet plus a transparent one) for the README / docs, used in the new
  **Cursors** section of the README.

### Changed
- Preferences preview now shows **all** cursors (arrow, hand, busy, app-starting, help,
  crosshair, text) in a 2-row grid on a single neutral background, sized so they grow with
  the cursor-size slider (was a side-by-side dark/light strip of just arrow + hand).
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
