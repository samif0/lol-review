param(
    [string]$VenvPath = ".venv-coach-qwen",
    [string]$TorchIndexUrl = "https://download.pytorch.org/whl/cu128",
    [string]$BasePython = "",
    [switch]$CpuOnly,
    [switch]$SkipTorch,
    [switch]$Recreate
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$venvFullPath = Join-Path $repoRoot $VenvPath

function Resolve-BasePython {
    param([string]$RequestedPath)

    $candidates = @()
    if ($RequestedPath) {
        $candidates += $RequestedPath
    }

    if ($env:LOLREVIEW_COACH_BOOTSTRAP_PYTHON) {
        $candidates += $env:LOLREVIEW_COACH_BOOTSTRAP_PYTHON
    }

    $candidates += @(
        "C:\Users\$env:USERNAME\AppData\Local\Programs\Python\Python310\python.exe",
        "C:\Python313\python.exe",
        "C:\Users\$env:USERNAME\AppData\Local\Programs\Python\Python314\python.exe",
        "python"
    )

    foreach ($candidate in $candidates) {
        if (-not $candidate) {
            continue
        }

        if ($candidate -eq "python") {
            $resolved = Get-Command python -ErrorAction SilentlyContinue
            if ($resolved -and $resolved.Source -notlike "*Inkscape*") {
                return $resolved.Source
            }
            continue
        }

        if (Test-Path $candidate) {
            return $candidate
        }
    }

    throw "Could not find a supported base CPython interpreter. Set -BasePython or LOLREVIEW_COACH_BOOTSTRAP_PYTHON."
}

function Invoke-Pip {
    param([string[]]$PipArgs)

    & $pythonExe -m pip @PipArgs
    if ($LASTEXITCODE -ne 0) {
        throw "pip failed: $($PipArgs -join ' ')"
    }
}

$basePythonExe = Resolve-BasePython -RequestedPath $BasePython

if ($Recreate -and (Test-Path $venvFullPath)) {
    Remove-Item $venvFullPath -Recurse -Force
}

if (-not (Test-Path $venvFullPath)) {
    & $basePythonExe -m venv $venvFullPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create venv with $basePythonExe"
    }
}

$pythonExe = Join-Path $venvFullPath "Scripts\python.exe"
if (-not (Test-Path $pythonExe)) {
    $pythonExe = Join-Path $venvFullPath "bin\python.exe"
}

if (-not (Test-Path $pythonExe)) {
    $pythonExe = Join-Path $venvFullPath "bin\python"
}

if (-not (Test-Path $pythonExe)) {
    throw "Could not find virtualenv python at $venvFullPath"
}

Invoke-Pip -PipArgs @("install", "--upgrade", "pip", "setuptools", "wheel")

if (-not $SkipTorch) {
    if ($CpuOnly) {
        Invoke-Pip -PipArgs @("install", "--upgrade", "--index-url", "https://pypi.org/simple", "torch", "torchvision", "torchaudio")
    }
    else {
        try {
            Invoke-Pip -PipArgs @("install", "--upgrade", "--index-url", $TorchIndexUrl, "torch", "torchvision", "torchaudio")
        }
        catch {
            Write-Warning "CUDA torch wheel install failed from $TorchIndexUrl. Falling back to the default PyPI index."
            Invoke-Pip -PipArgs @("install", "--upgrade", "--index-url", "https://pypi.org/simple", "torch", "torchvision", "torchaudio")
        }
    }
}

Invoke-Pip -PipArgs @(
    "install",
    "--upgrade",
    "--index-url",
    "https://pypi.org/simple",
    "transformers>=4.57.0",
    "accelerate",
    "peft",
    "pillow",
    "numpy",
    "qwen-vl-utils",
    "huggingface_hub",
    "safetensors",
    "sentencepiece"
)

Write-Host ""
Write-Host "Qwen inference stack installed into $venvFullPath"
Write-Host "Base interpreter: $basePythonExe"
Write-Host "Coach Lab will auto-detect this interpreter on the next app launch."
Write-Host "Notes:"
Write-Host "- This installs teacher/base inference dependencies."
Write-Host "- The pretrained base judge can be registered immediately with register_qwen_base_model.py."
Write-Host "- Axolotl training is still expected to run under Linux/WSL, not native Windows."
