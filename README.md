# WormsCursor

<img src="src/WormsCursor.App/Assets/icon.png" width="96" align="right" alt="WormsCursor icon">

A tiny Windows tray utility that shows a **bouncing token when an AI agent is waiting on you**. When
Claude Code is blocked on your decision (a permission/idle prompt) or has finished its turn, a small
Claude logo appears and bounces — either hanging off your mouse pointer and following it, or pinned to
a screen corner. Reply (or the session ends) and it disappears. It's an ambient, peripheral-vision
nudge: no toast, no extra window to manage.

It runs in the background from the system tray, with no main window.

> This project began as a *Worms 3D*-style rotating-cursor theme. That cursor-theming engine has been
> removed; the agent-waiting token is now the whole app, drawn as its own transparent overlay — so it
> works **without theming or replacing any of your system cursors**. (The old cursor code lives in git
> history if you want it.)

## The token

When an agent needs you, WormsCursor draws the waiting tool's logo as a bouncing, click-through
overlay — one logo for the waiting tool, with a frameless **"+N"** when several agents wait at once.
Two placements (set in **Preferences…**):

- **Next to the mouse cursor** — the token hangs off the pointer on a springy pendulum and follows it,
  swinging as you move and settling (with a gentle idle bob) when you stop.
- **Pinned to a screen corner** — the token sits in the corner you choose and bounces in place.

The overlay touches no system state, so there's nothing to restore on exit, and it costs nothing when
nothing's waiting (the animation only runs while at least one agent needs you).

## How the notifier works

Set it up from **Preferences… → Agent settings…**: register a tool there (currently **Claude Code**),
toggle the token, and set the "clear a stuck logo after" timeout. Registering writes WormsCursor's
`hook` command into the tool's config — `%CLAUDE_CONFIG_DIR%\settings.json` when that environment
variable is set, otherwise `~/.claude/settings.json` — with backup-and-merge, so it never overwrites
your own hooks. Each event then runs `WormsCursor.exe hook --tool …`, a throwaway process that writes
one line to the running tray app over a named pipe and exits. It's **fail-silent** (<½ s pipe timeout,
errors logged to `bridge.log`, always exits 0), so it can never block or break the agent.

**What it reacts to.** Each tool's lifecycle hooks are normalised to a few events, and the token
tracks whether the ball is in *your* court:

| The agent… | Claude hook | Token |
|---|---|---|
| is blocked on you (permission / idle prompt) | `Notification` | **appears** |
| finished its turn | `Stop` | **appears** |
| ended on an error | `StopFailure` | **appears** |
| started on your prompt, or you reopened the session | `UserPromptSubmit` / `SessionStart` | **clears** |
| exited cleanly (`/exit`, `Ctrl+C`, `/clear`, logout) | `SessionEnd` | **clears at once** |

So it shows up when an agent is *waiting on you* and clears the moment you're clearly back (you
submitted a prompt) or the session ends. Multiple sessions are tracked independently (keyed by tool +
session id), so the count is simply "how many agents need you right now".

**Why it's event-driven (and the timeout).** WormsCursor only knows what the hooks tell it over the
pipe — it never polls the agent or watches its process. That keeps it dead-simple and zero-cost when
idle, but it means a session that never sends a closing event would otherwise wait forever. So there's
a backstop: any waiting session with no further events for the **linger timeout** (default 20 s,
configurable 10–1800 s) is swept out of the count. The tool-call hooks (`PreToolUse` / `PostToolUse`)
are deliberately *not* registered — they'd spawn a hook process on every single tool call — so "you
replied" is inferred from your next prompt, not from the agent resuming work.

## Known issues

- **A hard-killed agent's token lingers until the timeout.** The notifier learns everything from the
  tool's hooks over the pipe — it never watches the agent's process. A *clean* exit (`/exit`,
  `Ctrl+C`, `/clear`, logout) fires `SessionEnd` and the token clears immediately, but if the agent is
  **killed outright** (Task Manager "End Task", closing the terminal window, `kill`) no hook runs, so
  no "session ended" ever reaches WormsCursor. The waiting token then stays until the **linger
  timeout** sweeps it (default 20 s, set in *Agent settings…*). This is by design — with no event
  there's nothing to react to, and the timeout is the backstop.

