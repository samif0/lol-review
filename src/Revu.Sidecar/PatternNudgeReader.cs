#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// GET /api/patterns/nudge: whether Revu should quietly say a mistake trend is ready for
/// a back-to-back review. Applies <see cref="PatternReviewGate.Readiness"/> to every
/// trend, the same rule the Patterns page uses, then holds the nudge while the player is
/// in champ select or a game ("live") or within
/// <see cref="PatternConstants.NudgeQuietHours"/> hours of any review ("recent_review").
/// Saved collections never count.
///
/// <para>The shell asks on navigation, so this stays light: three evidence reads, and
/// file checks that are remembered for <see cref="ProbeLifetime"/> (files rarely
/// appear or vanish within a minute; review and moment changes are read fresh every
/// time, so finishing a review clears the nudge at once). Any failure degrades to "no
/// nudge" with ErrorText set, so a broken check can never nag.</para>
/// </summary>
public sealed class PatternNudgeReader(
    IEvidenceRepository evidence,
    LcuLiveState live,
    ILogger<PatternNudgeReader> logger,
    TimeProvider? time = null,
    Func<string, bool>? fileExists = null)
{
    public const string QuietLive = "live";
    public const string QuietRecentReview = "recent_review";
    public const string FailedText = "Couldn't check your patterns.";
    public static readonly TimeSpan ProbeLifetime = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _probeGate = new();
    private PatternMediaProbe? _probe;
    private DateTimeOffset _probeCreated;

    public async Task<PatternNudgeDto> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var cards = await evidence.GetPatternCardsAsync(limit: int.MaxValue);
            var trends = cards.Where(card => card.Kind == PatternConstants.KindBadObjectiveEvidence).ToList();
            var playlists = trends.Count == 0
                ? new Dictionary<string, IReadOnlyList<PatternMoment>>()
                : await evidence.GetPatternMomentsAsync(trends);
            var stamps = await evidence.GetReviewedPatternsAsync();
            ct.ThrowIfCancellationRequested();

            var ready = new List<(ObjectivePatternCard Card, PatternReadiness Readiness)>();
            lock (_probeGate)
            {
                var probe = CurrentProbe();
                foreach (var trend in trends)
                {
                    var moments = playlists.GetValueOrDefault(trend.PatternKey) ?? Array.Empty<PatternMoment>();
                    var readiness = PatternReviewGate.Readiness(stamps, trend.PatternKey, moments,
                        i => probe.IsWatchable(moments[i]));
                    if (readiness.IsReady) ready.Add((trend, readiness));
                }
            }

            var quiet = live.IsInChampSelect || live.IsGameInProgress ? QuietLive
                : PatternReviewGate.InQuietPeriod(stamps, _time.GetUtcNow().ToUnixTimeSeconds()) ? QuietRecentReview
                : "";
            if (ready.Count == 0)
                return new PatternNudgeDto(false, 0, "", "", 0, 0, quiet);

            // Cards arrive in severity order; the first ready one is the one to open.
            var (first, firstReadiness) = ready[0];
            var objectiveTitle = cards.FirstOrDefault(card => card.Kind == PatternConstants.KindSavedObjectiveEvidence
                && card.ObjectiveId == first.ObjectiveId)?.Title;
            return new PatternNudgeDto(
                Notify: quiet.Length == 0,
                ReadyCount: ready.Count,
                PatternKey: first.PatternKey,
                Title: string.IsNullOrWhiteSpace(objectiveTitle) ? first.Title : objectiveTitle,
                MomentCount: firstReadiness.UnreviewedWatchableCount,
                GameCount: firstReadiness.UnreviewedWatchableGames,
                QuietReason: quiet);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Patterns: nudge check failed");
            return new PatternNudgeDto(false, 0, "", "", 0, 0, "", FailedText);
        }
    }

    // Under _probeGate. A probe remembers each path it checked; a fresh one replaces it
    // once it is older than ProbeLifetime.
    private PatternMediaProbe CurrentProbe()
    {
        var now = _time.GetUtcNow();
        if (_probe is null || now - _probeCreated >= ProbeLifetime)
        {
            _probe = new PatternMediaProbe(fileExists);
            _probeCreated = now;
        }
        return _probe;
    }
}
