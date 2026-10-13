# Runs the same proxy checks as CI's proxy job (after `npm ci` in proxy/).
$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location (Join-Path $repo "proxy")
try {
    npm.cmd run typecheck
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    npm.cmd test
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
