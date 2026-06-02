<#
.SYNOPSIS
  Build a Velopack release for WormsCursor (Setup.exe + Portable.zip + delta).

.DESCRIPTION
  Mirrors the steps the GitHub Actions release workflow runs, so you can produce
  a release locally for testing. It:
    1. Publishes WormsCursor.App self-contained for win-x64.
    2. Installs/uses the `vpk` global tool.
    3. (Optional) Downloads the latest GitHub release for delta computation.
    4. Runs `vpk pack` to produce the installer + update payload.

  Requires the .NET 8 SDK (pinned by global.json) and, for --DownloadPrior,
  a GH_TOKEN env var or `gh auth login` so vpk can reach the GitHub API.

.PARAMETER Version
  SemVer for this release, WITHOUT a leading 'v' (e.g. 0.1.0). Required.

.PARAMETER DownloadPrior
  Pull the latest GitHub release first so vpk can build a delta package.
  Skip for the very first release (there is nothing to delta against).

.EXAMPLE
  pwsh tools/pack.ps1 -Version 0.1.0
  pwsh tools/pack.ps1 -Version 0.2.0 -DownloadPrior
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [switch] $DownloadPrior
)

$ErrorActionPreference = 'Stop'

# Repo root = parent of the tools/ folder this script lives in.
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$RepoUrl    = 'https://github.com/dawidope/WormsCursor'
$PublishDir = Join-Path $RepoRoot 'artifacts/publish'
$OutputDir  = Join-Path $RepoRoot 'velopack-output'
$Csproj     = Join-Path $RepoRoot 'src/WormsCursor.App/WormsCursor.App.csproj'

Write-Host "==> Publishing WormsCursor.App (self-contained win-x64)…" -ForegroundColor Cyan
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
dotnet publish $Csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# AssemblyName is 'WormsCursor', so the published entry point is WormsCursor.exe.
$mainExe = Join-Path $PublishDir 'WormsCursor.exe'
if (-not (Test-Path $mainExe)) {
    throw "Expected main exe not found at $mainExe — check <AssemblyName> in the csproj."
}

Write-Host "==> Ensuring the vpk global tool is available…" -ForegroundColor Cyan
# Idempotent: install if missing, otherwise leave the existing one in place.
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    dotnet tool install --global vpk
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool install vpk failed (exit $LASTEXITCODE)." }
    Write-Host "    Installed vpk. If 'vpk' isn't found below, restart the shell so PATH refreshes." -ForegroundColor Yellow
}

if ($DownloadPrior) {
    Write-Host "==> Downloading latest GitHub release for delta computation…" -ForegroundColor Cyan
    # vpk reads GH_TOKEN from the environment to avoid the anonymous rate limit.
    vpk download github --repoUrl $RepoUrl
    if ($LASTEXITCODE -ne 0) {
        throw "vpk download failed. For the first release omit -DownloadPrior."
    }
}

Write-Host "==> Packing v$Version with Velopack…" -ForegroundColor Cyan
vpk pack `
    --packId WormsCursor `
    --packVersion $Version `
    --packDir $PublishDir `
    --mainExe WormsCursor.exe `
    --packTitle "WormsCursor" `
    --packAuthors "Dawid Wenderski" `
    --outputDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed (exit $LASTEXITCODE)." }

Write-Host "==> Done. Release artifacts in $OutputDir :" -ForegroundColor Green
Get-ChildItem $OutputDir | Select-Object Name, Length | Format-Table -AutoSize
