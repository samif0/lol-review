#requires -Version 5.1
<#
.SYNOPSIS
    Build the coach-core sidecar pack.

.DESCRIPTION
    Produces coach-core-<version>-win-x64.zip + .sha256 under coach/dist/.
    The pack contains an embedded Python 3.12 interpreter, core runtime
    deps (fastapi, httpx, ...), and the coach/ package. It does NOT
    contain torch / sentence-transformers / hdbscan — those ship in the
    optional coach-ml pack built by build-ml.ps1.

    This script is designed to run both locally (for developer smoke
    testing) and in CI (GitHub Actions, PR 2). It is deterministic: the
    same inputs produce the same output bytes on the same Python +
    platform.

.PARAMETER Version
    Version string to embed in the zip filename. Defaults to the
    __version__ string in coach/coach/__init__.py.

.PARAMETER Clean
    If set, delete coach/build/ before starting. Use when switching
    Python versions or to force a clean rebuild.

.EXAMPLE
    .\coach\packaging\build-core.ps1
    .\coach\packaging\build-core.ps1 -Version 0.1.1 -Clean
#>

[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ─────────────────────────────── Config ───────────────────────────────

$PythonVersion = '3.12.8'
$PythonZipName = "python-$PythonVersion-embed-amd64.zip"
$PythonZipUrl  = "https://www.python.org/ftp/python/$PythonVersion/$PythonZipName"
$GetPipUrl     = 'https://bootstrap.pypa.io/get-pip.py'

# Resolve paths relative to this script's location.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$CoachRoot = Resolve-Path (Join-Path $ScriptDir '..')
$BuildDir  = Join-Path $CoachRoot 'build'
$DistDir   = Join-Path $CoachRoot 'dist'
$CoreDir   = Join-Path $BuildDir  'core'
$RuntimeDir = Join-Path $CoreDir 'runtime'
$AppDir    = Join-Path $CoreDir 'app'
$CacheDir  = Join-Path $BuildDir '_cache'
$ReqFile   = Join-Path $CoachRoot 'requirements-core.txt'
$CoachPkg  = Join-Path $CoachRoot 'coach'

if (-not $Version) {
    $initPy = Join-Path $CoachPkg '__init__.py'
    $match = Select-String -Path $initPy -Pattern '__version__\s*=\s*"([^"]+)"'
    if (-not $match) { throw "Could not read __version__ from $initPy" }
    $Version = $match.Matches[0].Groups[1].Value
    Write-Host "Detected version: $Version"
}

$PackName  = "coach-core-$Version-win-x64"
$ZipPath   = Join-Path $DistDir "$PackName.zip"
$ShaPath   = Join-Path $DistDir "$PackName.sha256"

# ─────────────────────────────── Prep ─────────────────────────────────

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

if ($Clean -and (Test-Path $BuildDir)) {
    Step "Cleaning $BuildDir"
    Remove-Item -Recurse -Force $BuildDir
}

New-Item -ItemType Directory -Force -Path $BuildDir, $DistDir, $CacheDir | Out-Null

# ─────────────────────────────── Fetch embedded Python ────────────────

$PythonZipPath = Join-Path $CacheDir $PythonZipName
if (-not (Test-Path $PythonZipPath)) {
    Step "Downloading $PythonZipName"
    Invoke-WebRequest -Uri $PythonZipUrl -OutFile $PythonZipPath -UseBasicParsing
} else {
    Write-Host "Using cached $PythonZipName"
}

$GetPipPath = Join-Path $CacheDir 'get-pip.py'
if (-not (Test-Path $GetPipPath)) {
    Step "Downloading get-pip.py"
    Invoke-WebRequest -Uri $GetPipUrl -OutFile $GetPipPath -UseBasicParsing
} else {
    Write-Host "Using cached get-pip.py"
}

# ─────────────────────────────── Extract runtime ──────────────────────

Step "Extracting embedded Python to $RuntimeDir"
if (Test-Path $RuntimeDir) { Remove-Item -Recurse -Force $RuntimeDir }
New-Item -ItemType Directory -Force -Path $RuntimeDir | Out-Null
Expand-Archive -Path $PythonZipPath -DestinationPath $RuntimeDir -Force

# Embedded Python ships with isolated `sys.path` via a `._pth` file that
# disables `site.py` by default. We need site.py active so pip and our
# pip-installed deps work. Uncomment `import site` and add the
# site-packages dir we'll use.
$PthFile = Get-ChildItem -Path $RuntimeDir -Filter 'python*._pth' | Select-Object -First 1
if (-not $PthFile) { throw "No python*._pth file found in $RuntimeDir" }

Step "Patching $($PthFile.Name) to enable site-packages + app dir"
# The embedded python `._pth` file REPLACES sys.path — it does not augment
# it — so `PYTHONPATH` is ignored unless site.py is active AND the path
# is listed here. `..\app` points at the launcher's `<pack>/app/` which
# contains the `coach/` package.
$pthContent = @'
python312.zip
.
Lib\site-packages
..\app

# Uncomment to run site.main() automatically
import site
'@
Set-Content -Path $PthFile.FullName -Value $pthContent -Encoding ASCII

# ─────────────────────────────── Bootstrap pip ────────────────────────

$PythonExe = Join-Path $RuntimeDir 'python.exe'
Step "Bootstrapping pip"
& $PythonExe $GetPipPath --no-warn-script-location
if ($LASTEXITCODE -ne 0) { throw "get-pip.py failed with exit code $LASTEXITCODE" }

# ─────────────────────────────── Install core deps ────────────────────

Step "Installing core requirements into embedded runtime"
& $PythonExe -m pip install --no-warn-script-location --disable-pip-version-check -r $ReqFile
if ($LASTEXITCODE -ne 0) { throw "pip install failed with exit code $LASTEXITCODE" }

# Strip cached bytecode — we want `python -m coach.main` to compile
# fresh on first run. Saves ~5–10 MB per pack and avoids pycache
# path leakage of the build machine's file structure.
Step "Stripping __pycache__ directories from runtime"
Get-ChildItem -Path $RuntimeDir -Include '__pycache__' -Recurse -Force -Directory `
    | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# ─────────────────────────────── Copy coach/ package ──────────────────

Step "Copying coach/ package into $AppDir"
if (Test-Path $AppDir) { Remove-Item -Recurse -Force $AppDir }
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null

# Exclude dev artifacts and the concepts subpackage's heavy deps are
# already handled at import time; we still ship concepts/ because the
# code needs to be present for the endpoints to exist. They just return
# 501 until the ML pack is installed.
$excludeDirs = @('__pycache__', '.pytest_cache', '.mypy_cache', '.ruff_cache')
$excludeFiles = @('*.pyc', '*.pyo')

# Robocopy handles large trees efficiently and has nice exclude flags.
# Its exit codes ≤ 7 are success (see `robocopy /?`).
$roboArgs = @(
    $CoachPkg, (Join-Path $AppDir 'coach'),
    '/E',                              # recurse including empty
    '/XD', ($excludeDirs -join ' '),   # exclude dirs
    '/XF', ($excludeFiles -join ' '),  # exclude files
    '/NFL', '/NDL', '/NJH', '/NJS',    # quiet output
    '/NC', '/NS', '/NP'
)
& robocopy @roboArgs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

# ─────────────────────────────── Launcher ─────────────────────────────

# A tiny batch launcher at the pack root so downstream code (C# sidecar
# service) has a stable entry point that doesn't care about paths inside.
$launcher = @'
@echo off
rem Coach sidecar launcher. Forwards all args to `python -m coach.main`.
rem The app/ dir is on sys.path via runtime\python312._pth, so we do
rem NOT set PYTHONPATH (which the embedded python ignores anyway).
setlocal
set "PACK_ROOT=%~dp0"
"%PACK_ROOT%runtime\python.exe" -u -X utf8 -m coach.main %*
endlocal
'@
Set-Content -Path (Join-Path $CoreDir 'coach.cmd') -Value $launcher -Encoding ASCII

# ─────────────────────────────── Manifest ─────────────────────────────

$manifest = [ordered]@{
    pack        = 'coach-core'
    version     = $Version
    python      = $PythonVersion
    platform    = 'win-x64'
    entry       = 'coach.cmd'
    ml_required = $false
    built_at    = [DateTime]::UtcNow.ToString('o')
}
$manifestJson = $manifest | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $CoreDir 'manifest.json') -Value $manifestJson -Encoding UTF8

# ─────────────────────────────── Zip + SHA ────────────────────────────

Step "Creating $ZipPath"
if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
# Compress the CONTENTS of $CoreDir, not the dir itself, so users extract
# into a dir of their own choosing without a nested redundant folder.
Push-Location $CoreDir
try {
    Compress-Archive -Path * -DestinationPath $ZipPath -CompressionLevel Optimal
} finally {
    Pop-Location
}

Step "Computing SHA256"
$hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaLine = "$hash *$PackName.zip"
Set-Content -Path $ShaPath -Value $shaLine -Encoding ASCII

# ─────────────────────────────── Summary ──────────────────────────────

$zipSize = (Get-Item $ZipPath).Length
Write-Host ""
Write-Host "Built $PackName" -ForegroundColor Green
Write-Host "  zip:    $ZipPath"
Write-Host "  sha256: $hash"
Write-Host ("  size:   {0:N2} MB" -f ($zipSize / 1MB))
