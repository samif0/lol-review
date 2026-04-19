#requires -Version 5.1
<#
.SYNOPSIS
    Scrub leaked Google AI API keys from local log files.

.DESCRIPTION
    Walks every *.log file under %LOCALAPPDATA%\LoLReview, finds any
    Google-AI-key-shaped string (AIzaSy + 33 chars of [A-Za-z0-9_-]),
    and replaces it with a redacted placeholder. Safe to run multiple
    times. Uses System.IO.File (not Get-Content) because PowerShell's
    default file IO can't reliably address files in reparse-pointed
    packaged-app folders.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$logDir = Join-Path $env:LOCALAPPDATA 'LoLReview'
if (-not (Test-Path $logDir)) {
    Write-Host "No log directory at $logDir"
    exit 0
}

$pattern = 'AIzaSy[A-Za-z0-9_-]{33}'
$replacement = 'AIzaSy[REDACTED]'
$totalScrubbed = 0

Get-ChildItem -Path "$logDir\*.log" -ErrorAction SilentlyContinue | ForEach-Object {
    $text = [System.IO.File]::ReadAllText($_.FullName)
    $matches = ([regex]$pattern).Matches($text)
    if ($matches.Count -gt 0) {
        $scrubbed = [regex]::Replace($text, $pattern, $replacement)
        [System.IO.File]::WriteAllText($_.FullName, $scrubbed)
        Write-Host ("{0}: {1} keys scrubbed" -f $_.Name, $matches.Count)
        $totalScrubbed += $matches.Count
    }
}

Write-Host ""
Write-Host ("Total scrubbed: {0}" -f $totalScrubbed)
