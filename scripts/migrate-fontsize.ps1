#requires -Version 5.1
<#
.SYNOPSIS
    Rewrite FontSize literal values in every XAML file to use the
    FontSizes singleton, so the app can live-scale text on Ctrl+/-
    without a RenderTransform.

.DESCRIPTION
    - Scans src/LoLReview.App/**/*.xaml
    - Replaces FontSize="<N>" with FontSize="{x:Bind svc:FontSizes.Instance.<Bucket>, Mode=OneWay}"
      where <Bucket> is picked by the numeric value (see $BucketMap below).
    - Adds `xmlns:svc="using:LoLReview.App.Services"` to the root tag of
      any XAML file that got rewritten and doesn't already have it.
    - Idempotent: running twice is a no-op because the second pass
      finds no numeric FontSize literals.

.NOTES
    Intentional cliff: values outside the known buckets (anything
    bigger than 52 or non-integer) are printed to stderr as warnings
    and left unchanged. That's the safety net — visual flagging a
    tiny number of outliers beats silently miscategorizing them.
#>

[CmdletBinding()]
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Resolve-Path (Join-Path $ScriptDir '..')
$AppDir    = Join-Path $RepoRoot 'src\LoLReview.App'

# Size -> bucket mapping. Matches FontSizes.cs.
function Get-Bucket([int]$size) {
    if ($size -le 8)  { return 'Micro' }
    if ($size -eq 9)  { return 'Caption' }
    if ($size -le 11) { return 'Meta' }
    if ($size -eq 12) { return 'Body' }
    if ($size -eq 13) { return 'BodyEmphasis' }
    if ($size -le 15) { return 'Subtitle' }
    if ($size -le 18) { return 'Title' }
    if ($size -le 24) { return 'TitleLarge' }
    if ($size -le 28) { return 'Hero' }
    if ($size -le 36) { return 'HeroLarge' }
    if ($size -le 60) { return 'Display' }
    return $null
}

$xamlFiles = Get-ChildItem -Path $AppDir -Filter '*.xaml' -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }

$totalReplacements = 0
$filesTouched = 0
$warnings = @()

foreach ($file in $xamlFiles) {
    $content = Get-Content -Path $file.FullName -Raw
    if ($content -notmatch 'FontSize="\d') { continue }

    $original = $content
    $localReplacements = 0

    # Match FontSize="123" with optional surrounding whitespace. Capture
    # only integers — if someone wrote FontSize="12.5" we leave it.
    $content = [regex]::Replace($content, 'FontSize="(\d+)"', {
        param($m)
        $size = [int]$m.Groups[1].Value
        $bucket = Get-Bucket $size
        if (-not $bucket) {
            $script:warnings += "  $($file.Name): skipped FontSize=`"$size`" (no bucket)"
            return $m.Value
        }
        $script:totalReplacements++
        $localReplacements++
        return "FontSize=`"{Binding $bucket, Source={StaticResource FontSizes}}`""
    })

    if ($content -eq $original) { continue }

    # Ensure xmlns:svc is on the root element. Look for the first opening
    # tag with xmlns:x=... in the file and append xmlns:svc after it if
    # missing.
    if ($content -notmatch 'xmlns:svc="using:LoLReview\.App\.Services"') {
        $content = [regex]::Replace(
            $content,
            '(xmlns:x="http://schemas\.microsoft\.com/winfx/2006/xaml")',
            "`$1`n    xmlns:svc=`"using:LoLReview.App.Services`"",
            [System.Text.RegularExpressions.RegexOptions]::Singleline,
            1)
    }

    $filesTouched++
    if (-not $DryRun) {
        Set-Content -Path $file.FullName -Value $content -NoNewline -Encoding UTF8
    }
    Write-Host ("  {0,-50} {1,3} replaced" -f $file.Name, $localReplacements)
}

Write-Host ""
Write-Host ("Files touched: {0}" -f $filesTouched)
Write-Host ("Total replacements: {0}" -f $totalReplacements)
if ($warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "Warnings:" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
}
if ($DryRun) {
    Write-Host ""
    Write-Host "(dry run - no files modified)"
}
