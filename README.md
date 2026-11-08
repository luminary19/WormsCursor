# WormsCursor

<img src="src/WormsCursor.App/Assets/icon.png" width="96" align="right" alt="WormsCursor icon">

A tiny Windows tray utility that shows a **bouncing token when an AI agent is waiting on you**. When
Claude Code or Codex is blocked on your decision or has finished its turn, its
logo appears and bounces — either hanging off your mouse pointer and following it, or pinned to
a screen corner. Reply (or the session ends) and it disappears. It's an ambient, peripheral-vision
nudge: no toast, no extra window to manage.

It runs in the background from the system tray, with no main window.

The [OpenCode 2 plugin](integrations/opencode/README.md) uses an independent shared native helper,
following Pi's cursor extension: the same motion, completion linger, persistent pipe ownership,
and optimized rendering, with the OpenCode logo. Its source and build live in this repository.

> This project began as a *Worms 3D*-style rotating-cursor theme. That cursor-theming engine has been
> removed; the agent-waiting token is now the whole app, drawn as its own transparent overlay — so it
> works **without theming or replacing any of your system cursors**. (The old cursor code lives in git
> history if you want it.)

## The token

When an agent needs you, WormsCursor draws the waiting tool's logo as a bouncing, click-through
overlay. It shows one tool logo, and a small **"+N"** pill when several sessions wait at once:

- **Plain logo**: the agent finished its turn and is waiting for your next prompt.
- **Logo with a yellow "?" badge**: the agent is blocked on you. It needs a permission approval, an
  answer to a question, or an MCP elicitation (OpenCode: a permission or a form). If any waiting
  session is blocked, its logo is the one shown.

Two placements (set on the **Token** page of **Preferences…**):

- **Next to the mouse cursor** — the token hangs off the pointer on a springy pendulum and follows it,
  swinging as you move and settling (with a gentle idle bob) when you stop.
- **Pinned to a screen corner** — the token sits in the corner you choose and bounces in place.

The overlay touches no system state, so there's nothing to restore on exit, and it costs nothing when
nothing's waiting (the animation only runs while at least one agent needs you).

### Stays out of your games

The token stays hidden while a program on the **Hide the token while using** list (the **Apps** page
of **Preferences…**) is in front, whether it runs fullscreen, borderless or windowed. Dota 2
(`dota2.exe`) is on the list by default. **Add a program** offers the programs that are open now, or
takes a typed exe name; **Remove** takes one off. Your agents are still tracked meanwhile. When you switch
away and one is still waiting, the token comes back. While hidden, the token costs no CPU: WormsCursor
learns about focus changes from Windows instead of checking repeatedly. The OpenCode helper follows
the same list.

## How the notifier works

Set it up on the **Agents** page of **Preferences…**: register **Claude Code** or **Codex** there
and set how long a finished notice stays (the master switch is on the **Token** page). Registering writes WormsCursor's
`hook` command into the tool's config — `%CLAUDE_CONFIG_DIR%\settings.json` when that environment
variable is set, otherwise `%USERPROFILE%\.claude\settings.json`; Codex uses
`%CODEX_HOME%\hooks.json`, otherwise `%USERPROFILE%\.codex\hooks.json` — with backup-and-merge, so it preserves
your own hooks. Each event then runs `WormsCursor.exe hook --tool …`, a throwaway process that writes
one line to the running tray app over a named pipe and exits. It's **fail-silent** (<½ s pipe timeout,
errors logged to `bridge.log`, always exits 0), so it can never block or break the agent.

**What it reacts to.** Each tool's lifecycle hooks are normalised to a few events, and the token
tracks whether the ball is in *your* court:

| The agent… | Claude hook | Token |
|---|---|---|
| asks for permission (main agent or a subagent) | `PermissionRequest` | **appears — stays until that agent moves on** |
| asks you a question (main agent or a subagent) | `PreToolUse` of `AskUserQuestion` | **appears — stays until that agent moves on** |
| relays an MCP server's request for input | `Elicitation` | **appears — stays until that request is answered** (`ElicitationResult`) |
| sends the "still waiting for your input" reminder | `Notification` (`idle_prompt`) | **appears — clears after the timeout** |
| finished its turn | `Stop` | **appears — clears after the timeout** |
| ended on an error | `StopFailure` | **appears — clears after the timeout** |
| started on your prompt, or you reopened the session | `UserPromptSubmit` / `SessionStart` | **clears** |
| resumes tool work after you answered | `PreToolUse` / `PostToolUse` / `PostToolUseFailure` | **clears that agent's prompt** |
| exited cleanly (`/exit`, `Ctrl+C`, `/clear`, logout) | `SessionEnd` | **clears at once** |

