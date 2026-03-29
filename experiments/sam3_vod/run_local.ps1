param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)

$scriptPath = Join-Path $PSScriptRoot "analyze_vods.py"
$localWindowsPython = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
$localPosixPython = Join-Path $PSScriptRoot ".venv\bin\python.exe"

if (-not (Test-Path $scriptPath)) {
    throw "Runner not found at $scriptPath"
}

if (-not $env:LOLREVIEW_ENABLE_SAM3_EXPERIMENT) {
    Write-Error "Set LOLREVIEW_ENABLE_SAM3_EXPERIMENT=1 before running this local-only experiment."
    exit 1
}

if (Test-Path $localWindowsPython) {
    & $localWindowsPython $scriptPath @Args
    exit $LASTEXITCODE
}

if (Test-Path $localPosixPython) {
    & $localPosixPython $scriptPath @Args
    exit $LASTEXITCODE
}

$pyLauncher = Get-Command py -ErrorAction SilentlyContinue
if ($null -ne $pyLauncher) {
    & py -3.12 $scriptPath @Args
    exit $LASTEXITCODE
}

$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -ne $python) {
    & python $scriptPath @Args
    exit $LASTEXITCODE
}

throw "Could not find py or python on PATH."
