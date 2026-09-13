#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapReviews(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ── GET /api/review[?gameId=N] (token-gated): single-game review snapshot ────
        // With gameId, loads THAT game (clicking a game row); without, the sample subject.
        app.MapGet("/api/review", async (long? gameId, ReviewSnapshotBuilder builder, CancellationToken ct) =>
        {
            var snapshot = await builder.BuildAsync(gameId, ct);
            return Results.Json(snapshot, jsonOptions);
        });

        // GET /api/review/export?gameId=N — SINGLE-game review markdown (for the review
        // page's Copy + Export). Reuses ReviewExportService.ExportGameAsync (returns null
        // when the game doesn't exist). The review page copies the markdown to the
        // clipboard or saves it via the native dialog (save_export_file).
        app.MapGet("/api/review/export", async (long gameId, IReviewExportService export, ILogger<Program> log, CancellationToken ct) =>
        {
            if (gameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            var markdown = await export.ExportGameAsync(gameId, ct);
            if (markdown is null)
                return Results.Json(new { ok = true, found = false }, jsonOptions);
            var fileName = $"revu-{gameId}-review.md";
            log.LogInformation("Single-game review export built ({Chars} chars) -> {FileName}", markdown.Length, fileName);
            return Results.Json(new { ok = true, found = true, markdown, fileName }, jsonOptions);
        });

        // POST /api/review/save — the full post-game review write (multi-table, via the
        // SAME IReviewWorkflowService.SaveAsync the WinUI app uses).
        app.MapPost("/api/review/save", async (SaveReviewBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            await w.BackupGuard.EnsureBackedUpAsync();

            // Resolve free-text tags (typed in the review tag input) to catalog ids —
            // find-or-create each by name — then merge with the toggled catalog ids so a
            // tag the user typed actually persists. Done only on explicit save (NOT draft
            // autosave) so frequent drafts don't spawn catalog tags per keystroke.
            var tagIds = new List<long>(body.SelectedTagIds ?? new List<long>());
            if (body.FreeTextTags is { Count: > 0 })
            {
                var existing = await w.ConceptTags.GetAllAsync();
                foreach (var raw in body.FreeTextTags)
                {
                    var name = (raw ?? "").Trim();
                    if (name.Length == 0) continue;
                    var match = existing.FirstOrDefault(t =>
                        string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                    var tagId = match is not null ? match.Id : await w.ConceptTags.CreateAsync(name);
                    if (tagId > 0 && !tagIds.Contains(tagId)) tagIds.Add(tagId);
                }
            }

            // NULL passthrough (NOT ?? "") for the fields the desktop form doesn't render, so
            // an omitted field leaves the persisted value unchanged instead of zeroing it
            // (SaveAsync skips the write when the value is null). The fields the form DOES
            // submit keep ?? "" — an empty string from a cleared textarea is a real value.
            var snapshot = new Revu.Core.Services.ReviewSnapshot(
                MentalRating: body.MentalRating,
                WentWell: body.WentWell ?? "",
                Mistakes: body.Mistakes ?? "",
                FocusNext: body.FocusNext ?? "",
                ReviewNotes: body.ReviewNotes ?? "",
                ImprovementNote: body.ImprovementNote,
                Attribution: body.Attribution ?? "",
                MentalHandled: body.MentalHandled,
                SpottedProblems: body.SpottedProblems ?? "",
                OutsideControl: body.OutsideControl,
                WithinControl: body.WithinControl,
                PersonalContribution: body.PersonalContribution,
                EnemyLaner: body.EnemyLaner,
                MatchupNote: body.MatchupNote,
                SelectedTagIds: tagIds,
                ObjectivePractices: (body.ObjectivePractices ?? new List<ObjectivePracticeBody>())
                    .Select(p => new Revu.Core.Services.SaveObjectivePracticeRequest(p.ObjectiveId, p.Practiced, p.ExecutionNote ?? ""))
                    .ToList(),
                FocusAdherence: body.FocusAdherence);

            var request = new Revu.Core.Services.SaveReviewRequest(
                GameId: body.GameId,
                ChampionName: body.ChampionName ?? "",
                Win: body.Win,
                RequireReviewNotes: false,
                Snapshot: snapshot);

            var result = await w.ReviewWorkflow.SaveAsync(request, ct);
            if (!result.Success)
                return Results.Json(new { ok = false, error = result.ErrorMessage }, jsonOptions, statusCode: 422);
            // v3.6: refresh the game's failed-criterion anchors — a review save can
            // change objective practices/criteria outcomes (best-effort; on failure
            // un-stamp so the next startup backfill re-materializes the game).
            try { await w.PatternMaterializer.MaterializeReviewSignalsAsync(body.GameId); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Review-signal materialization failed for game {GameId}", body.GameId);
                try { await w.Games.UpdatePatternEvidenceVersionAsync(body.GameId, 0); } catch { /* same outage; next launch retries */ }
            }
            log.LogInformation("Review saved for game {GameId}", body.GameId);
            return Results.Json(new { ok = true, savedEnemyLaner = result.SavedEnemyLaner }, jsonOptions);
        });

        // POST /api/review/skip  { gameId } — mark reviewed without opening.
        app.MapPost("/api/review/skip", async (GameIdBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.SessionLog.MarkSkippedAsync(body.GameId);
            // A skip is an explicit "no notes" — discard any autosaved draft so the
            // abandoned text can't resurrect as "Unsaved edits restored" later.
            try { await w.ReviewDrafts.DeleteAsync(body.GameId); }
            catch (Exception ex) { log.LogDebug(ex, "Skip: draft cleanup failed for game {GameId}", body.GameId); }
            log.LogInformation("Review skipped for game {GameId}", body.GameId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/review/delete  { gameId } — DELETE a saved review, returning the game
        // to the unreviewed queue. Clears the review text + session_log review markers +
        // concept tags + matchup note + draft, but PRESERVES objective progress and the
        // session_log behavioral fields (mental/adherence streaks stay intact). Keeps the
        // game row (this is NOT a game delete). Backup guard runs first, like every write.
        app.MapPost("/api/review/delete", async (GameIdBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            var result = await w.ReviewWorkflow.DeleteAsync(body.GameId, ct);
            if (!result.Success)
                return Results.Json(new { ok = false, error = result.ErrorMessage }, jsonOptions, statusCode: 422);
            log.LogInformation("Review deleted for game {GameId}", body.GameId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/review/draft/save — persist an in-progress review WITHOUT finalizing
        // it, so navigating away (e.g. to the VOD player) doesn't lose edits. Mirrors
        // ReviewViewModel.WatchVodCommand: SaveDraftAsync(ReviewDraftRequest(GameId,
        // BuildSnapshot())). Reuses the SAME ReviewSnapshot shape POST /api/review/save
        // builds — body fields are identical minus championName/win/requireReviewNotes.
        app.MapPost("/api/review/draft/save", async (SaveReviewDraftBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            await w.BackupGuard.EnsureBackedUpAsync();

            var snapshot = new Revu.Core.Services.ReviewSnapshot(
                MentalRating: body.MentalRating,
                WentWell: body.WentWell ?? "",
                Mistakes: body.Mistakes ?? "",
                FocusNext: body.FocusNext ?? "",
                ReviewNotes: body.ReviewNotes ?? "",
                ImprovementNote: body.ImprovementNote ?? "",
                Attribution: body.Attribution ?? "",
                MentalHandled: body.MentalHandled ?? "",
                SpottedProblems: body.SpottedProblems ?? "",
                OutsideControl: body.OutsideControl ?? "",
                WithinControl: body.WithinControl ?? "",
                PersonalContribution: body.PersonalContribution ?? "",
                EnemyLaner: body.EnemyLaner ?? "",
                MatchupNote: body.MatchupNote ?? "",
                SelectedTagIds: body.SelectedTagIds ?? new List<long>(),
                ObjectivePractices: (body.ObjectivePractices ?? new List<ObjectivePracticeBody>())
                    .Select(p => new Revu.Core.Services.SaveObjectivePracticeRequest(p.ObjectiveId, p.Practiced, p.ExecutionNote ?? ""))
                    .ToList(),
                FocusAdherence: body.FocusAdherence);

            var request = new Revu.Core.Services.ReviewDraftRequest(GameId: body.GameId, Snapshot: snapshot);

            var ok = await w.ReviewWorkflow.SaveDraftAsync(request, ct);
            log.LogInformation("Review draft saved for game {GameId} (ok={Ok})", body.GameId, ok);
            return Results.Json(new { ok }, jsonOptions);
        });

        // ── DEATH AUDIT (per-death cause classification) ──────────────────────────────
        // POST /api/death/classify  { gameId, timeS, key }  — one-tap cause chip.
        // Upsert keyed on (gameId, gameTimeSeconds); the repo normalizes the class key.
        app.MapPost("/api/death/classify", async (DeathClassifyBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.GameId <= 0 || string.IsNullOrWhiteSpace(body.Key))
                return Results.BadRequest(new { error = "gameId and key required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.DeathClassifications.UpsertAsync(body.GameId, body.TimeS, body.Key.Trim());
            log.LogInformation("Death classified: game {GameId} @{TimeS}s -> {Key}", body.GameId, body.TimeS, body.Key);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/death/clear  { gameId, timeS }  — re-tapping the selected chip clears.
        app.MapPost("/api/death/clear", async (DeathClearBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.DeathClassifications.ClearAsync(body.GameId, body.TimeS);
            log.LogInformation("Death classification cleared: game {GameId} @{TimeS}s", body.GameId, body.TimeS);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // ── CUSTOM PROMPT ANSWERS (per objective) ─────────────────────────────────────
        // POST /api/prompt/answer/save  { promptId, gameId, text }  — upsert; empty text
        // deletes the answer row (IPromptsRepository.SaveAnswerAsync handles both).
        app.MapPost("/api/prompt/answer/save", async (PromptAnswerBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.PromptId <= 0 || body.GameId <= 0)
                return Results.BadRequest(new { error = "promptId and gameId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Prompts.SaveAnswerAsync(body.PromptId, body.GameId, body.Text ?? "");
            log.LogInformation("Prompt {PromptId} answer saved for game {GameId}", body.PromptId, body.GameId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // ── FOCUS ADHERENCE (Yes/Partly/No, immediate persist) ────────────────────────
        // POST /api/focus-adherence  { gameId, value? }  — value 2=Yes / 1=Partly / 0=No;
        // null (or omitted) clears it back to unset (-1 semantics). Persisted immediately;
        // also re-stamped at full save via Snapshot.FocusAdherence.
        app.MapPost("/api/focus-adherence", async (FocusAdherenceBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.SessionLog.UpdateFocusAdherenceAsync(body.GameId, body.Value);
            log.LogInformation("Focus adherence for game {GameId} = {Value}", body.GameId, body.Value);
            return Results.Json(new { ok = true }, jsonOptions);
        });
    }
}
