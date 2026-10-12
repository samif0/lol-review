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
    private readonly ISessionLogRepository _sessionLogRepository;
    private readonly IObjectivesRepository _objectivesRepository;
    private readonly IReviewDraftRepository _reviewDraftRepository;
    private readonly IMatchupNotesRepository _matchupNotesRepository;
    private readonly IEvidenceRepository _evidenceRepository;
    private readonly IConfigService _configService;
    private readonly ILogger<ReviewWorkflowService> _logger;

    public ReviewWorkflowService(
        IGameRepository gameRepository,
        IConceptTagRepository conceptTagRepository,
        IVodRepository vodRepository,
        ISessionLogRepository sessionLogRepository,
        IObjectivesRepository objectivesRepository,
        IReviewDraftRepository reviewDraftRepository,
        IMatchupNotesRepository matchupNotesRepository,
        IEvidenceRepository evidenceRepository,
        IConfigService configService,
        ILogger<ReviewWorkflowService> logger)
    {
        _gameRepository = gameRepository;
        _conceptTagRepository = conceptTagRepository;
        _vodRepository = vodRepository;
        _sessionLogRepository = sessionLogRepository;
        _objectivesRepository = objectivesRepository;
        _reviewDraftRepository = reviewDraftRepository;
        _matchupNotesRepository = matchupNotesRepository;
        _evidenceRepository = evidenceRepository;
        _configService = configService;
        _logger = logger;
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
            // NULL = "leave the persisted value unchanged" for the fields the Tauri
            // review form doesn't render (it omits them, so they arrive null). We
            // load the current rows first and COALESCE nulls back to what's stored,
            // so the (blind) UPDATEs re-write the existing value instead of zeroing
            // migrated/pre-existing data. A non-null value (incl. "") is an explicit
            // edit and overwrites as before.
            var existingGame = await _gameRepository.GetAsync(request.GameId);
            var existingEntry = await _sessionLogRepository.GetEntryAsync(request.GameId);

            var review = new GameReview
            {
                Rating = 1,
                Notes = request.Snapshot.ReviewNotes.Trim(),
                Mistakes = request.Snapshot.Mistakes.Trim(),
                WentWell = request.Snapshot.WentWell.Trim(),
                FocusNext = request.Snapshot.FocusNext.Trim(),
                SpottedProblems = request.Snapshot.SpottedProblems.Trim(),
                OutsideControl = request.Snapshot.OutsideControl?.Trim() ?? existingGame?.OutsideControl ?? "",
                WithinControl = request.Snapshot.WithinControl?.Trim() ?? existingGame?.WithinControl ?? "",
                Attribution = request.Snapshot.Attribution.Trim(),
                PersonalContribution = request.Snapshot.PersonalContribution?.Trim() ?? existingGame?.PersonalContribution ?? "",
            };

            await _gameRepository.UpdateReviewAsync(request.GameId, review);
            // improvement_note: null → keep the stored note (LogGameAsync writes it
            // unconditionally, so pass the existing value through rather than "").
            var improvementNote = request.Snapshot.ImprovementNote?.Trim()
                ?? existingEntry?.ImprovementNote ?? "";
            // mental rating: valid answers are 1..10. Anything else (0 from an
            // untouched slider / NaN parse, negatives) must not reach session_log
            // — a stored 0 falls outside every analytics bucket yet still drags
            // filtered averages down. Keep the existing stored value, else the
            // neutral default.
            var storedMental = existingEntry?.MentalRating ?? 0;
            var mentalRating = request.Snapshot.MentalRating is >= 1 and <= 10
                ? request.Snapshot.MentalRating
                : storedMental is >= 1 and <= 10 ? storedMental : 5;
            await _sessionLogRepository.LogGameAsync(
                request.GameId,
                request.ChampionName,
                request.Win,
                mentalRating,
                improvementNote);
            // mental_handled: null → don't touch it (skip the UPDATE entirely).
            if (request.Snapshot.MentalHandled is not null)
            {
                await _sessionLogRepository.UpdateMentalHandledAsync(request.GameId, request.Snapshot.MentalHandled.Trim());
            }
            // v2.18 (schema v5): persist the one-tap focus-adherence answer.
            // Runs after LogGameAsync so the session_log row is guaranteed.
            await _sessionLogRepository.UpdateFocusAdherenceAsync(request.GameId, request.Snapshot.FocusAdherence);
            // Clear any prior skip-marker; if the caller is doing a true
            // skip-save, they re-stamp is_skipped after this returns.
            await _sessionLogRepository.ClearSkippedAsync(request.GameId);

            // enemy_laner / matchup_notes: when the form omits BOTH (both null) leave
            // the stored enemy laner and matchup note untouched — never blank the
            // enemy or hit the DeleteForGameAsync branch on a debrief-only save. Only
            // when at least one was explicitly sent do we run the upsert/delete logic.
            var savedEnemyLaner = existingGame?.EnemyLaner ?? "";
            if (request.Snapshot.EnemyLaner is not null || request.Snapshot.MatchupNote is not null)
            {
                var trimmedEnemy = (request.Snapshot.EnemyLaner ?? existingGame?.EnemyLaner ?? "").Trim();
                var trimmedMatchupNote = (request.Snapshot.MatchupNote ?? "").Trim();
                savedEnemyLaner = trimmedEnemy;

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

            return ReviewSaveResult.Ok(savedEnemyLaner);
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
                // Draft is the autosave safety net — it writes the WHOLE snapshot
                // every time, so null (an omitted field) is just stored as "". The
                // null=unchanged convention lives on the FINAL save path, not here.
                ImprovementNote = (request.Snapshot.ImprovementNote ?? "").Trim(),
                Attribution = request.Snapshot.Attribution.Trim(),
                MentalHandled = (request.Snapshot.MentalHandled ?? "").Trim(),
                SpottedProblems = request.Snapshot.SpottedProblems.Trim(),
                OutsideControl = (request.Snapshot.OutsideControl ?? "").Trim(),
                WithinControl = (request.Snapshot.WithinControl ?? "").Trim(),
                PersonalContribution = (request.Snapshot.PersonalContribution ?? "").Trim(),
                EnemyLaner = (request.Snapshot.EnemyLaner ?? "").Trim(),
                MatchupNote = (request.Snapshot.MatchupNote ?? "").Trim(),
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
}
