param([ValidateSet('start','status','stop')][string]$Action = 'status')
$ErrorActionPreference = 'Stop'
# Uses the running app's existing local authentication. Never print its token.
$connection = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'Revu/sidecar.json') -Raw | ConvertFrom-Json
$headers = @{ Authorization = 'Bearer ' + $connection.token }
$method = if ($Action -eq 'status') { 'Get' } else { 'Post' }
Invoke-RestMethod -Uri "http://127.0.0.1:$($connection.port)/api/diagnostics/practice/$Action" `
    -Method $method -Headers $headers -TimeoutSec 10 | ConvertTo-Json -Depth 5
