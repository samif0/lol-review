$ErrorActionPreference = 'Stop'
$recorderDesktopRoot = Split-Path -Parent $PSScriptRoot
$recorderPreviousEmail = [Environment]::GetEnvironmentVariable('OW_CLI_EMAIL', 'Process')
$recorderPreviousApiKey = [Environment]::GetEnvironmentVariable('OW_CLI_API_KEY', 'Process')
$recorderPreviousDevKey = [Environment]::GetEnvironmentVariable('OW_DEV_KEY', 'Process')
$recorderSecureKey = Read-Host 'Overwolf developer key (used only for this launch)' -AsSecureString
$recorderKeyPointer = [IntPtr]::Zero
try {
    $recorderKeyPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($recorderSecureKey)
    $env:OW_DEV_KEY = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($recorderKeyPointer)
    [Environment]::SetEnvironmentVariable('OW_CLI_EMAIL', $null, 'Process')
    [Environment]::SetEnvironmentVariable('OW_CLI_API_KEY', $null, 'Process')
    Push-Location -LiteralPath $recorderDesktopRoot
    try { & node 'scripts/start-recorder.mjs'; $recorderExitCode = $LASTEXITCODE }
    finally { Pop-Location }
} finally {
    if ($recorderKeyPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($recorderKeyPointer) }
    [Environment]::SetEnvironmentVariable('OW_CLI_EMAIL', $recorderPreviousEmail, 'Process')
    [Environment]::SetEnvironmentVariable('OW_CLI_API_KEY', $recorderPreviousApiKey, 'Process')
    [Environment]::SetEnvironmentVariable('OW_DEV_KEY', $recorderPreviousDevKey, 'Process')
    $recorderSecureKey.Dispose()
}
exit $recorderExitCode
