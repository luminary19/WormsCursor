# OpenCode cursor helper

This standalone helper retains the Pi cursor extension's native overlay lifecycle,
spring/bob motion, and rendering implementation. OpenCode's plugin owns session
attention state; each plugin instance keeps one persistent pipe connection, and
the helper combines those clients into one cursor token.

- Executable: `OpenCodeCursorOverlay.exe`.
- Default pipe: `OpenCodeCursorOverlay-v1` (per-user singleton and pipe access).
- Input: newline-delimited JSON, for example
  `{"command":"state","display":"waiting","size":64}`.
- Displays: `hidden`, `waiting`, `completion`; waiting takes priority over
  completion. The largest visible client's size is used, clamped to 24-256 pixels.
- Disconnect removes only that client's state. The last disconnect exits the
  helper after 750 ms; an unclaimed helper exits after 5 seconds.
- `--pipe NAME` selects a separate pipe for isolated tests (ASCII letters,
  digits, hyphens, underscores, and dots; at most 128 characters).
- The token stays hidden, with the frame clock stopped, while a program on
  WormsCursor's "Hide the token while using" list is in front. The list comes
  from the tray app's `%LocalAppData%\WormsCursor\settings.json`. If that file is
  missing, the default list (`dota2.exe`) applies. `FocusSuppression.cs` uses
  WormsCursor.Core's `ForegroundWatcher`, `SettingsStore` and
  `ProgramSuppression`, compiled in as linked source.

The animation clock stops while hidden. Visible frames use monitor-refresh
pacing, a high-resolution waitable timer with fallback, coalesced UI callbacks,
bounded/interpolated spring simulation, a persistent DIB surface, and a cached
supersampled logo. No font installation is needed for the OpenCode mark.

Optional `OPENCODE_CURSOR_PROFILE=1` retains Pi's frame diagnostics, writing
`opencode-cursor-profile-PID.log` to the temporary directory. Profiling explicitly
enables a diagnostic write timer; normal hidden operation has no animation timer.

The geometry and colors come from the local OpenCode upstream checkout at commit
`0a7cb20e662e115438f56831d09ea96efff171cf`:

- `packages/ui/src/components/logo.tsx`, `Mark` (16 x 20 geometry).
- `packages/console/app/src/asset/brand/opencode-logo-dark-square.svg` (brand
  colors `#F1ECEC` and `#4B4646`), preserved verbatim in `Assets/`.

Only the logo drawing, process/pipe identity, and profile names differ from the
Pi native reference. The logo retains the small contrast rim and shadow used by
Pi so it stays readable against both light and dark applications.
