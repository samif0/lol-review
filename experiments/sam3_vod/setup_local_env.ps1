param(
    [string]$PythonVersion = "3.12",
    [switch]$RecreateVenv
)

$venvRoot = Join-Path $PSScriptRoot ".venv"
$pythonExe = Join-Path $venvRoot "Scripts\python.exe"
$patcher = Join-Path $PSScriptRoot "patch_windows_fallbacks.py"

$uv = Get-Command uv -ErrorAction SilentlyContinue
if ($null -eq $uv) {
    throw "uv is required for local SAM3 setup. Install uv first and rerun."
}

if ($RecreateVenv -and (Test-Path $venvRoot)) {
    Remove-Item -Recurse -Force $venvRoot
}

if (-not (Test-Path $pythonExe)) {
    & uv venv $venvRoot --python $PythonVersion
    if ($LASTEXITCODE -ne 0) {
        throw "uv venv failed."
    }
}

& uv pip install --python $pythonExe --index-url https://download.pytorch.org/whl/cu128 torch==2.10.0 torchvision
if ($LASTEXITCODE -ne 0) {
    throw "PyTorch installation failed."
}

& uv pip install --python $pythonExe git+https://github.com/facebookresearch/sam3.git
if ($LASTEXITCODE -ne 0) {
    throw "SAM3 installation failed."
}

& uv pip install --python $pythonExe opencv-python einops ninja pycocotools psutil scikit-image numpy==1.26.4
if ($LASTEXITCODE -ne 0) {
    throw "Supporting package installation failed."
}

& $pythonExe $patcher
if ($LASTEXITCODE -ne 0) {
    throw "Applying Windows fallback patches failed."
}

Write-Host "Local SAM3 environment is ready at $venvRoot"
