#nullable enable

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Revu.Core.Data.Repositories;

namespace Revu.Sidecar;

/// <summary>
/// Builds the read-only ObjectiveNotes snapshot served at
/// GET /api/objective/notes?id=N.
///
/// <para>
/// Reproduces <c>ObjectiveNotesViewModel.LoadAsync</c>: aggregates per-game
/// review notes + per-game execution notes (both derived from
/// <see cref="IObjectivesRepository.GetGamesForObjectiveAsync"/>, no separate
/// Core call) and clips/bookmarks
/// (<see cref="IVodRepository.GetBookmarksForObjectiveAsync"/>) for ONE objective.
/// References only Core repo interfaces — never the WinUI ViewModel.
/// </para>
///
/// <para>
/// READ-ONLY ABSOLUTE: only repo read methods are called. No writes/migrations.
/// Each row's "Open"/"Play" jump is plain frontend navigation.
/// </para>
///
/// <para>
/// Like the other builders, the load is wrapped in try/catch that degrades to
/// empty so one failing query never blanks the whole page.
/// </para>
/// </summary>
public sealed class ObjectiveNotesSnapshotBuilder
{
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IVodRepository _vodRepo;
    private readonly IPromptsRepository _promptsRepo;
    private readonly ILogger<ObjectiveNotesSnapshotBuilder> _logger;

    public ObjectiveNotesSnapshotBuilder(
        IObjectivesRepository objectivesRepo,
        IVodRepository vodRepo,
        IPromptsRepository promptsRepo,
        ILogger<ObjectiveNotesSnapshotBuilder> logger)
    {
        _objectivesRepo = objectivesRepo;
        _vodRepo = vodRepo;
        _promptsRepo = promptsRepo;
        _logger = logger;
    }

