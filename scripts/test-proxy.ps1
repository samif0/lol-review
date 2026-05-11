$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location (Join-Path $repo "proxy")
try {
    npm.cmd test
}
finally {
    Pop-Location
}
