# Releasing WormsCursor

WormsCursor ships as a [Velopack](https://velopack.io/) application: a single
`Setup.exe`, a `Portable.zip`, and an update feed published as **GitHub
releases** on <https://github.com/dawidope/WormsCursor>. The app updates itself
in place via the tray's **Check for updates…** item (see
`src/WormsCursor.App/Services/UpdateService.cs`).

This mirrors how the sibling PowerLink project releases, minus the
shell-extension / junction / MSIX machinery WormsCursor doesn't have.

## Prerequisites

- .NET 8 SDK (pinned by `global.json`).
- The `vpk` CLI: `dotnet tool install --global vpk`.
  Keep it on the **same version** as the `Velopack` PackageReference in
  `src/WormsCursor.App/WormsCursor.App.csproj` (currently **0.0.1298**). The
  runtime library and the packing tool are versioned together.
- For delta packages / publishing: a GitHub token in `GH_TOKEN` (or run
  `gh auth login`) so `vpk` and `gh` don't hit the anonymous API rate limit.

## Versioning

The release version is the git tag minus its leading `v`
(`v0.2.0` → `0.2.0`). Keep `<Version>` in `WormsCursor.App.csproj` roughly in
sync for the dev-build version readout, but the **tag is the source of truth**
for the packed version.

## Option A — automated (recommended)

Push a `v*` tag; the `release` GitHub Actions workflow
(`.github/workflows/release.yml`) builds, packs, and creates the GitHub release.

```sh
git tag v0.2.0
git push origin v0.2.0
```

The workflow:

1. Publishes `WormsCursor.App` self-contained for `win-x64`.
2. Installs `vpk`, downloads the prior stable release (if any) for delta
   computation, and runs `vpk pack`.
3. Uploads `*Setup.exe`, `*Portable.zip`, `*.nupkg`, `releases.win.json`, and
   `assets.win.json` to a GitHub release for the tag.

A tag containing a hyphen (e.g. `v0.2.0-beta1`) is published as a prerelease /
draft so it doesn't become the "latest" auto-update target.

## Option B — local (for testing the payload)

```powershell
# First ever release (nothing to delta against):
pwsh tools/pack.ps1 -Version 0.1.0

# Subsequent releases (build a delta against the latest GitHub release):
$env:GH_TOKEN = "<a github token>"
pwsh tools/pack.ps1 -Version 0.2.0 -DownloadPrior
```

Artifacts land in `velopack-output/`. To publish them by hand you can use
`vpk upload github --repoUrl https://github.com/dawidope/WormsCursor ...` or just
attach the files to a GitHub release manually. The set that must be uploaded for
auto-update to work:

- `WormsCursor-<ver>-full.nupkg` (and `-delta.nupkg` if produced)
- `WormsCursor-win-Setup.exe`
- `WormsCursor-win-Portable.zip`
- `releases.win.json`
- `assets.win.json`

## How auto-update is wired

- `Program.Main` calls `VelopackApp.Build().Run()` first — this handles the
  install / update / uninstall hooks Velopack invokes via `--veloapp-*` args.
- `UpdateService` uses `UpdateManager` + `GithubSource` against the repo URL.
  It **no-ops cleanly for dev builds** (run from `bin\`): `IsInstalled` is false
  there, so `CheckAsync` returns `NotInstalled` and the tray item just opens the
  Releases page instead of trying to self-update.
- The tray's **Check for updates…** item (`TrayApplicationContext.cs`) checks,
  downloads, applies, and restarts, surfacing progress through balloon tips.
