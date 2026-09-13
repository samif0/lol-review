param(
    [Parameter(Mandatory = $true)][int]$TargetProcessId,
    [ValidateRange(5, 3600)][int]$DurationSeconds = 60,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [string]$FrameTimesJson
)
$ErrorActionPreference = 'Stop'
$samples = [Collections.Generic.List[object]]::new()
$process = Get-Process -Id $TargetProcessId
$previousCpu = $process.TotalProcessorTime.TotalSeconds
$previousTime = [Diagnostics.Stopwatch]::StartNew()
for ($index = 0; $index -lt $DurationSeconds; $index++) {
    Start-Sleep -Seconds 1
    $process.Refresh()
    $elapsed = $previousTime.Elapsed.TotalSeconds
    $cpu = $process.TotalProcessorTime.TotalSeconds
    $io = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess = $TargetProcessId" -ErrorAction SilentlyContinue
    $samples.Add([pscustomobject]@{
        TotalCpuPercent = 100 * ($cpu - $previousCpu) / $elapsed / [Environment]::ProcessorCount
        WorkingSetMB = $process.WorkingSet64 / 1MB
        PrivateMemoryMB = $process.PrivateMemorySize64 / 1MB
        IOReadBytesPerSecond = $io.IOReadBytesPersec
        IOWriteBytesPerSecond = $io.IOWriteBytesPersec
    })
    $previousCpu = $cpu
    $previousTime.Restart()
}
$p99 = $null
if ($FrameTimesJson) {
    $frames = @(Get-Content -LiteralPath $FrameTimesJson -Raw | ConvertFrom-Json | Sort-Object)
    if ($frames.Count -gt 0) { $p99 = $frames[[Math]::Ceiling($frames.Count * .99) - 1] }
}
[pscustomobject]@{
    Version = 1
    TimestampUtc = [DateTimeOffset]::UtcNow
    TargetProcessId = $TargetProcessId
    DurationSeconds = $DurationSeconds
    LogicalProcessors = [Environment]::ProcessorCount
    AverageTotalCpuPercent = ($samples | Measure-Object TotalCpuPercent -Average).Average
    PeakPrivateMemoryMB = ($samples | Measure-Object PrivateMemoryMB -Maximum).Maximum
    P99GameplayFrameTimeMs = $p99
    FrameTimeStatus = $(if ($null -eq $p99) { 'not measured' } else { 'imported external frame times' })
    Samples = $samples
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Saved performance observations to $OutputPath. Compare matched baseline/candidate runs; this is not a pass certificate."
