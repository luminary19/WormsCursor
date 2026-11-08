# OpenCode cursor notifications

A native OpenCode 2.2 server plugin with the same cursor-following behavior as
the Pi extension. A 64px OpenCode mark appears while a question/form or permission
request needs attention, and lingers for 30 seconds when a root session finishes.
Starting work clears that session's completion. Child-session completions are
silent; their questions and permission prompts still need attention.

All OpenCode locations share one independent Windows helper. Each plugin scope
holds one persistent named-pipe connection and sends its aggregate display only
when it changes. The helper removes a disconnected scope immediately and exits
750ms after its last client leaves. Helper crashes reconnect with bounded backoff
and replay the latest state. Closing or reloading a plugin cancels its subscription,
timer, and pipe; it does not kill the helper used by other locations.

The native renderer retains Pi's spring, high-resolution refresh-rate clock,
coalesced UI frames, cached logo, and reusable DIB surface. Its frame clock stops
while hidden. It uses the bundled OpenCode logo instead of Pi's mark.

Like the WormsCursor tray token, the helper stays hidden while a program on the
**Hide the token while using** list in WormsCursor's Preferences is in front.
Dota 2 is on that list by default.

## Install

Run from the WormsCursor checkout on Windows with Node 24 and the .NET 8 SDK:

```powershell
powershell -File .\integrations\opencode\Install.ps1
opencode reload
opencode plugin list
```

`Install.ps1` uses `OPENCODE_CONFIG_DIR`, or the normal user OpenCode config
directory. Pass `-ConfigPath C:\path\opencode.jsonc` to select a specific config.
It builds the helper from this checkout, backs up an existing config, and inserts
the plugin while preserving JSONC comments, existing plugins, and options. Repeated
registration is harmless. Invalid configuration is reported without replacing it.

If OpenCode is managed by Orca, the running server reads Orca's generated copy of
the user config under `%APPDATA%\orca\opencode2-config-overlays\<hash>`, not your own
config. Orca does not reliably refresh that copy: in practice it kept a two-week-old
config that never picked up the plugin. After installation, delete that overlay folder
so Orca regenerates it from your current config, then open a new OpenCode agent in Orca
(or run `opencode reload` in an existing one). Do not edit the generated files directly.

Codex launched from Orca also has its own hook trust, separate from your normal Codex.
See "Codex under Orca" in the main [README](../../README.md#how-the-notifier-works).

The registered `plugins` entry points directly to this directory, for example:

```jsonc
{
  "plugins": [
    {
      "package": "file:///C:/path/to/WormsCursor/integrations/opencode",
      "options": { "enabled": true, "tokenSize": 64, "lingerSeconds": 30 }
    }
  ]
}
```

OpenCode 2.2 requires a configured local plugin **directory**. Its `package.json`
selects `index.ts`; configuring the individual file is rejected by this version.
The host supplies its own `@opencode/plugin` instance. The development dependency
is pinned to 2.2.0 for verification.

Native builds live in `artifacts/opencode/<source-hash>` under WormsCursor. Source
and asset changes produce a new immutable build, so another session's running
executable is never overwritten. There is no helper process launched per event.

## Behavior and verification

This uses V2 `Plugin.define`, `event.data`, `form.created/replied/cancelled`,
`permission.asked/replied`, and session status events. The server event stream is
an in-process plugin subscription; terminal reconnections do not recreate it.
The plugin observes its location's sessions after it loads. Existing idle sessions
do not produce completion popups just from loading the plugin. A terminal
subscription failure releases its native resources and reports one error.

```powershell
npm --prefix .\integrations\opencode ci --ignore-scripts
npm --prefix .\integrations\opencode run typecheck
npm --prefix .\integrations\opencode test
```

Tests cover concurrent requests and sessions, completion expiry and deduplication,
child-session filtering, location ownership, cleanup during asynchronous work,
pipe replay and coalescing, JSONC preservation, and the real native helper's
multiple-client lifetime. Runtime loading was also checked against OpenCode 2.2.0
using a private server and `plugin.list`, which reported `local.opencode-cursor`
as active.

Remove this plugin's entry from the config and run `opencode reload` to uninstall.
The last disconnected helper exits automatically.
