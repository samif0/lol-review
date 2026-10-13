#nullable enable

using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

public sealed class ReviewWorkflowService : IReviewWorkflowService
{
    private readonly IDbConnectionFactory _dbFactory;
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
        IDbConnectionFactory dbFactory,
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
        _dbFactory = dbFactory;
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
            // NULL = "leave the persisted value unchanged" for the fields the
            // review form doesn't render (it omits them, so they arrive null). We
            // load the current rows first and COALESCE nulls back to what's stored,
            // so the (blind) UPDATEs re-write the existing value instead of zeroing
            // migrated/pre-existing data. A non-null value (incl. "") is an explicit
            // edit and overwrites as before.
            var existingGame = await _gameRepository.GetAsync(request.GameId);
            if (existingGame is null)
            {
                return ReviewSaveResult.Fail("This game is no longer saved. Reload and try again.");
            }

            var existingEntry = await _sessionLogRepository.GetEntryAsync(request.GameId);

            // Champion and result are facts of the stored game, never the client's echo
            // of them: a stale page must not rewrite session_log stats.
            var championName = existingGame.ChampionName;
            var win = existingGame.Win;

            // Criteria verdicts need each practiced objective's definition. Read them
            // before the write session opens (no reads of written tables mid-session).
            var practicedObjectives = new Dictionary<long, ObjectiveSummary>();
            foreach (var practice in request.Snapshot.ObjectivePractices.Where(p => p.Practiced))
            {
                try
                {
                    if (await _objectivesRepository.GetAsync(practice.ObjectiveId) is { } objective)
                    {
                        practicedObjectives[practice.ObjectiveId] = objective;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Criteria lookup failed for game {GameId} objective {ObjectiveId}", request.GameId, practice.ObjectiveId);
                }
            }

            var review = new GameReview
            {
                Rating = 1,
                Notes = request.Snapshot.ReviewNotes.Trim(),
                Mistakes = request.Snapshot.Mistakes.Trim(),
                WentWell = request.Snapshot.WentWell.Trim(),
                FocusNext = request.Snapshot.FocusNext.Trim(),
                SpottedProblems = request.Snapshot.SpottedProblems.Trim(),
                OutsideControl = request.Snapshot.OutsideControl?.Trim() ?? existingGame.OutsideControl ?? "",
                WithinControl = request.Snapshot.WithinControl?.Trim() ?? existingGame.WithinControl ?? "",
                Attribution = request.Snapshot.Attribution.Trim(),
                PersonalContribution = request.Snapshot.PersonalContribution?.Trim() ?? existingGame.PersonalContribution ?? "",
            };

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

            // Every write below runs in ONE transaction: a failure anywhere leaves the
            // game exactly as it was (never "reviewed" with half its practices or tags).
            var savedEnemyLaner = existingGame.EnemyLaner ?? "";
            using (var session = DbWriteSession.Begin(_dbFactory))
            {
                await _gameRepository.UpdateReviewAsync(request.GameId, review, session);
                await _sessionLogRepository.LogGameAsync(
                    request.GameId,
                    championName,
                    win,
                    mentalRating,
                    improvementNote,
                    session: session);
                // mental_handled: null → don't touch it (skip the UPDATE entirely).
                if (request.Snapshot.MentalHandled is not null)
                {
                    await _sessionLogRepository.UpdateMentalHandledAsync(request.GameId, request.Snapshot.MentalHandled.Trim(), session);
                }
                // v2.18 (schema v5): persist the one-tap focus-adherence answer.
                // Runs after LogGameAsync so the session_log row is guaranteed.
                await _sessionLogRepository.UpdateFocusAdherenceAsync(request.GameId, request.Snapshot.FocusAdherence, session);
                // Clear any prior skip-marker; if the caller is doing a true
                // skip-save, they re-stamp is_skipped after this returns.
                await _sessionLogRepository.ClearSkippedAsync(request.GameId, session);

                // enemy_laner / matchup_notes: when the form omits BOTH (both null) leave
                // the stored enemy laner and matchup note untouched — never blank the
                // enemy or hit the DeleteForGameAsync branch on a debrief-only save. Only
                // when at least one was explicitly sent do we run the upsert/delete logic.
                if (request.Snapshot.EnemyLaner is not null || request.Snapshot.MatchupNote is not null)
                {
                    var trimmedEnemy = (request.Snapshot.EnemyLaner ?? existingGame.EnemyLaner ?? "").Trim();
                    var trimmedMatchupNote = (request.Snapshot.MatchupNote ?? "").Trim();
                    savedEnemyLaner = trimmedEnemy;

                    if (!string.Equals(existingGame.EnemyLaner ?? "", trimmedEnemy, StringComparison.Ordinal))
                    {
                        await _gameRepository.UpdateEnemyLanerAsync(request.GameId, trimmedEnemy, session);
                    }

                    if (!string.IsNullOrWhiteSpace(trimmedEnemy) || !string.IsNullOrWhiteSpace(trimmedMatchupNote))
                    {
                        await _matchupNotesRepository.UpsertForGameAsync(request.GameId, championName, trimmedEnemy, trimmedMatchupNote, session);
                    }
                    else
                    {
                        await _matchupNotesRepository.DeleteForGameAsync(request.GameId, session);
                    }
                }

                foreach (var objective in request.Snapshot.ObjectivePractices)
                {
                    await _objectivesRepository.RecordGameAsync(
                        request.GameId,
                        objective.ObjectiveId,
                        objective.Practiced,
                        objective.ExecutionNote.Trim(),
                        session);

                    // v2.18 (schema v5): re-evaluate structured criteria on review
                    // save so post-game-checked objectives get a verdict too.
                    // Only practiced objectives are judged — an unpracticed
                    // criterion outcome would be noise.
                    if (objective.Practiced && practicedObjectives.TryGetValue(objective.ObjectiveId, out var definition))
                    {
                        await EvaluateCriteriaAsync(session, request.GameId, definition, existingGame);
                    }
                }

                await _conceptTagRepository.SetForGameAsync(request.GameId, request.Snapshot.SelectedTagIds, session);
                await _reviewDraftRepository.DeleteAsync(request.GameId, session);
                session.Commit();
            }

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
    /// One transaction, like the save: a failure leaves the review fully intact.
    /// </summary>
    public async Task<ReviewSaveResult> DeleteAsync(long gameId, CancellationToken cancellationToken = default)
    {
        if (gameId <= 0)
        {
            return ReviewSaveResult.Fail("Game id is required to delete a review.");
        }

        try
        {
            using var session = DbWriteSession.Begin(_dbFactory);

            // (a) games review columns → blank (Rating=0 so the COALESCE(rating,0)>0
            // queue gate is false; all text fields ''). No dedicated clear method —
            // a blank GameReview through the existing update is sufficient.
            await _gameRepository.UpdateReviewAsync(gameId, new GameReview(), session);

            // (b) session_log → clear only the queue-gating markers; KEEP mental_rating
            // / focus_adherence / rule_broken so streaks stay byte-identical.
            await _sessionLogRepository.ClearReviewMarkersAsync(gameId, session);

            // (c) concept tags → clear all for the game (empty set replaces).
            await _conceptTagRepository.SetForGameAsync(gameId, System.Array.Empty<long>(), session);

            // (d) matchup note + (e) any leftover draft → remove (not queue-gating,
            // but part of the review payload).
            await _matchupNotesRepository.DeleteForGameAsync(gameId, session);
            await _reviewDraftRepository.DeleteAsync(gameId, session);
            session.Commit();

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
    /// A failure is logged and rolled back alone; it never fails the review save.
    /// </summary>
    private async Task EvaluateCriteriaAsync(DbWriteSession session, long gameId, ObjectiveSummary objective, GameStats stats)
    {
        var failure = await session.TryStepAsync(async () =>
        {
            if (!objective.HasStructuredCriteria)
            {
                return;
            }

            var met = ObjectiveCriteria.Evaluate(
                objective.CriteriaMetric, objective.CriteriaOp, objective.CriteriaValue, stats);
            if (met is null)
            {
                return;
            }

            await _objectivesRepository.SetCriteriaMetAsync(gameId, objective.Id, met.Value, session);
        });
        if (failure is not null)
        {
            _logger.LogWarning(failure,
                "Criteria evaluation failed for game {GameId} objective {ObjectiveId}", gameId, objective.Id);
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
