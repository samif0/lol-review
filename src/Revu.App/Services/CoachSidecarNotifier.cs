#nullable enable

using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.App.Services;

/// <summary>
/// App-layer implementation of the Core <see cref="ICoachSidecarNotifier"/>.
/// Fire-and-forget wrapper around <see cref="ICoachApiClient"/>. All calls
/// swallow exceptions so they can never bubble back into critical paths
/// (game save, review save, bookmark create).
/// </summary>
public sealed class CoachSidecarNotifier : ICoachSidecarNotifier
{
    private readonly ICoachApiClient _api;
    private readonly CoachSidecarService _sidecar;
    private readonly ILogger<CoachSidecarNotifier> _logger;

    public CoachSidecarNotifier(
        ICoachApiClient api,
        CoachSidecarService sidecar,
        ILogger<CoachSidecarNotifier> logger)
    {
        _api = api;
        _sidecar = sidecar;
        _logger = logger;
    }

    public async Task NotifyGameEndedAsync(long gameId, CancellationToken cancellationToken = default)
    {
        if (!_sidecar.IsHealthy) return;
        try
        {
            // Phase 1: build summary. Phase 3: compute features for ranker.
            // Phase 2: concepts (if review already exists; otherwise Save hook picks it up).
            await _api.BuildSummaryAsync(gameId, cancellationToken).ConfigureAwait(false);
            await _api.ComputeFeaturesAsync(gameId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Coach NotifyGameEndedAsync swallowed exception for game {GameId}", gameId);
        }
    }

    public async Task NotifyReviewSavedAsync(long gameId, CancellationToken cancellationToken = default)
    {
        if (!_sidecar.IsHealthy) return;
        try
        {
            await _api.ExtractConceptsAsync(gameId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Coach NotifyReviewSavedAsync swallowed exception for game {GameId}", gameId);
        }
    }

    public async Task NotifyBookmarkCreatedAsync(long bookmarkId, CancellationToken cancellationToken = default)
    {
        if (!_sidecar.IsHealthy) return;
        try
        {
            await _api.DescribeBookmarkAsync(bookmarkId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Coach NotifyBookmarkCreatedAsync swallowed exception for bookmark {BookmarkId}", bookmarkId);
        }
    }
}