Every "blocked on you" row comes from a hook that says who is waiting and has a matching "answered"
event. Permission requests and questions carry the asking agent's id, so only *that* agent's next
tool call clears its prompt: a subagent still waiting keeps the token up while its siblings or the
main agent work, and background subagents can't clear the main agent's question. An MCP request for
input belongs to no agent, so only its own `ElicitationResult` clears it. Claude's `Notification`
hook says nothing about which agent it is for, so it is never used for a blocked state.

Codex desktop and CLI use the same shared lifecycle hooks: `Stop` shows completion,
`PermissionRequest` shows approval waits, and user-input tools show question waits.
`UserPromptSubmit`, `SessionStart`, resumed tool work, `Interrupt`, and `SessionEnd` clear the
corresponding state. Asynchronous questions remain pending while Codex continues working; answering
an individual question clears that question, and a fresh ordinary prompt clears the pending set.
Subagent completion hooks are not registered. Codex's hook trust approval applies when enabling
new or changed hook commands. Restart existing Codex desktop and CLI sessions after registering
or changing hook trust so they load the updated configuration.

Codex runs a Windows hook through PowerShell (`powershell.exe -NoProfile -Command …`, or `pwsh`
when installed), so WormsCursor registers its Codex hook as `& '<path>\WormsCursor.exe' hook --tool
codex` in `commandWindows`. A registration from before this fix used a bare quoted path, which
PowerShell rejects, so every Codex hook failed; the **Agents** page shows it as *Update available*.

**Codex under Orca.** Orca runs Codex with
`CODEX_HOME=%APPDATA%\orca\codex-runtime-home\home`. Orca copies your hooks there but trusts only
its own, so WormsCursor's hooks don't run in Orca-launched Codex until you trust them: open Codex
from Orca, run `/hooks`, and trust the WormsCursor entries. Do this once, and again whenever the
hook command changes (after **Re-register**, or when the WormsCursor exe moves).

Codex currently has no failure lifecycle hook for model/API errors, and those errors can skip
`Stop`. Consequently, these failures cannot reliably produce a completion token. Normal completion,
approval waits, and questions use the native lifecycle hooks described above.

So it shows up when an agent is *waiting on you* and clears the moment you're clearly back (you
submitted a prompt) or the session ends. Multiple sessions are tracked independently (keyed by tool +
session id), so the count is simply "how many agents need you right now".

**Two kinds of waiting.** A token isn't all-or-nothing on the timeout:

- **Blocked on you** (a permission request, a question, an MCP request for input, or an unanswered
  Codex question) — the agent is *actively* waiting on your decision, so the token **stays
  indefinitely**. It's never swept by the timeout; it clears only when you actually respond (that
  agent reports work again, or the request is answered), or the turn or session ends.
- **Finished its turn / errored** — the agent is done and just needs a fresh prompt, so the token
  **lingers, then auto-clears** after the **linger timeout** (default 20 s, configurable 10–1800 s).

**Why it's event-driven (and the timeout).** WormsCursor only knows what the hooks tell it over the
pipe — it never polls the agent or watches its process. That keeps it dead-simple and zero-cost when
idle, but it means a *finished-turn* session that never sends a closing event would otherwise wait
forever — so the linger timeout is its backstop. (An approval prompt has no such backstop by design:
the agent really is still blocked on you, so its token persists until you reply.) The tool-call hooks
(`PreToolUse` / `PostToolUse`) clear ordinary blocking waits when work resumes. They do not dismiss
an unanswered asynchronous Codex question.

## Known issues

- **A hard-killed agent's token can linger.** The notifier learns everything from the tool's hooks
  over the pipe — it never watches the agent's process. A *clean* exit (`/exit`, `Ctrl+C`, `/clear`,
  logout) fires `SessionEnd` and the token clears immediately, but if the agent is **killed outright**
  (Task Manager "End Task", closing the terminal window, `kill`) no hook runs, so no "session ended"
  ever reaches WormsCursor. A token left by a **finished turn** is swept by the **linger timeout**
  (default 20 s, set on the **Agents** page). A token left by an **approval prompt** stays
  indefinitely — by design, since an unanswered approval is exactly the case that should persist —
  so to dismiss it, click the tray and toggle the notifier off and on (or just **Exit**).

## Project structure

