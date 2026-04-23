#nullable enable

using System.Text.Json;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

public sealed class ReviewWorkflowService : IReviewWorkflowService
{
    private readonly IGameRepository _gameRepository;
    private readonly IConceptTagRepository _conceptTagRepository;
    private readonly IVodRepository _vodRepository;
    private readonly IVodService _vodService;
    private readonly ISessionLogRepository _sessionLogRepository;
    private readonly IObjectivesRepository _objectivesRepository;
    private readonly IReviewDraftRepository _reviewDraftRepository;
    private readonly IMatchupNotesRepository _matchupNotesRepository;
    private readonly IConfigService _configService;
    private readonly ICoachSidecarNotifier _coachNotifier;
    private readonly ILogger<ReviewWorkflowService> _logger;

    public ReviewWorkflowService(
        IGameRepository gameRepository,
        IConceptTagRepository conceptTagRepository,
        IVodRepository vodRepository,
        IVodService vodService,
        ISessionLogRepository sessionLogRepository,
        IObjectivesRepository objectivesRepository,
        IReviewDraftRepository reviewDraftRepository,
        IMatchupNotesRepository matchupNotesRepository,
        IConfigService configService,
        ICoachSidecarNotifier coachNotifier,
        ILogger<ReviewWorkflowService> logger)
    {
        _gameRepository = gameRepository;
        _conceptTagRepository = conceptTagRepository;
        _vodRepository = vodRepository;
        _vodService = vodService;
        _sessionLogRepository = sessionLogRepository;
        _objectivesRepository = objectivesRepository;
        _reviewDraftRepository = reviewDraftRepository;
        _matchupNotesRepository = matchupNotesRepository;
        _configService = configService;
        _coachNotifier = coachNotifier;
        _logger = logger;
    }

    public async Task<ReviewScreenData?> LoadAsync(long gameId, CancellationToken cancellationToken = default)
    {
        var game = await _gameRepository.GetAsync(gameId);
        if (game is null)
        {
            return null;
        }

        var config = await _configService.LoadAsync();
        var sessionEntry = await _sessionLogRepository.GetEntryAsync(gameId);
        var tags = await _conceptTagRepository.GetAllAsync();
        var selectedTagIds = await _conceptTagRepository.GetIdsForGameAsync(gameId);
        var allActiveObjectives = await _objectivesRepository.GetActiveAsync();
        var activeObjectives = allActiveObjectives
            .Where(objective => ObjectivePhases.ShowsInPostGame(objective.Phase))
            .ToList();
        var priorityObjective = allActiveObjectives
            .Where(objective => ObjectivePhases.ShowsInPostGame(objective.Phase))
            .FirstOrDefault(objective => objective.IsPriority)
            ?? activeObjectives.FirstOrDefault();
        var savedObjectives = await _objectivesRepository.GetGameObjectivesAsync(gameId);
        var savedNoteForGame = await _matchupNotesRepository.GetForGameAsync(gameId);

        var vod = await _vodRepository.GetVodAsync(gameId);
        if (vod is null && _configService.IsAscentEnabled)
        {
            try
            {
                await _vodService.TryLinkRecordingAsync(game);
                vod = await _vodRepository.GetVodAsync(gameId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "VOD lookup retry failed for game {GameId}", gameId);
            }
        }

        var snapshot = BuildInitialSnapshot(game, sessionEntry, savedNoteForGame, selectedTagIds, savedObjectives);
        var draft = await _reviewDraftRepository.GetAsync(gameId);
        if (draft is not null)
        {
            snapshot = ApplyDraft(snapshot, draft);
        }

        var tagStates = tags
            .Select(tag => new ReviewTagState(
                Id: tag.Id,
                Name: tag.Name,
                Polarity: tag.Polarity,
                ColorHex: tag.Color,
                IsSelected: snapshot.SelectedTagIds.Contains(tag.Id)))
            .ToList();

        var objectiveStates = BuildObjectiveStates(activeObjectives, priorityObjective, savedObjectives, snapshot.ObjectivePractices);
        var matchupHistory = await GetMatchupHistoryAsync(game.ChampionName, snapshot.EnemyLaner, gameId, cancellationToken);
        var bookmarkCount = vod is null ? 0 : await _vodRepository.GetBookmarkCountAsync(gameId);

        return new ReviewScreenData(
            Game: game,
            RequireReviewNotes: config.RequireReviewNotes,
            HasVod: vod is not null,
            BookmarkCount: bookmarkCount,
            PriorityObjective: priorityObjective,
            Tags: tagStates,
            ObjectiveAssessments: objectiveStates,
            MatchupHistory: matchupHistory,
            Snapshot: snapshot);
    }

