#nullable enable

using System.Globalization;
using System.Text;
using Revu.Core.Data.Repositories;
using GameStats = Revu.Core.Models.GameStats;

namespace Revu.Core.Services;

public sealed class ReviewExportService : IReviewExportService
{
    private const int ExportLimit = 10_000;

    private readonly IGameHistoryQuery _gameHistory;
    private readonly IObjectivesRepository _objectives;
    private readonly IConceptTagRepository _conceptTags;
    private readonly IPromptsRepository _prompts;
    private readonly IVodRepository _vod;
    private readonly IMatchupNotesRepository _matchupNotes;

    public ReviewExportService(
        IGameHistoryQuery gameHistory,
        IObjectivesRepository objectives,
        IConceptTagRepository conceptTags,
        IPromptsRepository prompts,
        IVodRepository vod,
        IMatchupNotesRepository matchupNotes)
    {
        _gameHistory = gameHistory;
        _objectives = objectives;
        _conceptTags = conceptTags;
        _prompts = prompts;
        _vod = vod;
        _matchupNotes = matchupNotes;
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
        sb.AppendLine("# Revu Game Review Export");
        sb.AppendLine();
        AppendField(sb, "Exported", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        sb.AppendLine();

        await AppendGameAsync(sb, game, headingLevel: 2);
        return sb.ToString();
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

        AppendObjectives(sb, objectives);
        AppendPromptAnswers(sb, promptAnswers);
        AppendVod(sb, vod, bookmarks, objectives);
    }

    private static void AppendObjectives(StringBuilder sb, IReadOnlyList<GameObjectiveRecord> objectives)
    {
        sb.AppendLine("### Objectives");
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
            AppendNestedField(sb, "Type", objective.Type);
            AppendNestedField(sb, "Phase", objective.Phase);
            AppendNestedField(sb, "Completion criteria", objective.CompletionCriteria);
            AppendNestedField(sb, "Execution note", objective.ExecutionNote);
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
}
