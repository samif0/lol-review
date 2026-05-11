param(
    [switch]$Ml
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$script = if ($Ml) {
    Join-Path $repo "coach\packaging\build-ml.ps1"
} else {
    Join-Path $repo "coach\packaging\build-core.ps1"
}

& $script
