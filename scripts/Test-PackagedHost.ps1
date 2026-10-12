# Packaged startup and synthetic playback smoke for artifacts\desktop-package\win-unpacked,
# shared by CI and the release workflow. Requires deps\ffmpeg.exe from Get-PinnedFfmpeg.ps1
# before `npm run package:dir`, so the package carries the verified binary.
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($RepositoryRoot)
$package = Join-Path $root 'artifacts\desktop-package\win-unpacked'
$packagedFfmpeg = Join-Path $package 'ffmpeg.exe'
if (-not (Test-Path -LiteralPath $packagedFfmpeg -PathType Leaf)) {
    throw 'The package must include ffmpeg.exe for clip extraction.'
}
$expected = (Get-FileHash -LiteralPath (Join-Path $root 'deps\ffmpeg.exe') -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $packagedFfmpeg -Algorithm SHA256).Hash -ne $expected) {
    throw 'Packaged ffmpeg.exe differs from the verified download.'
}
$env:PATH = $package + [IO.Path]::PathSeparator + $env:PATH
& (Join-Path $PSScriptRoot 'Test-ElectronHost.ps1') -PackagedExe (Join-Path $package 'revu-desktop.exe')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
