#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Builds saved-moment collections and cross-game mistake trends for active
/// objectives. The full saved playlists remain available for paginated revision,
/// including reviewed trends and notes whose recording has since been removed.
/// Each trend also says whether it is ready for a back-to-back review
/// (<see cref="PatternReviewGate.Readiness"/>) and which moments that review plays.
/// </summary>
public sealed class PatternsSnapshotBuilder
{
    // ── Mockup palette (mirror DashboardSnapshotBuilder; TODO: extract to Core) ──
    private const string GoldHex = "#f3c794";
    private const string WinHex = "#8ee7ba";   // PositiveHex equivalent
    private const string LossHex = "#f3a3a8";  // NegativeHex equivalent
    private const string NeutralHex = "#8a80a8";

    private readonly IEvidenceRepository _evidenceRepo;
    private readonly ILogger<PatternsSnapshotBuilder> _logger;

    public PatternsSnapshotBuilder(
        IEvidenceRepository evidenceRepo,
        ILogger<PatternsSnapshotBuilder> logger)
    {
        _evidenceRepo = evidenceRepo;
        _logger = logger;
    }

    public async Task<PatternsSnapshotDto> BuildAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;

        var cards = new List<PatternCardDto>();
        var reviewedCount = 0;
        var errorText = "";

        try
        {
            // Collections must remain reachable even after reviewing a trend.
            var rawPatterns = await _evidenceRepo.GetPatternCardsAsync(limit: int.MaxValue);
            var reviewedStamps = await _evidenceRepo.GetReviewedPatternsAsync();
            reviewedCount = await _evidenceRepo.CountReviewedPatternsAsync();
            var playlists = await LoadMomentsAsync(rawPatterns);

            foreach (var pattern in rawPatterns)
            {
                var rawMoments = playlists.GetValueOrDefault(pattern.PatternKey) ?? Array.Empty<PatternMoment>();

                var savedCollection = pattern.Kind == PatternConstants.KindSavedObjectiveEvidence;
                var isReviewed = !savedCollection && PatternReviewGate.IsReviewed(reviewedStamps, pattern.PatternKey, rawMoments);
                var newMoments = savedCollection ? 0 : PatternReviewGate.NewMomentCount(reviewedStamps, pattern.PatternKey, rawMoments);
                var (moments, playableCount) = MapMoments(rawMoments);
                // Saved collections never nudge. A trend's review plays only what can be watched.
                var readiness = savedCollection ? null : PatternReviewGate.Readiness(reviewedStamps, pattern.PatternKey,
                    rawMoments, i => moments[i].HasVod || moments[i].HasClip);

                var distinctGames = moments.Select(m => m.GameId).Distinct().Count();
                var momentCount = moments.Count;
                var totalMoments = rawMoments.Count;
                var unwatchable = totalMoments - playableCount;

                cards.Add(new PatternCardDto(
                    PatternKey: pattern.PatternKey,
                    Kind: pattern.Kind,
                    Title: pattern.Title,
                    Detail: pattern.Detail,
                    GameId: pattern.GameId,
                    ObjectiveId: pattern.ObjectiveId,
                    Severity: pattern.Severity,
                    SeverityLabel: pattern.Severity.ToUpperInvariant(),
                    // "high" -> negative red, else gold (mirror SeverityHex).
                    SeverityHex: pattern.Severity == "high" ? LossHex : GoldHex,
                    IsReviewed: isReviewed,
                    MomentCount: momentCount,
                    GameCount: distinctGames,
                    Subtitle: BuildSubtitle(momentCount, totalMoments, distinctGames),
                    // Carry-forward note write is DEFERRED — display-only placeholder.
                    CarryForwardNote: "",
                    Moments: moments,
                    NewMomentCount: newMoments,
                    TotalMomentCount: totalMoments,
                    UnwatchableMomentCount: unwatchable,
                    ReviewMode: savedCollection ? "saved" : "trend",
                    ReadyToReview: readiness?.IsReady ?? false,
                    ReviewMomentOrdinals: readiness?.ReviewIndexes.Select(i => moments[i].Ordinal).ToArray() ?? []));
            }
        }
        catch (Exception ex)
        {
            // A backend failure must not masquerade as "no patterns yet" — log
            // loud and surface it so the page renders its error panel.
            _logger.LogError(ex, "Patterns: pattern-card load failed");
            errorText = "Couldn't load patterns from the local database. See the sidecar log for details.";
        }

        // Keep reviewed trends and saved collections available for revision.
        // The viewer paginates the playlist instead of silently dropping moments.
        var pendingCount = cards.Count(card => card.ReviewMode == "trend" && !card.IsReviewed);
        cards = cards
            .OrderBy(card => card.ReviewMode == "saved" ? 2 : card.IsReviewed ? 1 : 0)
            .ToList();

