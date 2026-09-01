#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only Patterns snapshot served at GET /api/patterns.
///
/// <para>
/// Reproduces the WinUI Pattern Review surface
/// (<c>PatternReviewViewModel</c> + <c>PatternMomentItem</c>) EXACTLY for display
/// — title / severity / subtitle on each card and the labels/accent hexes on each
/// moment — minus all WinUI/dispatcher concerns, and emits the camelCase JSON
/// contract (see <see cref="PatternsSnapshotDto"/> and
/// desktop/ui/sample-patterns.json). It deliberately does NOT reference the WinUI
/// ViewModel — only the Core <see cref="IEvidenceRepository"/>.
/// </para>
///
/// <para>
/// Unlike the WinUI viewer (which loads one pattern at a time), the snapshot
/// carries every pattern card with its full ordered moment playlist so the Tauri
/// Patterns page can render the cross-game cards and drill into each moment
/// without a second round-trip.
/// </para>
///
/// <para>
/// READ-ONLY: "Mark reviewed" (and the per-moment note/clip writes) are NOT here
/// — that is a write and is DEFERRED. We surface the reviewed flag + a
/// carry-forward note placeholder for display only. Per-pattern moment loads are
/// each wrapped in try/catch that degrades to an empty playlist so one bad
/// pattern never blanks the whole page.
/// </para>
///
/// <para>
/// COLOR PARITY: mirrors <see cref="DashboardSnapshotBuilder"/> — the glass-aurora
/// mockup palette is hardcoded here (win/positive #8ee7ba, loss/negative #f3a3a8,
/// gold #f3c794, neutral #8a80a8) because the WinUI
/// <c>Revu.App.Styling.AppSemanticPalette</c> isn't visible to Core. TODO: lift
/// these constants into Revu.Core so the app and the sidecar share one source.
/// </para>
/// </summary>
public sealed class PatternsSnapshotBuilder
{
    // ── Tunables (mirror PatternReviewViewModel / dashboard nag) ────────────────
    private const int PatternCardLimit = 6;

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
            // Fetch the FULL candidate set so the review gate runs before the
            // display cap — a reviewed-closed card must never crowd a pending
            // one out of the page.
            var rawPatterns = await _evidenceRepo.GetPatternCardsAsync(limit: PatternConstants.PatternCandidateLimit);
            var reviewedStamps = await _evidenceRepo.GetReviewedPatternsAsync();
            reviewedCount = await _evidenceRepo.CountReviewedPatternsAsync();

            foreach (var pattern in rawPatterns)
            {
                var rawMoments = await LoadMomentsAsync(pattern);
                var moments = MapMoments(rawMoments);

                // Reviewed with re-arm hysteresis (PatternReviewGate) — the ONE
                // rule the dashboard nag also applies, so page and nag agree.
                var isReviewed = PatternReviewGate.IsReviewed(reviewedStamps, pattern.PatternKey, rawMoments);
                var newMoments = isReviewed
                    ? 0
                    : PatternReviewGate.NewMomentCount(reviewedStamps, pattern.PatternKey, rawMoments);

                var distinctGames = moments.Select(m => m.GameId).Distinct().Count();
                var momentCount = moments.Count;

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
                    Subtitle: BuildSubtitle(momentCount, distinctGames),
                    // Carry-forward note write is DEFERRED — display-only placeholder.
                    CarryForwardNote: "",
                    Moments: moments,
                    NewMomentCount: newMoments));
            }
        }
        catch (Exception ex)
        {
            // A backend failure must not masquerade as "no patterns yet" — log
            // loud and surface it so the page renders its error panel.
            _logger.LogError(ex, "Patterns: pattern-card load failed");
            errorText = "Couldn't load patterns from the local database. See the sidecar log for details.";
        }

        // Honest counts over the full candidate set; the DISPLAY list is then
        // capped pending-first (stable within each group — the repo already
        // ordered by severity then volume).
        var pendingCount = cards.Count(c => !c.IsReviewed);
        cards = cards
            .Where(c => !c.IsReviewed)
            .Concat(cards.Where(c => c.IsReviewed))
            .Take(PatternCardLimit)
            .ToList();

        return new PatternsSnapshotDto(
            GeneratedAt: now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ReviewedPatternCount: reviewedCount,
            HasPending: pendingCount > 0,
            PendingCount: pendingCount,
            EmptyText: cards.Count == 0 && errorText.Length == 0
                ? $"No recurring patterns on your learning objectives in the last {PatternConstants.WindowDays} days of ranked games. "
                  + "Patterns build from the objectives you set — clips you mark bad on them, "
                  + "structured criteria that keep failing, and recurrences of the events they track."
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

    private static IReadOnlyList<PatternMomentDto> MapMoments(IReadOnlyList<PatternMoment> moments)
    {
        // vod_files rows outlive the recordings they point at (Ascent retention
        // prunes old files), so probe the disk before advertising a playable
        // VOD — same File.Exists shape as GamesSnapshotBuilder — and degrade a
        // pruned one to the graceful no-VOD state instead of a player that
        // errors with "Could not load this clip". One probe per distinct path:
        // a playlist's moments mostly share their game's VOD.
        var onDisk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool VodOnDisk(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (!onDisk.TryGetValue(path, out var exists))
            {
                exists = File.Exists(path);
                onDisk[path] = exists;
            }
            return exists;
        }

        var ordinal = 0;
        return moments.Select(m => MapMoment(m, ++ordinal, VodOnDisk(m.VodPath))).ToList();
    }

    /// <summary>Mirror of PatternReviewViewModel.PatternSubtitle.</summary>
    private static string BuildSubtitle(int momentCount, int gameCount)
    {
        if (momentCount == 0)
        {
            return "No moments are still pending for this pattern.";
        }
        var moments = $"{momentCount} moment{(momentCount == 1 ? "" : "s")}";
        var games = $"{gameCount} game{(gameCount == 1 ? "" : "s")}";
        return $"{moments} across {games}";
    }

    /// <summary>Mirror of PatternMomentItem's display projection (no brushes).</summary>
    private static PatternMomentDto MapMoment(PatternMoment m, int ordinal, bool vodOnDisk)
    {
        var championLabel = string.IsNullOrWhiteSpace(m.ChampionName) ? "Game" : m.ChampionName;
        var resultLabel = m.Win ? "WIN" : "LOSS";
        var resultHex = m.Win ? WinHex : LossHex;
        var note = m.Note ?? "";
        var polarity = m.Polarity;

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
            StartTimeSeconds: m.StartTimeSeconds,
            EndTimeSeconds: m.EndTimeSeconds,
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
            HasVod: vodOnDisk);
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
