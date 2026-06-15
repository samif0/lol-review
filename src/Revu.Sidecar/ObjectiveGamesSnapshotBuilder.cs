#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only ObjectiveGames snapshot served at
/// GET /api/objective/games?id=N.
///
/// <para>
/// Reproduces <c>ObjectiveGamesViewModel.LoadAsync</c>'s data loading +
/// formatting (header, games list, derived counters, evidence ledger) minus all
/// WinUI/dispatcher concerns, emitting the camelCase JSON contract (see
/// <see cref="ObjectiveGamesDto"/> and desktop/ui/sample-objective-games.json).
/// It references only the Core repo interfaces — never the WinUI ViewModel.
/// </para>
///
/// <para>
/// READ-ONLY ABSOLUTE: only repo read methods are called
/// (<see cref="IObjectivesRepository.GetAsync"/>,
/// <see cref="IObjectivesRepository.GetGamesForObjectiveAsync"/>,
/// <see cref="IEvidenceRepository.GetForObjectiveAsync"/> with the default
/// includeDismissed=false). No writes/migrations. Every "Watch VOD"/"Review"
/// jump is plain frontend navigation.
/// </para>
///
/// <para>
/// Like the other builders, the load is wrapped in try/catch that degrades to
/// empty so one failing query never blanks the whole page.
/// </para>
/// </summary>
public sealed class ObjectiveGamesSnapshotBuilder
{
    // ── Palette (mirrors the other builders; TODO: extract to Revu.Core) ──────
    private const string WinHex = "#8ee7ba";
    private const string LossHex = "#f3a3a8";
    // Practiced badge: positive (green) when practiced, neutral when skipped.
    private const string PositiveHex = "#8ee7ba";
    private const string PositiveDimHex = "#10221a";
    private const string NeutralHex = "#a79ec2";
    private const string NeutralDimHex = "#16131f";
    // Evidence polarity accents (good/bad/neutral).
    private const string GoodHex = "#8ee7ba";
    private const string BadHex = "#f3a3a8";
    private const string EvidenceNeutralHex = "#a79ec2";

    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IEvidenceRepository _evidenceRepo;
    private readonly ILogger<ObjectiveGamesSnapshotBuilder> _logger;

    public ObjectiveGamesSnapshotBuilder(
        IObjectivesRepository objectivesRepo,
        IEvidenceRepository evidenceRepo,
        ILogger<ObjectiveGamesSnapshotBuilder> logger)
    {
        _objectivesRepo = objectivesRepo;
        _evidenceRepo = evidenceRepo;
        _logger = logger;
    }

