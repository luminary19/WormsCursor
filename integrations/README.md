# Pi and OpenCode plugins

AgentsCursor's plugins for **Pi** (1.3 or later) and **OpenCode** (2.2, V2 plugins). They tell the
AgentsCursor tray when the agent is blocked on you or has finished its turn, so Pi and OpenCode get
the same token as Claude Code and Codex: the same yellow "?" cloud for "needs your answer", the same
green check cloud for "done", the same "+N", linger timeout, on/off switch and hide-while-using list. All of
that is set in AgentsCursor; the plugins have no settings of their own.

They ship with the app, next to `AgentsCursor.exe`:

```
<app folder>\integrations\
  shared\   tray-client.ts (the pipe connection), wire.ts (the message format)
  pi\       index.ts, controller.ts, package.json
  opencode\ index.ts, controller.ts, state.ts, package.json
```

For the installed app the folder is `%LocalAppData%\AgentsCursor\current\integrations\`. Nothing to
install: Pi runs the TypeScript itself (jiti), and OpenCode (Bun) supplies `@opencode/plugin` to its
plugins, so the folders need no `node_modules`.

## Connecting

Open **Preferences… → Agents** and press **Connect** on the **Pi** or **OpenCode** card.

- **Pi**: adds the absolute path of `integrations\pi` to the `extensions` list in
  `%PI_CODING_AGENT_DIR%\settings.json` (else `%USERPROFILE%\.pi\agent\settings.json`). Then run
  `/reload` in open Pi sessions, or start a new one. This plugin replaces the standalone pi-cursor
  extension, which drew its own token with a helper of its own: remove it from Pi (Pi loads every
  folder under `extensions\`, so move the folder out), or Pi shows two tokens.
- **OpenCode**: adds the `file:` URL of `integrations\opencode` to `plugins` in
  `%OPENCODE_CONFIG_DIR%\opencode.jsonc` (else `%USERPROFILE%\.config\opencode\`, or `opencode.json`
  when only that exists). The edit touches only that entry: comments, trailing commas and formatting
  stay as they were. The file is rewritten in place, never replaced, because Orca's OpenCode config
  overlay (`%APPDATA%\orca\opencode-config-overlays\<hash>\opencode.jsonc`) is a hard link to it, so
  Orca picks the change up too. Then run `opencode reload` in open sessions, or start a new one.

The config folders come from AgentsCursor's own environment, as they do for Claude Code and Codex.
Start AgentsCursor from the Start menu or with Windows, not from a terminal inside Orca: there
`OPENCODE_CONFIG_DIR` (and `CODEX_HOME`) point at Orca's copies.

Each config is backed up once, before its first change, to `<file>.agentscursor.bak`. **Disconnect**
removes only AgentsCursor's entry. An entry that points at another copy of AgentsCursor (a moved app,
a dev build, or this repository's old `integrations/opencode` plugin with its own options) shows as
**Needs update**, and AgentsCursor brings it up to date by itself when it starts.

## How they report

Each plugin host keeps **one connection** to the tray's pipe (`\\.\pipe\AgentsCursor.<user name>`) and
opens it with `{"tool":"pi","event":"attach"}` (or `"opencode"`). The tray ties the sessions reported
over that connection to it, so when the host goes away (quit, reload, or killed while blocked) its
token clears at once. The plugins never start the tray: when it isn't running they retry with backoff
(1 s, doubling to 30 s) and stay quiet, and on every reconnect they replay what is still waiting on
you. After the `attach` line they send AgentsCursor's ordinary events, one JSON line each:
`thinking_started`, `awaiting_user` (with `inputIds`), `input_answered`, `turn_complete` and
`session_end`.

**Pi.** Only an interactive TUI session reports (`ctx.mode === "tui"` with a UI). Subagents that run
inside Pi have no UI, and their approvals appear as prompts in the parent's TUI, so they are covered
there; print, json and rpc runs don't report.

| Pi event | AgentsCursor |
|---|---|
| `agent_start` | `thinking_started` |
| `ui_prompt_start` during a run (any extension prompt: a permission, a question, a confirm) | `awaiting_user`, blocked until… |
| `ui_prompt_end` | …`input_answered` |
| `ui_prompt_start` while idle (from a command you typed) | nothing: you are at the keyboard |
| `agent_settled` | `turn_complete` |
| `agent_settled` with `aborted` (Escape) | `session_end`, like Codex's Interrupt |
| `session_shutdown` (quit, reload, new, resume, fork) | `session_end`, and the connection closes |

**OpenCode.** One connection per plugin scope (location); events from other locations are ignored.
Subagents are child sessions, so everything is reported under the **root** session: one conversation
is one token entry, and a subagent's permission shows on its parent conversation. Each prompt's answer
is sent under the root it opened under.

| OpenCode event | AgentsCursor |
|---|---|
| `session.execution.started`, `session.status` busy/retry (root) | `thinking_started` |
| `permission.asked`, `form.created` | `awaiting_user` under the root, blocked until… |
| `permission.replied`, `form.replied`, `form.cancelled` | …`input_answered` |
| `session.execution.succeeded` / `failed`, `session.status` idle, `session.idle` (root, the first of them) | `turn_complete`, unless a prompt is still open |
| a subagent finishing | nothing |
| `session.execution.interrupted`, `session.deleted`, `session.archived` (root) | `session_end` |
| the same for a subagent | its open prompts are answered |
| `location.shutdown` | `session_end` for every root |

A real `opencode run` ends with `session.execution.succeeded` and emits no `session.status` or
`session.idle`, so the run's own result is what marks the finish. A session whose parent can't be
looked up counts as its own root for prompts, and is never reported as finished.

## Development

```powershell
cd integrations
npm ci --ignore-scripts
npm test            # node --test: the tray client, and the Pi and OpenCode controllers
npm run typecheck   # tsc, including tests/contracts.ts: the plugins' interfaces against the
                    # installed Pi 1.3 and OpenCode 2.2 SDK types
```

The dev dependencies (the two SDKs, TypeScript) are for these checks only; the shipped plugins import
nothing at runtime but Node built-ins and, for OpenCode, the host's `@opencode/plugin`. The C# tests
(`tests/AgentsCursor.HookTests`) cover the tray side: the pipe with concurrent hook and plugin
connections, releasing a closed connection's sessions, and the Pi and OpenCode registration.
