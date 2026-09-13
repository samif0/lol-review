param([Parameter(Mandatory = $true)][ValidateSet('gep', 'recorder')][string]$Mode)

$ErrorActionPreference = 'Stop'
# Read-Host masks input and keeps the key out of shell history and arguments.
# This environment exists only for this PowerShell process and its owned child.
$probeKey = Read-Host 'Approved Overwolf developer key (used for this launch only; never saved)' -AsSecureString
$probeKeyPointer = [IntPtr]::Zero
$previousDevKey = $env:OW_DEV_KEY
$previousConsoleEmail = $env:OW_CLI_EMAIL
$previousConsoleKey = $env:OW_CLI_API_KEY
try {
    $probeKeyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($probeKey)
    $env:OW_DEV_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($probeKeyPointer)
    if ([string]::IsNullOrWhiteSpace($env:OW_DEV_KEY)) { throw 'A developer key is required.' }
    # Console credentials take precedence in Dev Mode; select the prompted key.
    $env:OW_CLI_EMAIL = $null
    $env:OW_CLI_API_KEY = $null
    & node (Join-Path $PSScriptRoot 'launch.mjs') $Mode --enable-live
    $probeExitCode = $LASTEXITCODE
}
finally {
    $env:OW_DEV_KEY = $previousDevKey
    $env:OW_CLI_EMAIL = $previousConsoleEmail
    $env:OW_CLI_API_KEY = $previousConsoleKey
    if ($probeKeyPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($probeKeyPointer) }
    $probeKey.Dispose()
}
exit $probeExitCode
