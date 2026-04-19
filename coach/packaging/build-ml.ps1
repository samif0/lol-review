#requires -Version 5.1
<#
.SYNOPSIS
    Build the coach-ml extras pack.

.DESCRIPTION
    Produces coach-ml-<version>-win-x64.zip + .sha256 under coach/dist/.
    The pack contains a site-packages/ tree with torch (CPU-only),
    sentence-transformers, hdbscan, and their transitive deps. No
    Python interpreter — the extras load into the core pack's embedded
    Python at runtime via coach/_extras.py.

    Requires the core pack to have been built first (we reuse its
    embedded Python to pip-install with matching ABI).

.PARAMETER Version
    Version string to embed in the zip filename. Must match the core
    pack version to avoid cross-version ABI mismatches. Defaults to
    __version__ in coach/coach/__init__.py.

.PARAMETER CudaIndexUrl
    Override the PyTorch wheel index. Defaults to the CPU-only index,
    producing a ~500–800 MB pack. Pass
    https://download.pytorch.org/whl/cu121 (or similar) for a GPU pack.

.PARAMETER Clean
    If set, delete coach/build/ml/ before starting.

.EXAMPLE
    .\coach\packaging\build-ml.ps1
    .\coach\packaging\build-ml.ps1 -Version 0.1.1 -Clean
#>

[CmdletBinding()]
param(
    [string]$Version,
    [string]$CudaIndexUrl = 'https://download.pytorch.org/whl/cpu',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ─────────────────────────────── Config ───────────────────────────────

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$CoachRoot = Resolve-Path (Join-Path $ScriptDir '..')
$BuildDir  = Join-Path $CoachRoot 'build'
$DistDir   = Join-Path $CoachRoot 'dist'
$MlDir     = Join-Path $BuildDir 'ml'
$SiteDir   = Join-Path $MlDir 'site-packages'
$CoreRuntimeDir = Join-Path $BuildDir 'core\runtime'
$ReqFile   = Join-Path $CoachRoot 'requirements-ml.txt'
$CoachPkg  = Join-Path $CoachRoot 'coach'

if (-not $Version) {
    $initPy = Join-Path $CoachPkg '__init__.py'
    $match = Select-String -Path $initPy -Pattern '__version__\s*=\s*"([^"]+)"'
    if (-not $match) { throw "Could not read __version__ from $initPy" }
    $Version = $match.Matches[0].Groups[1].Value
    Write-Host "Detected version: $Version"
}

$PackName = "coach-ml-$Version-win-x64"
$ZipPath  = Join-Path $DistDir "$PackName.zip"
$ShaPath  = Join-Path $DistDir "$PackName.sha256"

# ─────────────────────────────── Prep ─────────────────────────────────

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

$CorePythonExe = Join-Path $CoreRuntimeDir 'python.exe'
if (-not (Test-Path $CorePythonExe)) {
    throw "Core pack runtime not found at $CorePythonExe. Run .\coach\packaging\build-core.ps1 first."
}

if ($Clean -and (Test-Path $MlDir)) {
    Step "Cleaning $MlDir"
    Remove-Item -Recurse -Force $MlDir
}

New-Item -ItemType Directory -Force -Path $MlDir, $SiteDir, $DistDir | Out-Null

# ─────────────────────────────── Install ML deps ──────────────────────

Step "Installing ML extras into $SiteDir (CPU torch index: $CudaIndexUrl)"
& $CorePythonExe -m pip install `
    --no-warn-script-location `
    --disable-pip-version-check `
    --target $SiteDir `
    --extra-index-url $CudaIndexUrl `
    -r $ReqFile
if ($LASTEXITCODE -ne 0) { throw "pip install --target failed with exit code $LASTEXITCODE" }

# ─────────────────────────────── Prune weight ─────────────────────────

Step "Pruning unused files from site-packages"

# 1. Strip __pycache__ — regenerated on first use, saves ~80–150 MB.
Get-ChildItem -Path $SiteDir -Include '__pycache__' -Recurse -Force -Directory `
    | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# 2. Remove torch test fixtures (torch ships ~200 MB of them by default).
$torchTestDir = Join-Path $SiteDir 'torch\test'
if (Test-Path $torchTestDir) {
    Write-Host "Removing torch/test/ (~200 MB)"
    Remove-Item -Recurse -Force $torchTestDir
}

# 3. Remove .dist-info except METADATA (pip needs METADATA for freeze,
#    but we don't need RECORD / wheel files at runtime).
# NOTE: Be conservative — some libs (notably sentence-transformers) do
# introspect dist-info at runtime. Skipping this prune for now. Revisit
# if size becomes an issue.

# ─────────────────────────────── Manifest ─────────────────────────────

$manifest = [ordered]@{
    pack        = 'coach-ml'
    version     = $Version
    platform    = 'win-x64'
    core_required = $true
    index_url   = $CudaIndexUrl
    built_at    = [DateTime]::UtcNow.ToString('o')
}
$manifestJson = $manifest | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $MlDir 'manifest.json') -Value $manifestJson -Encoding UTF8

# ─────────────────────────────── Zip + SHA ────────────────────────────

Step "Creating $ZipPath"
if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }

# Zip the contents of $MlDir so extract-into-dir produces
# <extract>/site-packages/ + <extract>/manifest.json directly.
Push-Location $MlDir
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

# Explicit success exit so the script doesn't inherit a stray
# $LASTEXITCODE from pip and fail CI.
exit 0