    public async Task<VodCheckResult> CheckVodAsync(long gameId, CancellationToken cancellationToken = default)
    {
        var game = await _gameRepository.GetAsync(gameId);
        if (game is null)
        {
            return new VodCheckResult(false, 0);
        }

        var vod = await _vodRepository.GetVodAsync(gameId);
        if (vod is null && _configService.IsAscentEnabled)
        {
            try
            {
                await _vodService.TryLinkRecordingAsync(game);
                vod = await _vodRepository.GetVodAsync(gameId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "VOD re-check failed for game {GameId}", gameId);
            }
        }

        var bookmarkCount = vod is null ? 0 : await _vodRepository.GetBookmarkCountAsync(gameId);
        return new VodCheckResult(vod is not null, bookmarkCount);
    }

    public async Task<ReviewSaveResult> SaveAsync(SaveReviewRequest request, CancellationToken cancellationToken = default)
    {
        if (request.GameId <= 0)
        {
            return ReviewSaveResult.Fail("Game id is required before saving.");
        }

        if (!ValidateRequest(request, out var validationError))
        {
            return ReviewSaveResult.Fail(validationError);
        }

        try
        {
            var trimmedEnemy = request.Snapshot.EnemyLaner.Trim();
            var trimmedMatchupNote = request.Snapshot.MatchupNote.Trim();

            var review = new GameReview
            {
                Rating = 1,
                Notes = request.Snapshot.ReviewNotes.Trim(),
                Mistakes = request.Snapshot.Mistakes.Trim(),
                WentWell = request.Snapshot.WentWell.Trim(),
                FocusNext = request.Snapshot.FocusNext.Trim(),
                SpottedProblems = request.Snapshot.SpottedProblems.Trim(),
                OutsideControl = request.Snapshot.OutsideControl.Trim(),
                WithinControl = request.Snapshot.WithinControl.Trim(),
                Attribution = request.Snapshot.Attribution.Trim(),
                PersonalContribution = request.Snapshot.PersonalContribution.Trim(),
            };

            await _gameRepository.UpdateReviewAsync(request.GameId, review);
            await _sessionLogRepository.LogGameAsync(
                request.GameId,
                request.ChampionName,
                request.Win,
                request.Snapshot.MentalRating,
                request.Snapshot.ImprovementNote.Trim());
            await _sessionLogRepository.UpdateMentalHandledAsync(request.GameId, request.Snapshot.MentalHandled.Trim());

            var existingGame = await _gameRepository.GetAsync(request.GameId);
            if (!string.Equals(existingGame?.EnemyLaner ?? "", trimmedEnemy, StringComparison.Ordinal))
            {
                await _gameRepository.UpdateEnemyLanerAsync(request.GameId, trimmedEnemy);
            }

            if (!string.IsNullOrWhiteSpace(trimmedEnemy) || !string.IsNullOrWhiteSpace(trimmedMatchupNote))
            {
                await _matchupNotesRepository.UpsertForGameAsync(request.GameId, request.ChampionName, trimmedEnemy, trimmedMatchupNote);
            }
            else
            {
                await _matchupNotesRepository.DeleteForGameAsync(request.GameId);
            }

            foreach (var objective in request.Snapshot.ObjectivePractices)
            {
                await _objectivesRepository.RecordGameAsync(
                    request.GameId,
                    objective.ObjectiveId,
                    objective.Practiced,
                    objective.ExecutionNote.Trim());
            }

            await _conceptTagRepository.SetForGameAsync(request.GameId, request.Snapshot.SelectedTagIds);
            await _reviewDraftRepository.DeleteAsync(request.GameId);

            // Fire-and-forget: ask the coach sidecar to extract concepts from
            // the freshly-saved review text. No-op if sidecar is off.
            _ = _coachNotifier.NotifyReviewSavedAsync(request.GameId, cancellationToken)
                .ContinueWith(
                    t => _logger.LogDebug(t.Exception, "Coach NotifyReviewSavedAsync failed (non-fatal)"),
                    TaskContinuationOptions.OnlyOnFaulted);

            return ReviewSaveResult.Ok(trimmedEnemy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save review for game {GameId}", request.GameId);
            return ReviewSaveResult.Fail("Failed to save review. Check the logs and try again.");
        }
    }

    public async Task<bool> SaveDraftAsync(ReviewDraftRequest request, CancellationToken cancellationToken = default)
    {
        if (request.GameId <= 0)
        {
            return false;
        }

        try
        {
            await _reviewDraftRepository.UpsertAsync(new ReviewDraft
            {
                GameId = request.GameId,
                MentalRating = request.Snapshot.MentalRating,
                WentWell = request.Snapshot.WentWell.Trim(),
                Mistakes = request.Snapshot.Mistakes.Trim(),
                FocusNext = request.Snapshot.FocusNext.Trim(),
                ReviewNotes = request.Snapshot.ReviewNotes.Trim(),
                ImprovementNote = request.Snapshot.ImprovementNote.Trim(),
                Attribution = request.Snapshot.Attribution.Trim(),
                MentalHandled = request.Snapshot.MentalHandled.Trim(),
                SpottedProblems = request.Snapshot.SpottedProblems.Trim(),
                OutsideControl = request.Snapshot.OutsideControl.Trim(),
                WithinControl = request.Snapshot.WithinControl.Trim(),
                PersonalContribution = request.Snapshot.PersonalContribution.Trim(),
                EnemyLaner = request.Snapshot.EnemyLaner.Trim(),
                MatchupNote = request.Snapshot.MatchupNote.Trim(),
                SelectedTagIdsJson = JsonSerializer.Serialize(request.Snapshot.SelectedTagIds),
                ObjectiveAssessmentsJson = JsonSerializer.Serialize(request.Snapshot.ObjectivePractices),
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save review draft for game {GameId}", request.GameId);
            return false;
        }
    }

    public async Task<IReadOnlyList<ReviewMatchupHistoryItem>> GetMatchupHistoryAsync(
        string championName,
        string enemyLaner,
        long currentGameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(championName) || string.IsNullOrWhiteSpace(enemyLaner))
        {
            return [];
        }

        var notes = await _matchupNotesRepository.GetForMatchupAsync(championName, enemyLaner.Trim());
        return notes
            .Where(note => note.GameId != currentGameId && !string.IsNullOrWhiteSpace(note.Note))
            .Select(note => new ReviewMatchupHistoryItem(
                Note: note.Note,
                Helpful: ParseHelpful(note.Helpful),
                GameId: note.GameId,
                CreatedAt: note.CreatedAt))
            .ToList();
    }

    private static ReviewSnapshot BuildInitialSnapshot(
        GameStats game,
        SessionLogEntry? sessionEntry,
        MatchupNoteRecord? savedNoteForGame,
        IReadOnlyList<long> selectedTagIds,
        IReadOnlyList<GameObjectiveRecord> savedObjectives)
    {
        var objectivePractices = savedObjectives
            .Select(record => new SaveObjectivePracticeRequest(
                ObjectiveId: record.ObjectiveId,
                Practiced: record.Practiced,
                ExecutionNote: record.ExecutionNote))
            .ToList();

        var enemyLaner = string.IsNullOrWhiteSpace(game.EnemyLaner)
            ? savedNoteForGame?.Enemy ?? ""
            : game.EnemyLaner;

        return new ReviewSnapshot(
            MentalRating: sessionEntry?.MentalRating ?? 5,
            WentWell: game.WentWell,
            Mistakes: game.Mistakes,
            FocusNext: game.FocusNext,
            ReviewNotes: game.ReviewNotes,
            ImprovementNote: sessionEntry?.ImprovementNote ?? "",
            Attribution: game.Attribution,
            MentalHandled: sessionEntry?.MentalHandled ?? "",
            SpottedProblems: game.SpottedProblems,
            OutsideControl: game.OutsideControl,
            WithinControl: game.WithinControl,
            PersonalContribution: game.PersonalContribution,
            EnemyLaner: enemyLaner,
            MatchupNote: savedNoteForGame?.Note ?? "",
            SelectedTagIds: selectedTagIds.ToList(),
            ObjectivePractices: objectivePractices);
    }

    private static IReadOnlyList<ReviewObjectiveState> BuildObjectiveStates(
        IReadOnlyList<ObjectiveSummary> activeObjectives,
        ObjectiveSummary? priorityObjective,
        IReadOnlyList<GameObjectiveRecord> savedObjectives,
        IReadOnlyList<SaveObjectivePracticeRequest> selectedPractices)
    {
        var selectedById = selectedPractices.ToDictionary(static item => item.ObjectiveId);
        var savedById = savedObjectives.ToDictionary(static item => item.ObjectiveId);
        var priorityObjectiveId = priorityObjective?.Id ?? 0;
        var results = new List<ReviewObjectiveState>();

        foreach (var objective in activeObjectives)
        {
            savedById.TryGetValue(objective.Id, out var saved);
            selectedById.TryGetValue(objective.Id, out var selected);

            results.Add(new ReviewObjectiveState(
                ObjectiveId: objective.Id,
                Title: objective.Title,
                Criteria: objective.CompletionCriteria,
                Phase: objective.Phase,
                IsPriority: objective.Id == priorityObjectiveId,
                Practiced: selected?.Practiced ?? saved?.Practiced ?? false,
                ExecutionNote: selected?.ExecutionNote ?? saved?.ExecutionNote ?? ""));

            savedById.Remove(objective.Id);
        }

        foreach (var leftover in savedById.Values)
        {
            selectedById.TryGetValue(leftover.ObjectiveId, out var selected);
            results.Add(new ReviewObjectiveState(
                ObjectiveId: leftover.ObjectiveId,
                Title: leftover.Title,
                Criteria: leftover.CompletionCriteria,
                Phase: leftover.Phase,
                IsPriority: leftover.ObjectiveId == priorityObjectiveId,
                Practiced: selected?.Practiced ?? leftover.Practiced,
                ExecutionNote: selected?.ExecutionNote ?? leftover.ExecutionNote));
        }

        return results;
    }

    private static ReviewSnapshot ApplyDraft(ReviewSnapshot snapshot, ReviewDraft draft)
    {
        return snapshot with
        {
            MentalRating = draft.MentalRating,
            WentWell = draft.WentWell,
            Mistakes = draft.Mistakes,
            FocusNext = draft.FocusNext,
            ReviewNotes = draft.ReviewNotes,
            ImprovementNote = draft.ImprovementNote,
            Attribution = draft.Attribution,
            MentalHandled = draft.MentalHandled,
            SpottedProblems = draft.SpottedProblems,
            OutsideControl = draft.OutsideControl,
            WithinControl = draft.WithinControl,
            PersonalContribution = draft.PersonalContribution,
            EnemyLaner = draft.EnemyLaner,
            MatchupNote = draft.MatchupNote,
            SelectedTagIds = DeserializeSelectedTagIds(draft.SelectedTagIdsJson),
            ObjectivePractices = DeserializeObjectivePractices(draft.ObjectiveAssessmentsJson)
        };
    }

    private static IReadOnlyList<long> DeserializeSelectedTagIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<long>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<SaveObjectivePracticeRequest> DeserializeObjectivePractices(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SaveObjectivePracticeRequest>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool ValidateRequest(SaveReviewRequest request, out string validationError)
    {
        if (!string.IsNullOrWhiteSpace(request.Snapshot.MatchupNote)
            && string.IsNullOrWhiteSpace(request.Snapshot.EnemyLaner))
        {
            validationError = "Add the enemy champion before saving a matchup note.";
            return false;
        }

        if (request.RequireReviewNotes && !HasMeaningfulReviewContent(request.Snapshot))
        {
            validationError = "Review notes are required in Settings. Add review content before saving.";
            return false;
        }

        validationError = "";
        return true;
    }

    private static bool HasMeaningfulReviewContent(ReviewSnapshot snapshot)
    {
        return !string.IsNullOrWhiteSpace(snapshot.WentWell)
               || !string.IsNullOrWhiteSpace(snapshot.Mistakes)
               || !string.IsNullOrWhiteSpace(snapshot.FocusNext)
               || !string.IsNullOrWhiteSpace(snapshot.ReviewNotes)
               || !string.IsNullOrWhiteSpace(snapshot.ImprovementNote)
               || !string.IsNullOrWhiteSpace(snapshot.MentalHandled)
               || !string.IsNullOrWhiteSpace(snapshot.SpottedProblems)
               || !string.IsNullOrWhiteSpace(snapshot.OutsideControl)
               || !string.IsNullOrWhiteSpace(snapshot.WithinControl)
               || !string.IsNullOrWhiteSpace(snapshot.PersonalContribution)
               || !string.IsNullOrWhiteSpace(snapshot.Attribution)
               || !string.IsNullOrWhiteSpace(snapshot.MatchupNote)
               || snapshot.SelectedTagIds.Count > 0
               || snapshot.ObjectivePractices.Any(practice => practice.Practiced || !string.IsNullOrWhiteSpace(practice.ExecutionNote));
    }

    private static bool? ParseHelpful(int? value)
    {
        return value switch
        {
            1 => true,
            0 => false,
            _ => null,
        };
    }
}
