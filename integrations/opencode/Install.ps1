param([string]$ConfigPath)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    if (-not $ConfigPath) {
        if ($env:OPENCODE_CONFIG_DIR) {
            $configDirectory = $env:OPENCODE_CONFIG_DIR
        } else {
            $configDirectory = Join-Path $env:USERPROFILE '.config\opencode'
        }
        $ConfigPath = Join-Path $configDirectory 'opencode.jsonc'
        $jsonPath = Join-Path $configDirectory 'opencode.json'
        if (-not (Test-Path -LiteralPath $ConfigPath) -and (Test-Path -LiteralPath $jsonPath)) {
            $ConfigPath = $jsonPath
        }
    }
    $ConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Could not install plugin dependencies.' }
    node --experimental-strip-types scripts/materialize-native.ts
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the native cursor helper.' }
    node --experimental-strip-types scripts/register.ts $ConfigPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not register the OpenCode V2 plugin.' }
    Write-Output 'Registration complete. Run opencode reload to load the plugin in the running service.'
} finally {
    Pop-Location
}
