---
name: release
description: Cut a new WormsCursor release — bump the version, roll the CHANGELOG, tag, and push so the GitHub release workflow builds and publishes it. Use when asked to "release", "ship a version", "bump the version", "cut a release", "tag a release", or "publish an update".
---

# Releasing WormsCursor

A release is driven entirely by **pushing a `vX.Y.Z` git tag**. The tag triggers
`.github/workflows/release.yml`, which packages the app with Velopack and publishes a
GitHub Release. There is no manual upload step — push the tag and watch the run.

## Versioning

Semver-ish, matching the existing tags:

- **Feature added** → bump the **minor** (`0.6.1` → `0.7.0`).
- **Fix / docs / packaging only** → bump the **patch** (`0.7.0` → `0.7.1`).
- **Pre-release**: a tag containing a hyphen (e.g. `v0.8.0-rc1`) is published as a
  **draft pre-release** automatically.

The **git tag is the source of truth** for the packed version: `release.yml` derives the
Velopack pack version from the tag (strips the leading `v`). Keep `<Version>` in
`src/WormsCursor.App/WormsCursor.App.csproj` in sync with it (it's the dev-build / in-app
version readout) and commit it as `Release X.Y.Z` — so tag `v0.7.0` ⇄
`<Version>0.7.0</Version>`. That's the established convention (see the `Release …` commits).

## Steps

1. **Pick the version** `X.Y.Z` per the rules above.

2. **Bump the assembly version** in `src/WormsCursor.App/WormsCursor.App.csproj`:

   ```xml
   <Version>X.Y.Z</Version>
   ```

3. **Roll the CHANGELOG** (`CHANGELOG.md`, Keep a Changelog style — one section per tag):
   - Rename the top `## Unreleased` section to `## X.Y.Z - YYYY-MM-DD` (today's date).
   - If there is no `## Unreleased` section, add a new `## X.Y.Z - YYYY-MM-DD` section at
     the top (below the intro) describing what shipped, grouped under `### Added` /
     `### Changed` / `### Fixed` / `### Removed`.
   - The release job extracts THIS section verbatim into the GitHub release notes by
     matching the `## ` header that contains the version string — so keep the header in
     the `## X.Y.Z - DATE` shape and don't let the same version appear in another header.

4. **Sanity-build before tagging** (the tag triggers a real release):

   ```bash
   dotnet build WormsCursor.sln -c Release
   ```

5. **Commit** the bump + changelog together:

   ```bash
   git add src/WormsCursor.App/WormsCursor.App.csproj CHANGELOG.md
   git commit -m "Release X.Y.Z"
   ```

6. **Tag and push** (push the branch first, then the tag):

   ```bash
   git push origin main
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

7. **Watch the workflow**:

   ```bash
   gh run list --workflow=release.yml --limit 3
   gh run watch <run-id>   # optional, follows to completion
   ```

## What the workflow does (`.github/workflows/release.yml`)

Triggered on `push` of tags matching `v*`:

- **build** (windows-latest): `dotnet publish` self-contained `win-x64` → sanity-checks the
  payload (`WormsCursor.exe` + `Assets/`) → installs `vpk` **0.0.1298** (kept in lockstep
  with the `Velopack` PackageReference in the csproj) → downloads the latest prior **stable**
  release into `velopack-output/` so `vpk pack` can compute a **delta** (skipped only when
  there is no prior stable release) → `vpk pack` → uploads the `velopack` artifact.
- **release** (ubuntu-latest): downloads the artifact → extracts the matching `CHANGELOG.md`
  section into `release-body.md` → `softprops/action-gh-release` publishes the GitHub Release
  with `Setup.exe`, `Portable.zip`, the `.nupkg`, and the Velopack `releases.win.json` /
  `assets.win.json`. Tags containing a `-` are marked draft + prerelease.

## Gotchas

- **Tag must equal the csproj `<Version>`.** A mismatch ships a package whose version
  doesn't match the tag people downloaded.
- **Delta integrity:** the workflow aborts if a prior stable release exists but
  `vpk download` fails, rather than shipping a full-only/missing-delta package that could
  brick auto-update on existing installs.
- **Don't reuse a tag.** To redo a release, bump to the next patch instead of retagging.
- The in-app updater is **Velopack**; "Check for updates" (tray + Preferences) pulls from the
  GitHub Releases this workflow publishes. A build run from `bin\` reports as a dev build and
  can't self-update — that's expected.

## See also

- `.github/workflows/release.yml` — the workflow this skill triggers (publish → `vpk pack`
  with a delta vs the prior stable release → GitHub release; notes from the CHANGELOG section).
- `src/WormsCursor.App/Services/UpdateService.cs` + `Program.cs` (`VelopackApp.Build().Run()`)
  — how the in-app updater consumes those releases; it no-ops for dev builds run from `bin\`.