        return new PatternsSnapshotDto(
            GeneratedAt: now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ReviewedPatternCount: reviewedCount,
            HasPending: pendingCount > 0,
            PendingCount: pendingCount,
            EmptyText: cards.Count == 0 && errorText.Length == 0
                ? "Save clips or bookmarks with your current learning objectives to revisit them here. "
                  + $"Mistake trends compare saved moments marked bad across at least two games in the last {PatternConstants.WindowDays} days."
                : "",
            Patterns: cards,
            ErrorText: errorText,
            WindowDays: PatternConstants.WindowDays);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Section builders
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve every card's ordered (oldest-first) raw moments in one read (the page used
    /// to run one query per card). Degrades to empty playlists on failure so a bad moment
    /// read never blanks the cards themselves.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<PatternMoment>>> LoadMomentsAsync(
        IReadOnlyList<ObjectivePatternCard> patterns)
    {
        try
        {
            return await _evidenceRepo.GetPatternMomentsAsync(patterns);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patterns: moment load failed for {Count} cards", patterns.Count);
            return new Dictionary<string, IReadOnlyList<PatternMoment>>();
        }
    }

    /// <summary>All saved moments plus the number with playable media.</summary>
    private static (IReadOnlyList<PatternMomentDto> Moments, int PlayableCount) MapMoments(IReadOnlyList<PatternMoment> moments)
    {
        // A pruned recording degrades to the graceful no-VOD state instead of a
        // player that errors with "Could not load this clip" (PatternMediaProbe,
        // shared with the review nudge). The viewer limits each batch, while notes
        // remain revisitable even after the source video has gone. Never
        // substitute raw event anchors.
        var probe = new PatternMediaProbe();
        var ordinal = 0;
        var mapped = moments.Select(m => MapMoment(m, ++ordinal, probe.Recording(m), probe.HasClip(m))).ToList();
        return (mapped, mapped.Count(moment => moment.HasVod || moment.HasClip));
    }

    /// <summary>Describe the saved collection without implying every match event is included.</summary>
    private static string BuildSubtitle(int shownCount, int totalCount, int gameCount)
    {
        if (totalCount == 0)
        {
            return "No moments are still pending for this pattern.";
        }
        if (shownCount == 0)
        {
            return $"{totalCount} moment{(totalCount == 1 ? "" : "s")} counted, none still watchable (recordings gone, no clips kept).";
        }
        var games = $"{gameCount} game{(gameCount == 1 ? "" : "s")}";
        if (shownCount < totalCount)
        {
            return $"{shownCount} of {totalCount} moments across {games}";
        }
        var moments = $"{shownCount} moment{(shownCount == 1 ? "" : "s")}";
        return $"{moments} across {games}";
    }

    /// <summary>Mirror of PatternMomentItem's display projection (no brushes).</summary>
    private static PatternMomentDto MapMoment(PatternMoment m, int ordinal,
        (bool Playable, double GameTimeAtVideoStart) recording, bool clipOnDisk)
    {
        var vodOnDisk = recording.Playable;
        var gameTimeAtVideoStart = recording.GameTimeAtVideoStart;
        var championLabel = string.IsNullOrWhiteSpace(m.ChampionName) ? "Game" : m.ChampionName;
        var resultLabel = m.Win ? "WIN" : "LOSS";
        var resultHex = m.Win ? WinHex : LossHex;
        var note = m.Note ?? "";
        var polarity = m.Polarity;
        // A bookmark is a virtual clip: a bounded window from the existing recording.
        // Keep TimeLabel at the bookmark itself and never export a file.
        var (startTimeSeconds, endTimeSeconds) = PatternMediaProbe.Window(m);

        // A start-less moment (game-level anchor: recurring tag, rule break) has
        // no in-game second — render no time rather than a fabricated "0:00".
        var timeLabel = m.StartTimeSeconds is int startS ? FormatTime(startS) : "";
        var videoHeaderText = timeLabel.Length > 0
            ? $"{championLabel} · {resultLabel} · {timeLabel}"
            : $"{championLabel} · {resultLabel}";

        return new PatternMomentDto(
            EvidenceId: m.EvidenceId,
            GameId: m.GameId,
            Ordinal: ordinal,
            ChampionName: m.ChampionName,
            ChampionLabel: championLabel,
            Win: m.Win,
            ResultLabel: resultLabel,
            ResultHex: resultHex,
            GameTimestamp: m.GameTimestamp,
            StartTimeSeconds: startTimeSeconds,
            EndTimeSeconds: endTimeSeconds,
            TimeLabel: timeLabel,
            VideoHeaderText: videoHeaderText,
            Title: m.Title,
            Note: note,
            HasNote: !string.IsNullOrWhiteSpace(note),
            Polarity: polarity,
            PolarityLabel: PolarityLabel(polarity),
            AccentHex: PolarityHex(polarity),
            SourceKind: m.SourceKind,
            // An empty VodPath also keeps the note flow from attempting a clip
            // extraction against the missing file (the endpoint's hasVod gate).
            VodPath: vodOnDisk ? m.VodPath : "",
            HasVod: vodOnDisk,
            ClipPath: clipOnDisk ? m.ClipPath : "",
            HasClip: clipOnDisk,
            GameTimeAtVideoStart: gameTimeAtVideoStart,
            BookmarkId: m.BookmarkId);
    }

    /// <summary>Mirror of PatternMomentItem.PolarityLabel.</summary>
    private static string PolarityLabel(string polarity) => polarity switch
    {
        "good" => "GOOD",
        "bad" => "BAD",
        _ => "NEUTRAL",
    };

    /// <summary>Mirror of PatternMomentItem.AccentHex (no SolidColorBrush).</summary>
    private static string PolarityHex(string polarity) => polarity switch
    {
        "good" => WinHex,
        "bad" => LossHex,
        _ => NeutralHex,
    };

    private static string FormatTime(int s) => $"{s / 60}:{s % 60:D2}";
}
