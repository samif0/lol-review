$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location (Join-Path $repo "coach")
try {
    py -3 -m pytest tests
}
finally {
    Pop-Location
}