    public async Task<ObjectiveNotesDto> BuildAsync(long objectiveId, CancellationToken ct = default)
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
                var statusText = string.Equals(obj.Status, "active", StringComparison.OrdinalIgnoreCase)
                    ? "Active"
                    : "Completed";
                status = $"{statusText} • {ObjectivePhases.ToDisplayLabel(obj.Phase)}";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ObjectiveNotes: header load failed for {ObjectiveId}", objectiveId);
        }

        // ── Games → review notes + execution notes + game-label lookup ──────
        var reviewNotes = new List<ObjectiveReviewNoteRowDto>();
        var executionNotes = new List<ObjectiveExecutionNoteRowDto>();
        var gameLabels = new Dictionary<long, string>();
        try
        {
            var entries = await _objectivesRepo.GetGamesForObjectiveAsync(objectiveId);
            foreach (var g in entries)
            {
                var date = FormatDate(g.Timestamp);
                var result = g.Win ? "W" : "L";
                var header = $"{result} • {g.ChampionName} • {date}";
                gameLabels[g.GameId] = header;

                if (!string.IsNullOrWhiteSpace(g.ReviewNotes))
                {
                    reviewNotes.Add(new ObjectiveReviewNoteRowDto(
                        GameId: g.GameId,
                        Header: header,
                        Notes: g.ReviewNotes.Trim()));
                }

                if (!string.IsNullOrWhiteSpace(g.ExecutionNote))
                {
                    executionNotes.Add(new ObjectiveExecutionNoteRowDto(
                        GameId: g.GameId,
                        Header: header,
                        Note: g.ExecutionNote.Trim()));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ObjectiveNotes: games load failed for {ObjectiveId}", objectiveId);
        }

        // ── Clips & bookmarks ───────────────────────────────────────────────
        var bookmarks = new List<ObjectiveBookmarkRowDto>();
        try
        {
            var raw = await _vodRepo.GetBookmarksForObjectiveAsync(objectiveId);
            foreach (var b in raw)
            {
                var tags = JoinTags(b.TagsJson);
                bookmarks.Add(new ObjectiveBookmarkRowDto(
                    BookmarkId: b.Id,
                    GameId: b.GameId,
                    GameTimeSeconds: b.GameTimeSeconds,
                    TimeLabel: FormatClockTime(b.GameTimeSeconds),
                    GameLabel: gameLabels.TryGetValue(b.GameId, out var label) ? label : $"Game #{b.GameId}",
                    Note: b.Note ?? "",
                    HasNote: !string.IsNullOrWhiteSpace(b.Note),
                    Tags: tags,
                    HasTags: !string.IsNullOrEmpty(tags),
                    HasClip: !string.IsNullOrEmpty(b.ClipPath)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ObjectiveNotes: bookmarks load failed for {ObjectiveId}", objectiveId);
        }

        // ── Custom-prompt answers (grouped by prompt) ───────────────────────
        // Every answer the user typed under a custom prompt for this objective,
        // across all games. Grouped under each prompt's label/phase; rows keep
        // the repo's order (prompt sort_order, then game timestamp DESC).
        var promptAnswers = new List<ObjectivePromptGroupDto>();
        try
        {
            var rawAnswers = await _promptsRepo.GetAnswersForObjectiveAsync(objectiveId);
            var groups = new Dictionary<long, (string Label, string Phase, List<ObjectivePromptAnswerRowDto> Rows)>();
            var order = new List<long>();
            foreach (var a in rawAnswers)
            {
                if (string.IsNullOrWhiteSpace(a.AnswerText)) continue;

                if (!groups.TryGetValue(a.PromptId, out var group))
                {
                    group = (a.Label, ObjectivePhases.ToDisplayLabel(a.Phase), new List<ObjectivePromptAnswerRowDto>());
                    groups[a.PromptId] = group;
                    order.Add(a.PromptId);
                }

                var date = FormatDate(a.Timestamp);
                var result = a.Win ? "W" : "L";
                var header = $"{result} • {a.ChampionName} • {date}";
                group.Rows.Add(new ObjectivePromptAnswerRowDto(
                    GameId: a.GameId,
                    Header: header,
                    Answer: a.AnswerText.Trim()));
            }

            foreach (var promptId in order)
            {
                var g = groups[promptId];
                if (g.Rows.Count == 0) continue;
                promptAnswers.Add(new ObjectivePromptGroupDto(
                    PromptId: promptId,
                    Label: g.Label,
                    Phase: g.Phase,
                    Answers: g.Rows));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ObjectiveNotes: prompt answers load failed for {ObjectiveId}", objectiveId);
        }

        var hasReviewNotes = reviewNotes.Count > 0;
        var hasExecutionNotes = executionNotes.Count > 0;
        var hasBookmarks = bookmarks.Count > 0;
        var hasPromptAnswers = promptAnswers.Count > 0;

        return new ObjectiveNotesDto(
            GeneratedAt: generatedAt,
            ObjectiveId: objectiveId,
            ObjectiveTitle: title,
            ObjectiveStatus: status,
            HasReviewNotes: hasReviewNotes,
            HasExecutionNotes: hasExecutionNotes,
            HasBookmarks: hasBookmarks,
            HasPromptAnswers: hasPromptAnswers,
            HasAnything: hasReviewNotes || hasExecutionNotes || hasBookmarks || hasPromptAnswers,
            ReviewNotes: reviewNotes,
            ExecutionNotes: executionNotes,
            Bookmarks: bookmarks,
            PromptAnswers: promptAnswers);
    }

    private static string FormatDate(long unixSeconds) =>
        unixSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime.ToString("MMM d, yyyy")
            : "";

    // m=seconds/60, s=seconds%60 → "m:ss". Minutes unpadded, seconds 2-digit,
    // no hour rollover (mirror FormatClockTime).
    private static string FormatClockTime(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:D2}";
    }

    // Deserialize tagsJson as List<string>; "" on null/whitespace/empty/parse
    // failure (mirror JoinTags' swallowing try/catch).
    private static string JoinTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return "";
        try
        {
            var tags = JsonSerializer.Deserialize<List<string>>(tagsJson);
            if (tags is null || tags.Count == 0) return "";
            return string.Join(", ", tags);
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
