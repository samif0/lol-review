#nullable enable

using System.Globalization;
using System.Text;
using Revu.Core.Data.Repositories;
using GameStats = Revu.Core.Models.GameStats;

namespace Revu.Core.Services;

public sealed class ReviewExportService : IReviewExportService
{
    private const int ExportLimit = 10_000;
    private const int SingleGameExportCharacterLimit = 2_000;

    private readonly IGameHistoryQuery _gameHistory;
    private readonly IObjectivesRepository _objectives;
    private readonly IConceptTagRepository _conceptTags;
    private readonly IPromptsRepository _prompts;
    private readonly IVodRepository _vod;
    private readonly IMatchupNotesRepository _matchupNotes;
    private readonly IEvidenceRepository _evidence;

    public ReviewExportService(
        IGameHistoryQuery gameHistory,
        IObjectivesRepository objectives,
        IConceptTagRepository conceptTags,
        IPromptsRepository prompts,
        IVodRepository vod,
        IMatchupNotesRepository matchupNotes,
        IEvidenceRepository evidence)
    {
        _gameHistory = gameHistory;
        _objectives = objectives;
        _conceptTags = conceptTags;
        _prompts = prompts;
        _vod = vod;
        _matchupNotes = matchupNotes;
        _evidence = evidence;
    }

