param(
    [int]$Hours = 24,
    [int]$SampleMinutes = 5,
    [string]$ProcessName = "LoLReview.App",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path $repo "docs\perf-long-soak-$stamp.csv"
}

$endAt = (Get-Date).AddHours($Hours)
"timestamp,process_id,private_mb,working_set_mb,handles,cpu_seconds" | Set-Content -LiteralPath $OutputPath -Encoding UTF8

while ((Get-Date) -lt $endAt) {
    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    $timestamp = Get-Date -Format o
    if ($process) {
        $privateMb = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
        $workingMb = [math]::Round($process.WorkingSet64 / 1MB, 2)
        $cpu = [math]::Round($process.CPU, 2)
        "$timestamp,$($process.Id),$privateMb,$workingMb,$($process.HandleCount),$cpu" |
            Add-Content -LiteralPath $OutputPath -Encoding UTF8
    } else {
        "$timestamp,,0,0,0,0" | Add-Content -LiteralPath $OutputPath -Encoding UTF8
    }

    Start-Sleep -Seconds ($SampleMinutes * 60)
}

Write-Host "Wrote $OutputPath"
