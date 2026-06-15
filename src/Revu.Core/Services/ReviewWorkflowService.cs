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
    private readonly IEvidenceRepository _evidenceRepository;
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
        IEvidenceRepository evidenceRepository,
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
        _evidenceRepository = evidenceRepository;
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

        // P-008: objectives with evidence attached on this game default their
        // PRACTICED toggle ON (a clip/bookmark tied to the objective is the
        // strongest practice signal). An explicit saved or drafted answer
        // always wins; the state carries a flag so the pre-check is explicable.
        var evidenceObjectiveIds = new HashSet<long>();
        try
        {
            foreach (var item in await _evidenceRepository.GetForGameAsync(gameId))
            {
                if (item.ObjectiveId is long objectiveId)
                {
                    evidenceObjectiveIds.Add(objectiveId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Evidence lookup for practiced-default failed for game {GameId}", gameId);
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

        var objectiveStates = BuildObjectiveStates(activeObjectives, priorityObjective, savedObjectives, snapshot.ObjectivePractices, game, evidenceObjectiveIds);
        var matchupHistory = await GetMatchupHistoryAsync(game.ChampionName, snapshot.EnemyLaner, gameId, cancellationToken);
        var bookmarkCount = vod is null ? 0 : await _vodRepository.GetBookmarkCountAsync(gameId);

        // v2.18 (schema v5): the intent declared at Start Block on the day this
        // game was played — what the one-tap adherence question asks against.
        var sessionIntention = "";
        try
        {
            var gameDate = !string.IsNullOrWhiteSpace(sessionEntry?.Date)
                ? sessionEntry!.Date
                : game.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(game.Timestamp).LocalDateTime.ToString("yyyy-MM-dd")
                    : "";
            if (!string.IsNullOrEmpty(gameDate))
            {
                var session = await _sessionLogRepository.GetSessionAsync(gameDate);
                sessionIntention = session?.Intention?.Trim() ?? "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session intention lookup failed for game {GameId}", gameId);
        }

        return new ReviewScreenData(
            Game: game,
            RequireReviewNotes: config.RequireReviewNotes,
            HasVod: vod is not null,
            BookmarkCount: bookmarkCount,
            PriorityObjective: priorityObjective,
            Tags: tagStates,
            ObjectiveAssessments: objectiveStates,
            MatchupHistory: matchupHistory,
            Snapshot: snapshot,
            SessionIntention: sessionIntention);
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
            // v2.18 (schema v5): persist the one-tap focus-adherence answer.
            // Runs after LogGameAsync so the session_log row is guaranteed.
            await _sessionLogRepository.UpdateFocusAdherenceAsync(request.GameId, request.Snapshot.FocusAdherence);
            // Clear any prior skip-marker; if the caller is doing a true
            // skip-save, they re-stamp is_skipped after this returns.
            await _sessionLogRepository.ClearSkippedAsync(request.GameId);

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

                // v2.18 (schema v5): re-evaluate structured criteria on review
                // save so post-game-checked objectives get a verdict too.
                // Only practiced objectives are judged — an unpracticed
                // criterion outcome would be noise.
                if (objective.Practiced && existingGame is not null)
                {
                    await EvaluateCriteriaAsync(request.GameId, objective.ObjectiveId, existingGame);
                }
            }

            await _conceptTagRepository.SetForGameAsync(request.GameId, request.Snapshot.SelectedTagIds);
            await _reviewDraftRepository.DeleteAsync(request.GameId);

            // Fire-and-forget: ask the coach sidecar to extract concepts from
            // the freshly-saved review text. No-op if sidecar is off.
            BackgroundTaskRunner.Run(
                () => _coachNotifier.NotifyReviewSavedAsync(request.GameId, cancellationToken),
                _logger,
                $"coach review-saved notify {request.GameId}",
                cancellationToken);

            return ReviewSaveResult.Ok(trimmedEnemy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save review for game {GameId}", request.GameId);
            return ReviewSaveResult.Fail("Failed to save review. Check the logs and try again.");
        }
    }

    /// <summary>
    /// Delete a saved review — see IReviewWorkflowService.DeleteAsync. Clears only
    /// the review payload that gates the queue, KEEPS objective progress + the
    /// session_log behavioral row (no streak corruption), and keeps the game row.
    /// Best-effort per repo (the save path is likewise non-transactional); a failure
    /// is logged and surfaced but earlier clears are not rolled back (the operation
    /// is monotonic — it only ever clears, never re-creates, so a partial run still
    /// leaves the game closer to unreviewed, never in a worse state).
    /// </summary>
    public async Task<ReviewSaveResult> DeleteAsync(long gameId, CancellationToken cancellationToken = default)
    {
        if (gameId <= 0)
        {
            return ReviewSaveResult.Fail("Game id is required to delete a review.");
        }

        try
        {
            // (a) games review columns → blank (Rating=0 so the COALESCE(rating,0)>0
            // queue gate is false; all text fields ''). No dedicated clear method —
            // a blank GameReview through the existing update is sufficient.
            await _gameRepository.UpdateReviewAsync(gameId, new GameReview());

            // (b) session_log → clear only the queue-gating markers; KEEP mental_rating
            // / focus_adherence / rule_broken so streaks stay byte-identical.
            await _sessionLogRepository.ClearReviewMarkersAsync(gameId);

            // (c) concept tags → clear all for the game (empty set replaces).
            await _conceptTagRepository.SetForGameAsync(gameId, System.Array.Empty<long>());

            // (d) matchup note + (e) any leftover draft → remove (not queue-gating,
            // but part of the review payload).
            await _matchupNotesRepository.DeleteForGameAsync(gameId);
            await _reviewDraftRepository.DeleteAsync(gameId);

            // NOTE: game_objectives + objectives.score/game_count are intentionally
            // LEFT INTACT — deleting a review preserves earned objective progress.

            _logger.LogInformation("Review deleted (un-reviewed) for game {GameId}", gameId);
            return ReviewSaveResult.Ok("");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete review for game {GameId}", gameId);
            return ReviewSaveResult.Fail("Failed to delete review. Check the logs and try again.");
        }
    }

    /// <summary>
    /// v2.18 (schema v5): evaluate an objective's structured criterion against
    /// the game's stats and stamp game_objectives.criteria_met. Silent no-op
    /// when the objective has no structured criterion or the stat is missing.
    /// </summary>
    private async Task EvaluateCriteriaAsync(long gameId, long objectiveId, GameStats stats)
    {
        try
        {
            var objective = await _objectivesRepository.GetAsync(objectiveId);
            if (objective is null || !objective.HasStructuredCriteria)
            {
                return;
            }

            var met = ObjectiveCriteria.Evaluate(
                objective.CriteriaMetric, objective.CriteriaOp, objective.CriteriaValue, stats);
            if (met is null)
            {
                return;
            }

            await _objectivesRepository.SetCriteriaMetAsync(gameId, objectiveId, met.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Criteria evaluation failed for game {GameId} objective {ObjectiveId}", gameId, objectiveId);
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
            ObjectivePractices: objectivePractices,
            FocusAdherence: sessionEntry?.FocusAdherence);
    }

    private static IReadOnlyList<ReviewObjectiveState> BuildObjectiveStates(
        IReadOnlyList<ObjectiveSummary> activeObjectives,
        ObjectiveSummary? priorityObjective,
        IReadOnlyList<GameObjectiveRecord> savedObjectives,
        IReadOnlyList<SaveObjectivePracticeRequest> selectedPractices,
        GameStats game,
        IReadOnlySet<long> evidenceObjectiveIds)
    {
        var selectedById = selectedPractices.ToDictionary(static item => item.ObjectiveId);
        var savedById = savedObjectives.ToDictionary(static item => item.ObjectiveId);
        var priorityObjectiveId = priorityObjective?.Id ?? 0;
        var results = new List<ReviewObjectiveState>();

        foreach (var objective in activeObjectives)
        {
            savedById.TryGetValue(objective.Id, out var saved);
            selectedById.TryGetValue(objective.Id, out var selected);

            var (verdict, sign) = BuildCriteriaVerdict(
                objective.CriteriaMetric, objective.CriteriaOp, objective.CriteriaValue, game);

            // P-008: evidence linked to this objective on this game defaults
            // the toggle ON — but never over an explicit saved/draft answer.
            var evidenceDefault = selected is null && saved is null
                && evidenceObjectiveIds.Contains(objective.Id);

            results.Add(new ReviewObjectiveState(
                ObjectiveId: objective.Id,
                Title: objective.Title,
                Criteria: objective.CompletionCriteria,
                Phase: objective.Phase,
                IsPriority: objective.Id == priorityObjectiveId,
                Practiced: selected?.Practiced ?? saved?.Practiced ?? evidenceDefault,
                ExecutionNote: selected?.ExecutionNote ?? saved?.ExecutionNote ?? "",
                CriteriaVerdict: verdict,
                CriteriaVerdictSign: sign,
                PracticedFromEvidence: evidenceDefault));

            savedById.Remove(objective.Id);
        }

        foreach (var leftover in savedById.Values)
        {
            selectedById.TryGetValue(leftover.ObjectiveId, out var selected);

            var (verdict, sign) = BuildCriteriaVerdict(
                leftover.CriteriaMetric, leftover.CriteriaOp, leftover.CriteriaValue, game);

            results.Add(new ReviewObjectiveState(
                ObjectiveId: leftover.ObjectiveId,
                Title: leftover.Title,
                Criteria: leftover.CompletionCriteria,
                Phase: leftover.Phase,
                IsPriority: leftover.ObjectiveId == priorityObjectiveId,
                Practiced: selected?.Practiced ?? leftover.Practiced,
                ExecutionNote: selected?.ExecutionNote ?? leftover.ExecutionNote,
                CriteriaVerdict: verdict,
                CriteriaVerdictSign: sign));
        }

        return results;
    }

    /// <summary>
    /// v2.18 (schema v5): live verdict line for the review screen, e.g.
    /// "HIT — 7.4 (CS per minute ≥ 7)". Empty when the objective has no
    /// structured criterion or the stat isn't available for this game.
    /// Sign: 1 = hit, -1 = miss, 0 = no verdict.
    /// </summary>
    private static (string Verdict, int Sign) BuildCriteriaVerdict(
        string criteriaMetric, string criteriaOp, double criteriaValue, GameStats game)
    {
        var met = ObjectiveCriteria.Evaluate(criteriaMetric, criteriaOp, criteriaValue, game);
        if (met is null)
        {
            return ("", 0);
        }

        var measured = ObjectiveCriteria.Measure(criteriaMetric, game);
        var measuredText = measured is null ? "?" : ObjectiveCriteria.FormatValue(measured.Value);
        var line = $"{(met.Value ? "HIT" : "MISS")} — {measuredText} ({ObjectiveCriteria.Describe(criteriaMetric, criteriaOp, criteriaValue)})";
        return (line, met.Value ? 1 : -1);
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
