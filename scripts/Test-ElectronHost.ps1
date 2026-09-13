param([string]$DataRoot, [string]$PackagedExe)
$ErrorActionPreference = 'Stop'
$handoffRepo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $handoffRepo ('artifacts\overwolf-handoff\smoke-' + [Guid]::NewGuid().ToString('N'))
}
if (-not [IO.Path]::IsPathRooted($DataRoot)) { throw 'DataRoot must be absolute.' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if (Test-Path -LiteralPath $DataRoot) { throw 'Use a new empty scratch root for this test.' }
if (-not [string]::IsNullOrWhiteSpace($PackagedExe)) {
    if (-not [IO.Path]::IsPathRooted($PackagedExe)) { throw 'PackagedExe must be absolute.' }
    $PackagedExe = [IO.Path]::GetFullPath($PackagedExe)
    if (-not (Test-Path -LiteralPath $PackagedExe -PathType Leaf)) { throw 'Packaged executable is missing.' }
    if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $PackagedExe) 'Revu.Sidecar.exe') -PathType Leaf)) {
        throw 'Packaged sidecar must sit next to the executable.'
    }
}
$handoffLauncher = Join-Path $handoffRepo 'desktop\scripts\start.mjs'
if (-not (Test-Path -LiteralPath $handoffLauncher)) { throw 'Desktop launcher is missing.' }
$handoffFfmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
Push-Location $handoffRepo
try {
    if ([string]::IsNullOrWhiteSpace($PackagedExe)) {
        dotnet build src/Revu.Sidecar/Revu.Sidecar.csproj -c Release -p:Platform=x64 --artifacts-path artifacts/desktop-build
        if ($LASTEXITCODE -ne 0) { throw 'Sidecar build failed.' }
        $handoffExe = Join-Path $handoffRepo 'artifacts\desktop-build\bin\Revu.Sidecar\release_win-x64\Revu.Sidecar.exe'
    }
    $handoffRecordings = Join-Path $DataRoot 'recordings'
    New-Item -ItemType Directory -Path $handoffRecordings -Force | Out-Null
    $handoffFixtureName = 'synthetic ' + [char]0x00FC + ' space.mp4'
    & $handoffFfmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=640x360:rate=30' -f lavfi -i 'sine=frequency=440:sample_rate=48000' -t 3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -movflags +faststart (Join-Path $handoffRecordings $handoffFixtureName)
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture generation failed.' }
    if ([string]::IsNullOrWhiteSpace($PackagedExe)) {
        & node $handoffLauncher --isolated --data-root $DataRoot --sidecar $handoffExe --smoke
    } else {
        # Use the same credential-free environment as the development launcher.
        # Arguments cross the process boundary as values, with no shell interpolation.
        $handoffRuntime = Join-Path $handoffRepo 'desktop\runtime\overwolf.mjs'
        @'
import { spawn } from 'node:child_process';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
const { cleanRuntimeEnvironment } = await import(pathToFileURL(process.argv[2]).href);
const child = spawn(process.argv[3], ['--isolated', '--data-root', process.argv[4], '--smoke'], {
  cwd: path.dirname(process.argv[3]), env: cleanRuntimeEnvironment(), stdio: 'inherit', windowsHide: true,
});
const timer = setTimeout(() => { console.error('Packaged smoke timed out.'); child.kill(); }, 120000);
child.once('error', error => { clearTimeout(timer); console.error(error.message); process.exitCode = 1; });
child.once('exit', code => { clearTimeout(timer); process.exitCode = code ?? 1; });
'@ | & node --input-type=module - $handoffRuntime $PackagedExe $DataRoot
    }
    if ($LASTEXITCODE -ne 0) { throw "Host smoke failed; inspect private diagnostics under $DataRoot" }
    Write-Host "Smoke diagnostics: $DataRoot"
    Get-Content -LiteralPath (Join-Path $DataRoot 'electron-smoke.json')
} finally { Pop-Location }
