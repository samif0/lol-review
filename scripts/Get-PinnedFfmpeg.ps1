# Downloads the pinned ffmpeg build to deps\ffmpeg.exe (gitignored) for packaging and
# the packaged smoke test. CI and the release workflow both call this, so the URL and
# checksum live in exactly one place.
#
# Pin a month-end BtbN build (retained for two years, unlike daily builds). Verify the
# archive SHA-256 against the upstream checksums.sha256 and GitHub release asset digest
# before bumping either value.
#
# -ArchiveCache keeps the verified archive in that directory between runs (CI restores
# it with actions/cache). A cached archive is hashed against the pin before every use
# and downloaded again on a mismatch, so the cache can only save time. Without it the
# archive is downloaded fresh and deleted afterwards, which is what releases do.
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArchiveCache = ''
)
$ErrorActionPreference = 'Stop'

$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-31-13-27/ffmpeg-n8.1.2-50-g1a748fe2cd-win64-gpl-8.1.zip'
$expectedSha256 = '273ABB45F3F9F76C303E35FF39F5BB6C23C163AE65F6244A32B7D4A7F6CF0616'

function Test-PinnedArchive([string] $path) {
    (Test-Path -LiteralPath $path -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $expectedSha256)
}

$root = [IO.Path]::GetFullPath($RepositoryRoot)
$extracted = Join-Path $root 'ffmpeg_tmp'
if ($ArchiveCache) {
    $cacheRoot = [IO.Path]::GetFullPath($ArchiveCache)
    New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
    $archive = Join-Path $cacheRoot "ffmpeg-$expectedSha256.zip"
} else {
    $archive = Join-Path $root 'ffmpeg.zip'
}
try {
    if ($ArchiveCache -and (Test-PinnedArchive $archive)) {
        Write-Host 'Using the cached ffmpeg archive (SHA-256 verified)'
    } else {
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 300
        if (-not (Test-PinnedArchive $archive)) {
            $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
            Remove-Item -LiteralPath $archive -Force
            throw "ffmpeg checksum mismatch - expected $expectedSha256, got $actual."
        }
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
    $temporary = @($extracted)
    if (-not $ArchiveCache) { $temporary += $archive }
    foreach ($target in $temporary) {
        if ([IO.Path]::GetDirectoryName($target) -ne $root) { throw 'Unexpected cleanup path' }
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    }
}
