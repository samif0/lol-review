#nullable enable

using System.Text.Json;
using LoLReview.Core.Constants;
using LoLReview.Core.Models;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Lcu;

public sealed class GameEndCaptureService : IGameEndCaptureService
{
    private readonly ILcuClient _lcuClient;
    private readonly ILogger<GameEndCaptureService> _logger;

    public GameEndCaptureService(
        ILcuClient lcuClient,
        ILogger<GameEndCaptureService> logger)
    {
        _lcuClient = lcuClient;
        _logger = logger;
    }

    public async Task<GameStats?> CaptureAsync(
        IReadOnlyList<GameEvent> liveEvents,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < GameConstants.EogStatsRetryAttempts; attempt++)
        {
            CoreDiagnostics.WriteVerbose(
                $"LCU: GameEndCapture attempt={attempt + 1}/{GameConstants.EogStatsRetryAttempts}");
            var eogData = await _lcuClient.GetEndOfGameStatsAsync(cancellationToken).ConfigureAwait(false);
            if (eogData is JsonElement eog)
            {
                // Dump full EOG JSON on first successful fetch so we can debug team kill parsing
                if (attempt == 0)
                {
                    try
                    {
                        var raw = eog.GetRawText();
                        var dumpPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "LoLReviewData", "last_eog_dump.json");
                        await File.WriteAllTextAsync(dumpPath, raw, cancellationToken).ConfigureAwait(false);
                    }
                    catch { /* best-effort diagnostic */ }
                }

                var stats = StatsExtractor.ExtractFromEog(eog, _logger);
                if (stats is not null)
                {
                    var summonerName = await TryGetCurrentSummonerNameAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(summonerName))
                    {
                        stats.SummonerName = summonerName;
                    }

                    stats.LiveEvents = [.. liveEvents];
                    CoreDiagnostics.WriteVerbose(
                        $"LCU: GameEndCapture success gameId={stats.GameId} attempt={attempt + 1}");
                    return stats;
                }
            }

            if (attempt + 1 < GameConstants.EogStatsRetryAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(GameConstants.EogStatsRetryDelayS),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogWarning("Could not retrieve end-of-game stats after retries");
        CoreDiagnostics.WriteVerbose("LCU: GameEndCapture failed after retries");
        return null;
    }

    private async Task<string?> TryGetCurrentSummonerNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            var summoner = await _lcuClient.GetCurrentSummonerAsync(cancellationToken).ConfigureAwait(false);
            if (summoner is not JsonElement summonerElement)
            {
                return null;
            }

            return summonerElement.GetPropertyOrDefault("displayName", "") is { Length: > 0 } displayName
                ? displayName
                : summonerElement.GetPropertyOrDefault("gameName", "Unknown");
        }
        catch
        {
            return null;
        }
    }
}