## Project structure

```
WormsCursor.sln
├─ src/
│  ├─ WormsCursor.Core/      Engine — no UI dependencies
│  │   ├─ NotifierRenderer.cs Draws the token (one tool logo + a "+N" badge) at a given centre/size/swing
│  │   ├─ AgentLogos.cs       Baked tool logos (Claude critter, OpenAI knot); SvgPath.cs parses the path data
│  │   ├─ AgentActivity.cs    Tracks which agent sessions need you (UI-free, thread-safe)
│  │   ├─ AgentEventMessage.cs The normalised wire format (one JSON line per event)
│  │   ├─ CursorSettings.cs   Token size / placement / corner / notifier settings (persisted as JSON)
│  │   └─ SettingsStore.cs    Load/save settings in %LocalAppData%\WormsCursor\
│  └─ WormsCursor.App/       Tray shell (WinForms, no main window)
│      ├─ Program.cs                   Entry point (Velopack + single-instance guard; `hook` verb short-circuits first)
│      ├─ NotifierOverlay.cs          The layered overlay window + bounce animation (cursor / corner)
│      ├─ TrayApplicationContext.cs   NotifyIcon + menu, owns the overlay + pipe server
│      ├─ PreferencesForm.cs          Live settings dialog (token preview, size, placement, autostart, updates)
│      ├─ AgentHooksForm.cs           Agent-notifier settings + per-tool hook registration UI
│      ├─ AgentPipeServer.cs          Named-pipe server that receives normalised agent events
│      ├─ AgentHookBridge.cs          The `hook` verb: normalises a tool's payload → one pipe line, fail-silent
│      ├─ Services/AgentHookRegistrar.cs Writes/removes WormsCursor's hook in each tool's config (backup + merge)
│      ├─ SingleInstance.cs           One instance only; a 2nd launch opens Preferences
│      ├─ Autostart.cs                "Start with Windows" via HKCU\…\Run
│      ├─ ChangelogForm.cs            "What's new" dialog (from CHANGELOG.md)
│      └─ Services/UpdateService.cs   Velopack check / download / apply updates
└─ tools/
   └─ generate-icon.py       Builds Assets/Icon.ico (+icon.png) — the app/tray glyph
```

The engine (`Core`) is deliberately UI-agnostic (GDI+ only), so the tray shell could later be swapped
for WPF/WinUI without touching the token/activity logic.

## Build & run

Requires the **.NET 8 SDK** (Windows). Open `WormsCursor.sln` in Visual Studio 2022+ and run, or from
a terminal:

```powershell
dotnet build WormsCursor.sln
dotnet run --project src/WormsCursor.App
```

A tray icon appears. Right-click it for **Enabled / Preferences… / Exit**; double-click toggles the
notifier on/off. Only one instance runs — launching it again just opens **Preferences**.

**Preferences…** opens a live editor with a token preview, a **token size** slider, a **placement**
choice (next to the cursor, or pinned to a screen corner of your choice), a **Start with Windows**
toggle, an **Agent settings…** button, and a **Check for updates** button. Settings are saved to
`%LocalAppData%\WormsCursor\settings.json` and persist across restarts — and across updates, since
they live outside the app folder.

To try the token without a real agent, use **Agent settings… → Preview**, or fire a hook by hand
(the tray must be running):

```powershell
'{"hook_event_name":"Notification","notification_type":"permission_prompt"}' | WormsCursor.exe hook --tool claude-code
```

### Standalone build (no .NET install on the target)

```powershell
dotnet publish src/WormsCursor.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

> Note: WinForms doesn't support trimming/NativeAOT, so a self-contained build is several tens of MB.
> A framework-dependent build is tiny but needs the *.NET 8 Desktop Runtime* installed.

## Releases

Installers and standalone builds are produced by [Velopack](https://velopack.io): pushing a `v*` tag
runs the release workflow (`.github/workflows/release.yml`), which publishes a **`Setup.exe`**
installer, a **`Portable.zip`** standalone, and delta packages to the repo's GitHub Releases. The app
auto-updates from these releases (tray → **Check for updates…**, or the button in Preferences).

## License

[MIT](LICENSE) © 2026 Dawid Wenderski
