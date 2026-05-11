param(
    [string]$Configuration = "Debug",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repo
try {
    dotnet test "src\Revu.Core.Tests\Revu.Core.Tests.csproj" --no-restore -c $Configuration -p:Platform=$Platform
}
finally {
    Pop-Location
}
