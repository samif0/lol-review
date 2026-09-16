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

            foreach (var pattern in rawPatterns)
            {
                var rawMoments = await LoadMomentsAsync(pattern);

                var savedCollection = pattern.Kind == PatternConstants.KindSavedObjectiveEvidence;
                var isReviewed = !savedCollection && PatternReviewGate.IsReviewed(reviewedStamps, pattern.PatternKey, rawMoments);
                var newMoments = savedCollection ? 0 : PatternReviewGate.NewMomentCount(reviewedStamps, pattern.PatternKey, rawMoments);
                var (moments, playableCount) = MapMoments(rawMoments);

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
                    ReviewMode: savedCollection ? "saved" : "trend"));
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
    /// Resolve one pattern's ordered (oldest-first) raw moments. Degrades to an
    /// empty playlist on failure so a single bad pattern never blanks the page.
    /// </summary>
    private async Task<IReadOnlyList<PatternMoment>> LoadMomentsAsync(ObjectivePatternCard pattern)
    {
        try
        {
            return await _evidenceRepo.GetPatternMomentsAsync(pattern);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Patterns: moment load failed for {Kind}", pattern.Kind);
            return Array.Empty<PatternMoment>();
        }
    }

    /// <summary>All saved moments plus the number with playable media.</summary>
    private static (IReadOnlyList<PatternMomentDto> Moments, int PlayableCount) MapMoments(IReadOnlyList<PatternMoment> moments)
    {
        // vod_files rows may outlive the recordings they point at (files can be
        // moved or removed), so probe the disk before advertising a playable
        // VOD — same File.Exists shape as GamesSnapshotBuilder — and degrade a
        // pruned one to the graceful no-VOD state instead of a player that
        // errors with "Could not load this clip". One probe per distinct path:
        // a playlist's moments mostly share their game's VOD. Clip files get the
        // same probe (a kept clip outlives its game's recording).
        var onDisk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool OnDisk(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!onDisk.TryGetValue(path, out var exists))
            {
                exists = File.Exists(path);
                onDisk[path] = exists;
            }
            return exists;
        }

        // The viewer limits each batch, while notes remain revisitable even
        // after the source video has gone. Never substitute raw event anchors.
        var saved = moments
            .Select(m => (Moment: m, HasVod: OnDisk(m.VodPath), HasClip: OnDisk(m.ClipPath)))
            .ToList();
        var ordinal = 0;
        var mapped = saved.Select(x => MapMoment(x.Moment, ++ordinal, x.HasVod, x.HasClip)).ToList();
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
    private static PatternMomentDto MapMoment(PatternMoment m, int ordinal, bool vodOnDisk, bool clipOnDisk)
    {
        var gameTimeAtVideoStart = 0d;
        if (vodOnDisk)
        {
            try { gameTimeAtVideoStart = RecordingTimeline.ReadGameTimeAtVideoStart(m.VodPath, m.GameId); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { vodOnDisk = false; }
        }
        var championLabel = string.IsNullOrWhiteSpace(m.ChampionName) ? "Game" : m.ChampionName;
        var resultLabel = m.Win ? "WIN" : "LOSS";
        var resultHex = m.Win ? WinHex : LossHex;
        var note = m.Note ?? "";
        var polarity = m.Polarity;
        var startTimeSeconds = m.StartTimeSeconds;
        var endTimeSeconds = m.EndTimeSeconds;
        if (m.SourceKind == "bookmark" && startTimeSeconds is int point)
        {
            // Virtual clip: play a bounded window from the existing recording.
            // Keep TimeLabel at the bookmark itself and never export a file.
            var upperBound = m.GameDurationSeconds > 0 ? m.GameDurationSeconds : int.MaxValue;
            point = Math.Clamp(point, 0, upperBound);
            startTimeSeconds = Math.Max(0, point - 15);
            endTimeSeconds = (int)Math.Min(upperBound, (long)point + 15);
        }

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