    public async Task<ObjectiveGamesDto> BuildAsync(long objectiveId, CancellationToken ct = default)
    {
        var generatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

        // ── Objective header ────────────────────────────────────────────────
        var title = "";
        var status = "";
        try
        {
            var obj = await _objectivesRepo.GetAsync(objectiveId);
            if (obj is not null)
            {
                title = obj.Title;
                var statusWord = string.Equals(obj.Status, "active", StringComparison.OrdinalIgnoreCase)
                    ? "Active"
                    : "Completed";
                status = $"{statusWord} • {ObjectivePhases.ToDisplayLabel(obj.Phase)}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ObjectiveGames: header load failed for {ObjectiveId}", objectiveId);
        }

        // ── Games list ──────────────────────────────────────────────────────
        var games = new List<ObjectiveGameRowDto>();
        try
        {
            var entries = await _objectivesRepo.GetGamesForObjectiveAsync(objectiveId);
            foreach (var g in entries)
            {
                games.Add(new ObjectiveGameRowDto(
                    GameId: g.GameId,
                    ChampionName: g.ChampionName ?? "",
                    Win: g.Win,
                    ResultText: g.Win ? "W" : "L",
                    ResultColorHex: g.Win ? WinHex : LossHex,
                    DateText: FormatDate(g.Timestamp),
                    KdaText: $"{g.Kills:F0}/{g.Deaths:F0}/{g.Assists:F0}",
                    Practiced: g.Practiced,
                    PracticedText: g.Practiced ? "Practiced" : "Skipped",
                    PracticedColorHex: g.Practiced ? PositiveHex : NeutralHex,
                    PracticedDimColorHex: g.Practiced ? PositiveDimHex : NeutralDimHex,
                    ExecutionNote: g.ExecutionNote ?? "",
                    HasExecutionNote: !string.IsNullOrWhiteSpace(g.ExecutionNote)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ObjectiveGames: games load failed for {ObjectiveId}", objectiveId);
        }

        // ── Evidence ledger (dismissed excluded by default) ─────────────────
        var evidence = new List<ObjectiveEvidenceRowDto>();
        try
        {
            var items = await _evidenceRepo.GetForObjectiveAsync(objectiveId);
            foreach (var e in items)
            {
                var polarity = EvidencePolarities.Normalize(e.Polarity);
                var date = FormatDate(e.GameTimestamp ?? 0);
                var time = e.StartTimeSeconds is int s ? FormatClock(s) : "";
                var meta = JoinSkippingBlanks("  /  ", e.ChampionName ?? "", date, time);
                var displayNote = BuildDisplayNote(e.Note ?? "", e.Title ?? "");

                evidence.Add(new ObjectiveEvidenceRowDto(
                    GameId: e.GameId,
                    Title: e.Title ?? "",
                    MetaText: meta,
                    DisplayNote: displayNote,
                    HasDisplayNote: displayNote.Length > 0,
                    Polarity: polarity,
                    PolarityLabel: PolarityLabel(polarity),
                    PolarityColorHex: PolarityHex(polarity)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ObjectiveGames: evidence load failed for {ObjectiveId}", objectiveId);
        }

        var practicedCount = games.Count(g => g.Practiced);
        var hasGames = games.Count > 0;
        var hasEvidence = evidence.Count > 0;

        return new ObjectiveGamesDto(
            GeneratedAt: generatedAt,
            ObjectiveId: objectiveId,
            ObjectiveTitle: title,
            ObjectiveStatus: status,
            CounterText: hasGames ? $"{practicedCount} practiced / {games.Count} total" : "",
            TotalCount: games.Count,
            PracticedCount: practicedCount,
            HasGames: hasGames,
            HasEvidence: hasEvidence,
            HasActivity: hasGames || hasEvidence,
            EvidenceSummary: BuildEvidenceSummary(evidence),
            Games: games,
            Evidence: evidence);
    }

    // ── Evidence summary (mirror EvidenceSummary) ─────────────────────────────
    private static string BuildEvidenceSummary(IReadOnlyList<ObjectiveEvidenceRowDto> evidence)
    {
        if (evidence.Count == 0) return "No linked evidence yet.";
        var good = evidence.Count(e => e.Polarity == EvidencePolarities.Good);
        var bad = evidence.Count(e => e.Polarity == EvidencePolarities.Bad);
        var neutral = evidence.Count - good - bad;
        return $"{evidence.Count} evidence item(s)  /  {good} good  /  {bad} bad  /  {neutral} neutral";
    }

    // ── DisplayNote suppression: "" when blank OR == Title (case-insensitive) ──
    private static string BuildDisplayNote(string note, string title)
    {
        if (string.IsNullOrWhiteSpace(note)) return "";
        return string.Equals(note.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase) ? "" : note;
    }

    private static string PolarityLabel(string polarity) => polarity switch
    {
        EvidencePolarities.Good => "Good example",
        EvidencePolarities.Bad => "Bad example",
        _ => "Neutral",
    };

    private static string PolarityHex(string polarity) => polarity switch
    {
        EvidencePolarities.Good => GoodHex,
        EvidencePolarities.Bad => BadHex,
        _ => EvidenceNeutralHex,
    };

    private static string FormatDate(long unixSeconds) =>
        unixSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("MMM d, yyyy")
            : "";

    private static string FormatClock(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:D2}";
    }

    // Join non-blank segments with a separator (mirror MetaText composition).
    private static string JoinSkippingBlanks(string sep, params string[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
