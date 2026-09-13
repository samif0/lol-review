param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(3, 14400)][int]$DurationSeconds = 120,
    [ValidateRange(500, 10000)][int]$IntervalMilliseconds = 1000,
    [int[]]$RootProcessIds = @(),
    [ValidateSet('disabled', 'enabled', 'observation', 'synthetic-disabled', 'synthetic-enabled')][string]$Mode = 'observation'
)

# Read-only counters. Never activates capture, changes settings, or stops programs.
$ErrorActionPreference = 'Stop'
function Number-OrNull($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { return $null }
    $parsed = 0.0
    if ([double]::TryParse([string]$value, [ref]$parsed) -and -not [double]::IsNaN($parsed) -and -not [double]::IsInfinity($parsed)) { return $parsed }
    return $null
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$samplePath = Join-Path $outputPath 'resources.jsonl'
if (Test-Path -LiteralPath $samplePath) { throw 'Refusing to overwrite existing benchmark samples.' }
$osInfo = Get-CimInstance Win32_OperatingSystem
$computerInfo = Get-CimInstance Win32_ComputerSystem
$inventory = [ordered]@{
    schemaVersion = 1; checkedAt = [DateTime]::UtcNow.ToString('o'); mode = $Mode
    durationRequestedSeconds = $DurationSeconds; intervalRequestedMs = $IntervalMilliseconds
    roots = @($RootProcessIds); samplerPid = $PID
    os = $osInfo | Select-Object Caption, Version, BuildNumber
    computer = $computerInfo | Select-Object Manufacturer, Model, TotalPhysicalMemory, NumberOfLogicalProcessors
    cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed)
    gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, DriverDate, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate)
    disks = @(Get-PhysicalDisk | Select-Object FriendlyName, MediaType, BusType, Size)
    powerScheme = (powercfg /GETACTIVESCHEME)
    warnings = @('GPU instances are per engine; do not sum engine percentages into total GPU utilization.', 'Process I/O includes nondisk I/O; physical disk counters are whole-machine activity.', 'No game or recording state is inferred from the supplied mode label.')
}
$inventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'inventory.json') -Encoding UTF8
$previousCpu = @{}
$timer = [Diagnostics.Stopwatch]::StartNew()
$writer = [IO.StreamWriter]::new($samplePath, $false, [Text.UTF8Encoding]::new($false))
try {
    while ($timer.Elapsed.TotalSeconds -lt $DurationSeconds) {
        $sampleStart = $timer.Elapsed.TotalMilliseconds
        $sampleUtc = [DateTime]::UtcNow.ToString('o')
        $unavailable = [Collections.Generic.List[string]]::new()
        $allProcesses = @(Get-CimInstance Win32_Process -Property Name, ProcessId, ParentProcessId, CreationDate, UserModeTime, KernelModeTime, WorkingSetSize, PrivatePageCount, ReadTransferCount, WriteTransferCount)
        $ownedIds = [Collections.Generic.HashSet[int]]::new()
        foreach ($processId in $RootProcessIds) { [void]$ownedIds.Add($processId) }
        do {
            $added = $false
            foreach ($proc in $allProcesses) {
                if ($ownedIds.Contains([int]$proc.ParentProcessId) -and $ownedIds.Add([int]$proc.ProcessId)) { $added = $true }
            }
        } while ($added)
        $processTime = $timer.Elapsed.TotalMilliseconds
        $processRows = @($allProcesses | Where-Object {
            $ownedIds.Contains([int]$_.ProcessId) -or $_.ProcessId -eq $PID -or $_.Name -match '^(League of Legends|revu-desktop|Revu.Sidecar|Ascent|ascent-gep|Overwolf|OverwolfBrowser|OverwolfHelper|OverwolfHelper64|obs64|obs32)\.exe$'
        } | ForEach-Object {
            $proc = $_
            $key = '{0}/{1}' -f $proc.ProcessId, $proc.CreationDate
            $userTime = Number-OrNull $proc.UserModeTime
            $kernelTime = Number-OrNull $proc.KernelModeTime
            $cpuSeconds = if ($null -ne $userTime -and $null -ne $kernelTime) { ($userTime + $kernelTime) / 10000000 } else { $null }
            $cpuPercent = $null
            if ($null -ne $cpuSeconds -and $previousCpu.ContainsKey($key)) {
                $elapsed = ($processTime - $previousCpu[$key].atMs) / 1000
                if ($elapsed -gt 0) { $cpuPercent = 100 * ($cpuSeconds - $previousCpu[$key].seconds) / $elapsed / $computerInfo.NumberOfLogicalProcessors }
            }
            if ($null -ne $cpuSeconds) { $previousCpu[$key] = @{ atMs = $processTime; seconds = $cpuSeconds } }
            [ordered]@{ pid = [int]$proc.ProcessId; parentPid = [int]$proc.ParentProcessId; name = $proc.Name
                owned = $ownedIds.Contains([int]$proc.ProcessId); cpuPercentMachine = $cpuPercent; cumulativeCpuSeconds = $cpuSeconds
                workingSetBytes = (Number-OrNull $proc.WorkingSetSize); privateBytes = (Number-OrNull $proc.PrivatePageCount)
                readTransferBytes = (Number-OrNull $proc.ReadTransferCount); writeTransferBytes = (Number-OrNull $proc.WriteTransferCount) }
        })
        $systemCpu = $null; $availableMb = $null; $diskRows = @(); $gpuRows = @()
        try { $systemCpu = Number-OrNull (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime; if ($null -eq $systemCpu) { $unavailable.Add('systemCpu') } } catch { $unavailable.Add('systemCpu') }
        try { $availableMb = Number-OrNull (Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory).AvailableMBytes; if ($null -eq $availableMb) { $unavailable.Add('systemMemory') } } catch { $unavailable.Add('systemMemory') }
        try { $diskRows = @(Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk | ForEach-Object {
            [ordered]@{ name = $_.Name; readBytesPerSecond = (Number-OrNull $_.DiskReadBytesPersec); writeBytesPerSecond = (Number-OrNull $_.DiskWriteBytesPersec); queueLength = (Number-OrNull $_.AvgDiskQueueLength); busyPercent = (Number-OrNull $_.PercentDiskTime) }
        }) } catch { $unavailable.Add('physicalDisk') }
        try {
            $gpuCounters = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine)
            if ($gpuCounters.Count -eq 0 -or @($gpuCounters | Where-Object { $null -eq $_.UtilizationPercentage }).Count -gt 0) { $unavailable.Add('gpuEngines') }
            $gpuRows = @($gpuCounters | Where-Object { $_.UtilizationPercentage -gt 0 -or ($_.Name -match '^pid_(\d+)_' -and $ownedIds.Contains([int]$Matches[1])) } | ForEach-Object {
            [ordered]@{ instance = $_.Name; utilizationPercent = (Number-OrNull $_.UtilizationPercentage) }
        }) } catch { $unavailable.Add('gpuEngines') }
        $row = [ordered]@{ utc = $sampleUtc; elapsedMs = $sampleStart; acquisitionMs = $timer.Elapsed.TotalMilliseconds - $sampleStart
            mode = $Mode; systemCpuPercent = $systemCpu; availableMemoryMB = $availableMb
            processes = $processRows; physicalDisks = $diskRows; gpuEngines = $gpuRows; unavailable = @($unavailable) }
        $writer.WriteLine(($row | ConvertTo-Json -Depth 6 -Compress)); $writer.Flush()
        $remaining = $IntervalMilliseconds - ($timer.Elapsed.TotalMilliseconds - $sampleStart)
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int]$remaining) }
    }
} finally { $writer.Dispose() }
Write-Output $outputPath
