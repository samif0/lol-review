param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"

if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild not found at $msbuild"
}

Push-Location $repo
try {
    & $msbuild "src\Revu.App\Revu.App.csproj" `
        /t:Build `
        /p:Configuration=$Configuration `
        /p:Platform=$Platform `
        /p:RuntimeIdentifier=$RuntimeIdentifier `
        /m `
        /verbosity:minimal
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}