```
WormsCursor.sln
├─ src/
│  ├─ WormsCursor.Core/      Engine — no UI dependencies
│  │   ├─ NotifierRenderer.cs Draws the token (one tool logo, the "?" badge when blocked, a "+N" pill) at a given centre/size/swing
│  │   ├─ TokenFace.cs        Picks what the token shows: which logo, badge on/off, how many others
│  │   ├─ AttentionBadge.cs   The yellow "?" badge, shared with the OpenCode helper
│  │   ├─ AgentLogos.cs       Baked tool logos (Claude critter, OpenAI knot); SvgPath.cs parses the path data
│  │   ├─ AgentActivity.cs    Tracks which agent sessions need you (UI-free, thread-safe)
│  │   ├─ AgentEventMessage.cs The normalised wire format (one JSON line per event)
│  │   ├─ CursorSettings.cs   Token size / placement / corner / notifier settings / hidden-while-using programs (persisted as JSON)
│  │   ├─ LiveSettings.cs     The one owner of the running settings: every edit is a small Change, saved at once
│  │   ├─ TokenMotion.cs      The token's corner bounce and idle bob, shared by the overlay and the panel preview
│  │   ├─ SettingsStore.cs    Load/save settings in %LocalAppData%\WormsCursor\
│  │   ├─ ProgramSuppression.cs Normalizes program names and matches them against the hidden-while-using list
│  │   ├─ WindowPrograms.cs   Window/process → exe name; the open programs the "Add a program" picker lists
│  │   └─ ForegroundWatcher.cs Event-driven foreground-program tracker (WinEvent hook, no polling)
│  └─ WormsCursor.App/       Tray shell (WinForms tray icon + overlay; WPF control panel)
│      ├─ Program.cs                   Entry point (Velopack + single-instance guard; `hook` verb short-circuits first)
│      ├─ NotifierOverlay.cs          The layered overlay window + bounce animation (cursor / corner)
│      ├─ TrayApplicationContext.cs   NotifyIcon + menu, owns the overlay, pipe server, settings and control panel
│      ├─ ControlPanel/               The WPF-UI control panel: one window, sidebar pages Token / Agents / Apps / About
│      ├─ AgentPipeServer.cs          Named-pipe server that receives normalised agent events
│      ├─ AgentHookBridge.cs          The `hook` verb: normalises a tool's payload → one pipe line, fail-silent
│      ├─ Services/AgentHookRegistrar.cs Writes/removes WormsCursor's hook in each tool's config (backup + merge)
│      ├─ SingleInstance.cs           One instance only; a 2nd launch opens the control panel
│      ├─ Autostart.cs                "Start with Windows" via HKCU\…\Run
│      └─ Services/UpdateService.cs   Velopack check / download / apply updates, release notes
└─ tools/
   └─ generate-icon.py       Builds Assets/Icon.ico (+icon.png) — the app/tray glyph
```

`src/WormsCursor.OpenCodeOverlay/` contains the independent OpenCode helper;
`integrations/opencode/` contains its V2 plugin, installation script, and lifecycle tests.

The engine (`Core`) is deliberately UI-agnostic (GDI+ only): the WinForms overlay and the WPF control
panel's preview draw the token with the same renderer.

## Build & run

Requires the **.NET 8 SDK** (Windows). Open `WormsCursor.sln` in Visual Studio 2022+ and run, or from
a terminal:

```powershell
git clone https://github.com/luminary19/WormsCursor.git
cd WormsCursor
dotnet build WormsCursor.sln
dotnet run --project src/WormsCursor.App
```

A tray icon appears. Right-click it for **Enabled / Preferences… / Exit**; double-click toggles the
notifier on/off. Only one instance runs — launching it again just opens the control panel.

**Preferences…** opens the control panel, a Windows 11 Settings-style window that follows your light
or dark theme. Every change applies the moment you make it. Its sidebar has four pages:

- **Token** — the master switch, a live preview of both looks (done, and the yellow "?" for "needs
  you") on dark and light, the size, and where it shows (next to the pointer, or a screen corner).
- **Agents** — register, update or unregister the hooks for Claude Code and Codex, how long a
  finished notice stays, and **Preview on screen**, which shows pretend agents on the real token.
- **Apps** — the **Hide the token while using** list.
- **About** — the version, **Check for updates**, **What's new**, **Start with Windows**, and links.

Settings are saved to
`%LocalAppData%\WormsCursor\settings.json` and persist across restarts — and across updates, since
they live outside the app folder.

To try the token without a real agent, use **Agents → Preview on screen**, or fire a hook by hand
(the tray must be running):

```powershell
'{"hook_event_name":"PermissionRequest","session_id":"test","tool_name":"Bash"}' | WormsCursor.exe hook --tool claude-code
```

### Standalone build (no .NET install on the target)

```powershell
dotnet publish src/WormsCursor.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

> Note: WinForms and WPF don't support trimming/NativeAOT, so a self-contained build is large (about
> 170 MB unpacked, about 75 MB as the Velopack package).
> A framework-dependent build is tiny but needs the *.NET 8 Desktop Runtime* installed.

## Releases

Installers and standalone builds are produced by [Velopack](https://velopack.io): pushing a `v*` tag
runs the release workflow (`.github/workflows/release.yml`), which publishes a **`Setup.exe`**
installer, a **`Portable.zip`** standalone, and delta packages to the repo's GitHub Releases. The app
updates from these releases when you ask it to: **Preferences… → About → Check for updates** finds a
newer version, downloads it, and restarts into it. A copy built from source can't update itself, and
the About page says so.

## License

[MIT](LICENSE) © 2026 Dawid Wenderski
