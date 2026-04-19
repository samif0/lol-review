#requires -Version 5.1
<#
.SYNOPSIS
    Repair UTF-8 mojibake in source files.

.DESCRIPTION
    A prior batch script (scripts/migrate-fontsize.ps1) wrote files
    via `Set-Content -Encoding UTF8` on PowerShell 5.1, which mangles
    already-present UTF-8 multi-byte sequences like em-dash by
    re-encoding them as cp1252. Result: literal strings like
    "â€""" and "â†'" appear in XAML sources, rendering in the app as
    visible mojibake.

    This script scans every source file under src/LoLReview.App,
    detects the known mangled sequences, and replaces them with the
    correct Unicode codepoint. Re-saves as UTF-8 with BOM (matching
    the existing repo convention).
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Resolve-Path (Join-Path $ScriptDir '..')
$AppDir    = Join-Path $RepoRoot 'src\LoLReview.App'

# Build the replacement table. Keys are the mojibake byte sequences as
# they appear when UTF-8 is mis-decoded as cp1252; values are the
# correct Unicode characters.
$replacements = [ordered]@{}
$replacements[[string]([char]0xE2 + [char]0x80 + [char]0x94)] = [string][char]0x2014  # em dash
$replacements[[string]([char]0xE2 + [char]0x80 + [char]0x93)] = [string][char]0x2013  # en dash
$replacements[[string]([char]0xE2 + [char]0x80 + [char]0x9D)] = [string][char]0x2014  # mis-rendered em dash
$replacements[[string]([char]0xE2 + [char]0x80 + [char]0x9C)] = [string][char]0x201C  # left curly double
$replacements[[string]([char]0xE2 + [char]0x80 + [char]0x99)] = [string][char]0x2019  # right curly single
$replacements[[string]([char]0xE2 + [char]0x80 + [char]0x98)] = [string][char]0x2018  # left curly single
$replacements[[string]([char]0xE2 + [char]0x80 + [char]0x9E)] = [string][char]0x201E  # low double
$replacements[[string]([char]0xE2 + [char]0x80 + [char]0xA2)] = [string][char]0x2022  # bullet
$replacements[[string]([char]0xE2 + [char]0x86 + [char]0x92)] = [string][char]0x2192  # right arrow
$replacements[[string]([char]0xE2 + [char]0x86 + [char]0x90)] = [string][char]0x2190  # left arrow

$files = Get-ChildItem -Path $AppDir -Include *.xaml,*.cs -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' }

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$touched = 0

foreach ($file in $files) {
    $content = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    $original = $content
    foreach ($key in $replacements.Keys) {
        $content = $content.Replace($key, $replacements[$key])
    }
    if ($content -ne $original) {
        [System.IO.File]::WriteAllText($file.FullName, $content, $utf8Bom)
        Write-Host ("fixed: {0}" -f $file.FullName.Replace($RepoRoot.Path + [IO.Path]::DirectorySeparatorChar, ''))
        $touched++
    }
}

Write-Host ""
Write-Host ("Files fixed: {0}" -f $touched)
