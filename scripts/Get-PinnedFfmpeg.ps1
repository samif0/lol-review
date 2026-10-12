# Downloads the pinned ffmpeg build to deps\ffmpeg.exe (gitignored) for packaging and
# the packaged smoke test. CI and the release workflow both call this, so the URL and
# checksum live in exactly one place.
#
# Pin a month-end BtbN build (retained for two years, unlike daily builds). Verify the
# archive SHA-256 against the upstream checksums.sha256 and GitHub release asset digest
# before bumping either value.
param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'

$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-31-13-27/ffmpeg-n8.1.2-50-g1a748fe2cd-win64-gpl-8.1.zip'
$expectedSha256 = '273ABB45F3F9F76C303E35FF39F5BB6C23C163AE65F6244A32B7D4A7F6CF0616'

$root = [IO.Path]::GetFullPath($RepositoryRoot)
$archive = Join-Path $root 'ffmpeg.zip'
$extracted = Join-Path $root 'ffmpeg_tmp'
try {
    Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 300
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actual -ne $expectedSha256) {
        throw "ffmpeg checksum mismatch - expected $expectedSha256, got $actual."
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted -Force
    $executables = @(Get-ChildItem -LiteralPath $extracted -Recurse -File -Filter 'ffmpeg.exe' |
        Where-Object { $_.Directory.Name -eq 'bin' })
    if ($executables.Count -ne 1) { throw 'Expected exactly one ffmpeg.exe in the verified archive.' }
    $deps = Join-Path $root 'deps'
    New-Item -ItemType Directory -Force -Path $deps | Out-Null
    Copy-Item -LiteralPath $executables[0].FullName -Destination (Join-Path $deps 'ffmpeg.exe') -Force
    Write-Host 'ffmpeg ready (archive SHA-256 verified)'
} finally {
    foreach ($target in @($archive, $extracted)) {
        if ([IO.Path]::GetDirectoryName($target) -ne $root) { throw 'Unexpected cleanup path' }
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
}
