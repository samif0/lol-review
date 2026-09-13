# Read-only, schema-only probe. Never writes authentication tokens or player identities.
param([Parameter(Mandatory = $true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$report = [ordered]@{ Version = 1; TimestampUtc = [DateTimeOffset]::UtcNow; LiveClient = 'unavailable'; Lcu = 'unavailable' }
try {
    $live = Invoke-RestMethod 'https://127.0.0.1:2999/liveclientdata/allgamedata' -SkipCertificateCheck -TimeoutSec 2
    $report.LiveClient = @{
        RootFields = @($live.PSObject.Properties.Name)
        ActivePlayerFields = @($live.activePlayer.PSObject.Properties.Name)
        ChampionStatsFields = @($live.activePlayer.championStats.PSObject.Properties.Name)
        PlayerFields = @($live.allPlayers[0].PSObject.Properties.Name)
        EventTypes = @($live.events.Events.EventName | Sort-Object -Unique)
    }
} catch { }
try {
    $client = Get-CimInstance Win32_Process -Filter "Name = 'LeagueClientUx.exe'" | Select-Object -First 1
    $port = [regex]::Match($client.CommandLine, '--app-port=(\d+)').Groups[1].Value
    $token = [regex]::Match($client.CommandLine, '--remoting-auth-token=([\w_-]+)').Groups[1].Value
    if (-not $port -or -not $token) { throw 'LCU not running' }
    $headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('riot:' + $token)) }
    $base = 'https://127.0.0.1:' + $port
    $phase = Invoke-RestMethod ($base + '/lol-gameflow/v1/gameflow-phase') -Headers $headers -SkipCertificateCheck -TimeoutSec 3
    $history = Invoke-RestMethod ($base + '/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=1') -Headers $headers -SkipCertificateCheck -TimeoutSec 3
    $game = @($history.games.games)[0].gameId
    $timeline = Invoke-RestMethod ($base + '/lol-match-history/v1/game-timelines/' + $game) -Headers $headers -SkipCertificateCheck -TimeoutSec 3
    $intervals = for ($i = 1; $i -lt $timeline.frames.Count; $i++) { $timeline.frames[$i].timestamp - $timeline.frames[$i - 1].timestamp }
    $report.Lcu = @{
        Phase = $phase
        TimelineFields = @($timeline.PSObject.Properties.Name)
        FrameCount = @($timeline.frames).Count
        FrameIntervalsMs = @($intervals | Sort-Object -Unique)
        ParticipantFields = @($timeline.frames[0].participantFrames.'1'.PSObject.Properties.Name)
        EventTypes = @($timeline.frames.events.type | Sort-Object -Unique)
    }
} catch { $report.LcuErrorType = $_.Exception.GetType().Name }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Saved source schema observations to $OutputPath"