    public async Task<string> ExportAllAsync(CancellationToken cancellationToken = default)
    {
        var games = await _gameHistory.GetRecentAsync(ExportLimit);
        var sb = new StringBuilder();

        sb.AppendLine("# Revu Review Export");
        sb.AppendLine();
        AppendField(sb, "Exported", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        AppendField(sb, "Games exported", games.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AppendGameAsync(sb, game, headingLevel: 2);
        }

        return sb.ToString();
    }

    public async Task<string?> ExportGameAsync(long gameId, CancellationToken cancellationToken = default)
    {
        var game = await _gameHistory.GetAsync(gameId);
        if (game is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var sb = new StringBuilder();
        await AppendSingleGameReviewAsync(sb, game);
        return TrimToCharacterLimit(sb.ToString(), SingleGameExportCharacterLimit);
    }

    private async Task AppendSingleGameReviewAsync(StringBuilder sb, GameStats game)
    {
        var objectives = await _objectives.GetGameObjectivesAsync(game.GameId);
        var promptAnswers = await _prompts.GetAnswersForGameAsync(game.GameId);
        var matchupNote = await _matchupNotes.GetForGameAsync(game.GameId);
        var bookmarks = await _vod.GetBookmarksAsync(game.GameId);
        var evidence = await _evidence.GetForGameAsync(game.GameId);
        var tagIds = await _conceptTags.GetIdsForGameAsync(game.GameId);
        var allTags = await _conceptTags.GetAllAsync();
        var tagNames = allTags
            .Where(tag => tagIds.Contains(tag.Id))
            .Select(tag => tag.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = game.Win ? "Win" : "Loss";
        sb.AppendLine($"# {OneLine(game.ChampionName)} vs {OneLine(game.EnemyLaner)} ({result})");
        sb.AppendLine();

        AppendCompactNotes(sb, game, matchupNote?.Note ?? "", tagNames);
        AppendCompactObjectives(sb, objectives);
        AppendCompactPromptAnswers(sb, promptAnswers);
        AppendMoments(sb, bookmarks, evidence, objectives);
    }

    private async Task AppendGameAsync(StringBuilder sb, GameStats game, int headingLevel)
    {
        var objectives = await _objectives.GetGameObjectivesAsync(game.GameId);
        var promptAnswers = await _prompts.GetAnswersForGameAsync(game.GameId);
        var matchupNote = await _matchupNotes.GetForGameAsync(game.GameId);
        var vod = await _vod.GetVodAsync(game.GameId);
        var bookmarks = await _vod.GetBookmarksAsync(game.GameId);
        var tagIds = await _conceptTags.GetIdsForGameAsync(game.GameId);
        var allTags = await _conceptTags.GetAllAsync();
        var tagNames = allTags
            .Where(tag => tagIds.Contains(tag.Id))
            .Select(tag => tag.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var result = game.Win ? "Win" : "Loss";
        var heading = new string('#', Math.Clamp(headingLevel, 1, 6));
        sb.AppendLine($"{heading} {FormatDate(game.Timestamp)} - {OneLine(game.ChampionName)} - {result}");
        sb.AppendLine();

        sb.AppendLine("### Game");
        AppendField(sb, "Game ID", game.GameId.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, "Champion", game.ChampionName);
        AppendField(sb, "Enemy laner", game.EnemyLaner);
        AppendField(sb, "Position", game.Position);
        AppendField(sb, "Queue", game.DisplayGameMode);
        AppendField(sb, "Result", result);
        AppendField(sb, "KDA", $"{game.Kills}/{game.Deaths}/{game.Assists} ({game.KdaRatio:0.##})");
        AppendField(sb, "CS", $"{game.CsTotal} ({game.CsPerMin:0.##}/min)");
        AppendField(sb, "Vision", game.VisionScore.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, "Damage to champions", game.TotalDamageToChampions.ToString("N0", CultureInfo.InvariantCulture));
        AppendField(sb, "Duration", game.DurationFormatted);
        sb.AppendLine();

        sb.AppendLine("### Review");
        AppendField(sb, "Mental rating", game.Rating > 0 ? $"{game.Rating}/10" : "");
        AppendField(sb, "Attribution", game.Attribution);
        AppendField(sb, "Review notes", game.ReviewNotes);
        AppendField(sb, "Went well", game.WentWell);
        AppendField(sb, "Mistakes", game.Mistakes);
        AppendField(sb, "Focus next", game.FocusNext);
        AppendField(sb, "Spotted problems", game.SpottedProblems);
        AppendField(sb, "Outside control", game.OutsideControl);
        AppendField(sb, "Within control", game.WithinControl);
        AppendField(sb, "Personal contribution", game.PersonalContribution);
        AppendField(sb, "Matchup note", matchupNote?.Note ?? "");
        AppendField(sb, "Concept tags", tagNames.Length > 0 ? string.Join(", ", tagNames) : "");
        sb.AppendLine();

        AppendObjectives(sb, objectives, includeMetadata: true);
        AppendPromptAnswers(sb, promptAnswers);
        AppendVod(sb, vod, bookmarks, objectives);
    }

    private static void AppendReviewNotes(
        StringBuilder sb,
        GameStats game,
        string matchupNote,
        IReadOnlyList<string> tagNames)
    {
        sb.AppendLine("## Review");
        AppendField(sb, "Mental rating", game.Rating > 0 ? $"{game.Rating}/10" : "");
        AppendField(sb, "Attribution", game.Attribution);
        AppendField(sb, "Review notes", game.ReviewNotes);
        AppendField(sb, "Went well", game.WentWell);
        AppendField(sb, "Mistakes", game.Mistakes);
        AppendField(sb, "Focus next", game.FocusNext);
        AppendField(sb, "Spotted problems", game.SpottedProblems);
        AppendField(sb, "Outside control", game.OutsideControl);
        AppendField(sb, "Within control", game.WithinControl);
        AppendField(sb, "Personal contribution", game.PersonalContribution);
        AppendField(sb, "Matchup note", matchupNote);
        AppendField(sb, "Concept tags", tagNames.Count > 0 ? string.Join(", ", tagNames) : "");
        sb.AppendLine();
    }

    private static void AppendCompactNotes(
        StringBuilder sb,
        GameStats game,
        string matchupNote,
        IReadOnlyList<string> tagNames)
    {
        sb.AppendLine("## Notes");
        AppendCompactField(sb, "Review", game.ReviewNotes);
        AppendCompactField(sb, "Mistakes", game.Mistakes);
        AppendCompactField(sb, "Went well", game.WentWell);
        AppendCompactField(sb, "Next", game.FocusNext);
        AppendCompactField(sb, "Problems", game.SpottedProblems);
        AppendCompactField(sb, "Matchup", matchupNote);
        AppendCompactField(sb, "Control", game.WithinControl);
        AppendCompactField(sb, "Outside", game.OutsideControl);
        AppendCompactField(sb, "Contribution", game.PersonalContribution);
        AppendCompactField(sb, "Tags", tagNames.Count > 0 ? string.Join(", ", tagNames) : "");
        sb.AppendLine();
    }

    private static void AppendCompactObjectives(StringBuilder sb, IReadOnlyList<GameObjectiveRecord> objectives)
    {
        if (objectives.Count == 0)
        {
            return;
        }

        sb.AppendLine("## Objectives");
        foreach (var objective in objectives)
        {
            var practiced = objective.Practiced ? "yes" : "no";
            var note = string.IsNullOrWhiteSpace(objective.ExecutionNote)
                ? ""
                : $": {OneLine(objective.ExecutionNote)}";
            sb.AppendLine($"- {OneLine(objective.Title)} ({practiced}){note}");
        }

        sb.AppendLine();
    }

    private static void AppendObjectives(StringBuilder sb, IReadOnlyList<GameObjectiveRecord> objectives, bool includeMetadata)
    {
        sb.AppendLine(includeMetadata ? "### Objectives" : "## Objectives");
        if (objectives.Count == 0)
        {
            sb.AppendLine("_No linked objectives._");
            sb.AppendLine();
            return;
        }

        foreach (var objective in objectives)
        {
            sb.AppendLine($"- **{OneLine(objective.Title)}**");
            sb.AppendLine($"  - Practiced: {(objective.Practiced ? "yes" : "no")}");
            if (includeMetadata)
            {
                AppendNestedField(sb, "Type", objective.Type);
                AppendNestedField(sb, "Phase", objective.Phase);
                AppendNestedField(sb, "Completion criteria", objective.CompletionCriteria);
            }
            AppendNestedField(sb, "Execution note", objective.ExecutionNote);
        }

        sb.AppendLine();
    }

    private static void AppendMoments(
        StringBuilder sb,
        IReadOnlyList<VodBookmarkRecord> bookmarks,
        IReadOnlyList<EvidenceItemRecord> evidence,
        IReadOnlyList<GameObjectiveRecord> objectives)
    {
        var moments = new List<ExportMoment>();
        var objectiveNames = objectives.ToDictionary(o => o.ObjectiveId, o => o.Title);
        // Public share links live on the bookmark row (set when a clip is shared to
        // revu.lol). Evidence-backed clips reference their bookmark via SourceId, so a
        // bookmarkId → ShareUrl lookup lets BOTH moment sources carry the link.
        var shareUrlByBookmarkId = bookmarks
            .Where(b => !string.IsNullOrWhiteSpace(b.ShareUrl))
            .GroupBy(b => b.Id)
            .ToDictionary(g => g.Key, g => g.First().ShareUrl);

        foreach (var bookmark in bookmarks)
        {
            moments.Add(new ExportMoment(
                StartSeconds: bookmark.ClipStartSeconds ?? bookmark.GameTimeSeconds,
                EndSeconds: bookmark.ClipEndSeconds,
                Text: string.IsNullOrWhiteSpace(bookmark.Note) ? "Saved moment" : bookmark.Note.Trim(),
                Objective: bookmark.ObjectiveId is { } objectiveId && objectiveNames.TryGetValue(objectiveId, out var title) ? title : "",
                ShareUrl: bookmark.ShareUrl ?? "",
                BookmarkId: bookmark.Id,
                FromEvidence: false));
        }

        foreach (var item in evidence)
        {
            var note = string.Equals(item.Note?.Trim(), item.Title?.Trim(), StringComparison.OrdinalIgnoreCase)
                ? ""
                : item.Note ?? "";
            var shareUrl = item.SourceId is { } srcId && shareUrlByBookmarkId.TryGetValue(srcId, out var u) ? u : "";
            moments.Add(new ExportMoment(
                StartSeconds: item.StartTimeSeconds ?? 0,
                EndSeconds: item.EndTimeSeconds,
                Text: string.IsNullOrWhiteSpace(note) ? item.Title ?? "Timeline moment" : note.Trim(),
                Objective: item.ObjectiveTitle,
                ShareUrl: shareUrl,
                // A clip evidence row's SourceId IS the bookmark it was clipped from, so
                // it dedupes against that bookmark. Timeline-only evidence has no bookmark.
                BookmarkId: string.Equals(item.SourceKind, EvidenceKinds.Clip, StringComparison.OrdinalIgnoreCase) ? (item.SourceId ?? 0) : 0,
                FromEvidence: true));
        }

        // First collapse bookmark↔evidence duplicates by their shared clip identity
        // (BookmarkId). A clip is saved as BOTH a vod_bookmark (often untagged, note
        // "Clip") AND an evidence_item (real note + objective tag); without this they
        // appear twice — once under the objective, once under "Unassigned". When both
        // exist for one BookmarkId, the EVIDENCE row wins (real note + objective). Rows
        // with BookmarkId==0 (timeline-only evidence, plain bookmarks) are untouched here.
        var byBookmark = moments
            .Where(m => m.BookmarkId != 0)
            .GroupBy(m => m.BookmarkId)
            .Select(g => g.FirstOrDefault(m => m.FromEvidence) ?? g.First());
        var collapsed = moments.Where(m => m.BookmarkId == 0).Concat(byBookmark).ToList();

        var deduped = collapsed
            .GroupBy(static item => (
                item.StartSeconds,
                item.EndSeconds,
                Text: OneLine(item.Text).ToLowerInvariant(),
                Objective: OneLine(item.Objective).ToLowerInvariant()))
            // Prefer the duplicate that carries a share link (content-identical rows
            // dedupe to one moment; keep the one with the public URL).
            .Select(static group => group.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.ShareUrl)) ?? group.First())
            .OrderBy(static item => string.IsNullOrWhiteSpace(item.Objective) ? 1 : 0)
            .ThenBy(static item => item.Objective, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.StartSeconds)
            .ThenBy(static item => item.Text, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var grouped = deduped
            .GroupBy(static item => string.IsNullOrWhiteSpace(item.Objective) ? "Unassigned" : OneLine(item.Objective))
            .OrderBy(static group => group.Key == "Unassigned" ? 1 : 0)
            .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        sb.AppendLine("## Moments");
        if (grouped.Length == 0)
        {
            sb.AppendLine("_No clips, bookmarks, or timeline evidence._");
            sb.AppendLine();
            return;
        }

        foreach (var group in grouped)
        {
            sb.AppendLine($"### {group.Key}");
            foreach (var moment in group
            .OrderBy(static item => item.StartSeconds)
            .ThenBy(static item => item.Text, StringComparer.OrdinalIgnoreCase))
            {
                var range = moment.EndSeconds is int end && end > moment.StartSeconds
                    ? $"{FormatSeconds(moment.StartSeconds)} - {FormatSeconds(end)}"
                    : FormatSeconds(moment.StartSeconds);
                var link = string.IsNullOrWhiteSpace(moment.ShareUrl)
                    ? ""
                    : $" ([clip]({moment.ShareUrl.Trim()}))";
                sb.AppendLine($"- {range}: {OneLine(moment.Text)}{link}");
            }
        }

        sb.AppendLine();
    }

    private static void AppendPromptAnswers(StringBuilder sb, IReadOnlyList<PromptAnswer> answers)
    {
        sb.AppendLine("### Prompt Answers");
        var populated = answers.Where(answer => !string.IsNullOrWhiteSpace(answer.AnswerText)).ToArray();
        if (populated.Length == 0)
        {
            sb.AppendLine("_No prompt answers._");
            sb.AppendLine();
            return;
        }

        foreach (var answer in populated)
        {
            sb.AppendLine($"- **{OneLine(answer.ObjectiveTitle)}** [{answer.Phase}] {OneLine(answer.Label)}");
            sb.AppendLine($"  - {Multiline(answer.AnswerText).Replace("\n", "\n  - ")}");
        }

        sb.AppendLine();
    }

    private static void AppendCompactPromptAnswers(StringBuilder sb, IReadOnlyList<PromptAnswer> answers)
    {
        var populated = answers.Where(answer => !string.IsNullOrWhiteSpace(answer.AnswerText)).ToArray();
        if (populated.Length == 0)
        {
            return;
        }

        sb.AppendLine("## Prompts");
        foreach (var answer in populated)
        {
            sb.AppendLine($"- {OneLine(answer.Label)}: {OneLine(answer.AnswerText)}");
        }

        sb.AppendLine();
    }

    private static void AppendVod(
        StringBuilder sb,
        VodSummary? vod,
        IReadOnlyList<VodBookmarkRecord> bookmarks,
        IReadOnlyList<GameObjectiveRecord> objectives)
    {
        sb.AppendLine("### VOD");
        if (vod is null && bookmarks.Count == 0)
        {
            sb.AppendLine("_No VOD linked._");
            sb.AppendLine();
            return;
        }

        if (vod is not null)
        {
            AppendField(sb, "File", vod.FilePath);
            AppendField(sb, "Duration", FormatSeconds(vod.DurationSeconds));
        }

        if (bookmarks.Count == 0)
        {
            sb.AppendLine();
            return;
        }

        var objectiveNames = objectives.ToDictionary(o => o.ObjectiveId, o => o.Title);
        sb.AppendLine();
        sb.AppendLine("#### Bookmarks");
        foreach (var bookmark in bookmarks.OrderBy(bookmark => bookmark.GameTimeSeconds))
        {
            sb.AppendLine($"- {FormatSeconds(bookmark.GameTimeSeconds)}");
            AppendNestedField(sb, "Note", bookmark.Note);
            AppendNestedField(sb, "Quality", bookmark.Quality);
            AppendNestedField(sb, "Clip", bookmark.ClipPath);
            AppendNestedField(sb, "Share link", bookmark.ShareUrl);
            if (bookmark.ClipStartSeconds is not null || bookmark.ClipEndSeconds is not null)
            {
                AppendNestedField(
                    sb,
                    "Clip range",
                    $"{FormatSeconds(bookmark.ClipStartSeconds ?? 0)} - {FormatSeconds(bookmark.ClipEndSeconds ?? 0)}");
            }

            if (bookmark.ObjectiveId is { } objectiveId)
            {
                var objectiveLabel = objectiveNames.TryGetValue(objectiveId, out var title)
                    ? title
                    : $"Objective {objectiveId}";
                AppendNestedField(sb, "Objective tag", objectiveLabel);
            }

            if (bookmark.PromptId is { } promptId)
            {
                AppendNestedField(sb, "Prompt tag", $"Prompt {promptId}");
            }
        }

        sb.AppendLine();
    }

    private static void AppendField(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.AppendLine($"- **{label}:** {Multiline(value)}");
    }

    private static void AppendCompactField(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.AppendLine($"- **{label}:** {OneLine(value)}");
    }

    private static void AppendNestedField(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.AppendLine($"  - {label}: {Multiline(value)}");
    }

    private static string FormatDate(long timestamp)
    {
        return timestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "Unknown date";
    }

    private static string FormatSeconds(int seconds)
    {
        if (seconds <= 0)
        {
            return "0:00";
        }

        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private static string OneLine(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "Unknown"
            : value.ReplaceLineEndings(" ").Trim();
    }

    private static string Multiline(string value)
    {
        return value.Trim().ReplaceLineEndings("\n");
    }

    private static string TrimToCharacterLimit(string value, int limit)
    {
        if (value.Length <= limit)
        {
            return value;
        }

        const string suffix = "\n\n...";
        var maxBody = Math.Max(0, limit - suffix.Length);
        var lines = value.ReplaceLineEndings("\n").Split('\n');
        var sb = new StringBuilder(maxBody);

        foreach (var line in lines)
        {
            var nextLength = sb.Length == 0 ? line.Length : sb.Length + 1 + line.Length;
            if (nextLength > maxBody)
            {
                break;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            sb.Append(line);
        }

        if (sb.Length == 0)
        {
            return value[..maxBody] + suffix;
        }

        return sb.ToString().TrimEnd() + suffix;
    }

    private sealed record ExportMoment(
        int StartSeconds,
        int? EndSeconds,
        string Text,
        string Objective,
        string ShareUrl = "",
        // The underlying clip-bookmark id (a bookmark's own Id, or an evidence clip's
        // SourceId). Two rows that share this id are the SAME moment — used to dedupe a
        // bookmark against its evidence row. 0 = no bookmark identity (timeline-only
        // evidence), which dedupes by content instead.
        long BookmarkId = 0,
        // True for the evidence-row variant: when a bookmark + its evidence collide on
        // BookmarkId, the evidence wins (it carries the real note + the objective tag).
        bool FromEvidence = false);
}
