#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapEvidence(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ── GET /api/derived?gameId=N (token-gated): the BUILT derived-event instances
        // for one game, shaped for the VOD timeline. Reads the persisted instances via
        // IDerivedEventsRepository.GetInstancesAsync (computed/saved during game capture);
        // this endpoint never recomputes. sourceEventIds is intentionally dropped — the
        // timeline only needs id/definition/color/span/count/sourceTypes to draw + label
        // a region. Empty instances list (game never had derived events computed) is a
        // valid { ok:true, instances:[] }, not an error.
        app.MapGet("/api/derived", async (long gameId, IDerivedEventsRepository derived) =>
        {
            var records = await derived.GetEligibleInstancesAsync(gameId);
            var instances = records.Select(static r => new
            {
                id = r.Id,
                definitionId = r.DefinitionId,
                definitionName = r.DefinitionName,
                color = r.Color,
                startTimeSeconds = r.StartTimeSeconds,
                endTimeSeconds = r.EndTimeSeconds,
                eventCount = r.EventCount,
                sourceTypes = r.SourceTypes,
            });
            return Results.Json(new { ok = true, instances }, jsonOptions);
        });

        // ─────────────────────────────────────────────────────────────────────────────
        // REVIEW-PAGE granular writes (Batch 2). Unlike POST /api/review/save (the whole
        // form at once), these are the SINGLE-FIELD, persist-immediately interactions:
        // one-tap death cause chips, evidence triage, prompt answers, focus adherence.
        // Each reuses a Revu.Core write method verbatim. All token-gated + backup-guarded.
        // ─────────────────────────────────────────────────────────────────────────────

        // ── SHARED EVIDENCE TRIAGE (Review + VOD pages both POST these) ───────────────
        // The evidence "inbox" rows are written by both the Review page (EVIDENCE TO SORT
        // + per-objective ATTACHED) and the VOD player. Three single-field upserts.

        // POST /api/evidence/polarity  { evidenceId, polarity }  (good|neutral|bad)
        // Mirrors ReviewViewModel.SetEvidencePolarityAsync: set the polarity, and if the
        // row was still needs_review promote it to evidence (a triaged judgement).
        app.MapPost("/api/evidence/polarity", async (EvidencePolarityBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.EvidenceId <= 0)
                return Results.BadRequest(new { error = "evidenceId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            var polarity = Revu.Core.Data.Repositories.EvidencePolarities.Normalize(body.Polarity);
            await w.Evidence.UpdatePolarityAsync(body.EvidenceId, polarity);
            // A polarity judgement promotes an untriaged row out of needs_review.
            await w.Evidence.UpdateStatusAsync(body.EvidenceId, Revu.Core.Data.Repositories.EvidenceStatuses.Evidence);
            log.LogInformation("Evidence {Id} polarity={Polarity}", body.EvidenceId, polarity);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/evidence/objective  { evidenceId, objectiveId? }  (null detaches)
        // Mirrors ReviewViewModel.SetEvidenceObjectiveAsync: attach to an objective; when
        // an objective is set, promote needs_review->evidence AND mark that objective
        // practiced for this game (the evidence IS the proof the objective was practiced).
        app.MapPost("/api/evidence/objective", async (EvidenceObjectiveBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.EvidenceId <= 0)
                return Results.BadRequest(new { error = "evidenceId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            long? objectiveId = (body.ObjectiveId is > 0) ? body.ObjectiveId : null;
            await w.Evidence.UpdateObjectiveAsync(body.EvidenceId, objectiveId);
            if (objectiveId is long oid && body.GameId is > 0)
            {
                // Promote out of needs_review and mark the objective practiced this game,
                // preserving any existing execution note (mirror MarkObjectivePracticed-
                // FromEvidenceAsync: only flips practiced->true, never clobbers the note).
                await w.Evidence.UpdateStatusAsync(body.EvidenceId, Revu.Core.Data.Repositories.EvidenceStatuses.Evidence);
                var existing = await w.Objectives.GetGameObjectivesAsync(body.GameId.Value);
                var note = existing.FirstOrDefault(r => r.ObjectiveId == oid)?.ExecutionNote ?? "";
                await w.Objectives.RecordGameAsync(body.GameId.Value, oid, practiced: true, executionNote: note);
            }
            log.LogInformation("Evidence {Id} -> objective {ObjectiveId}", body.EvidenceId, objectiveId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/evidence/prompt  { evidenceId, promptId? }  (null/<=0 detaches)
        // P-027: tag an evidence row to the custom prompt it answers so the review can
        // group clips under that prompt. Independent of objective_id (both coexist) and
        // carries no score award — see IEvidenceRepository.UpdatePromptAsync.
        app.MapPost("/api/evidence/prompt", async (EvidencePromptBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.EvidenceId <= 0)
                return Results.BadRequest(new { error = "evidenceId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            long? promptId = (body.PromptId is > 0) ? body.PromptId : null;
            await w.Evidence.UpdatePromptAsync(body.EvidenceId, promptId);
            log.LogInformation("Evidence {Id} -> prompt {PromptId}", body.EvidenceId, promptId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/evidence/status  { evidenceId, status }  (needs_review|evidence|dismissed|highlight)
        // Mirrors ReviewViewModel.SetEvidenceStatusAsync (dismiss is the common case).
        app.MapPost("/api/evidence/status", async (EvidenceStatusBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.EvidenceId <= 0)
                return Results.BadRequest(new { error = "evidenceId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            var status = Revu.Core.Data.Repositories.EvidenceStatuses.Normalize(body.Status);
            await w.Evidence.UpdateStatusAsync(body.EvidenceId, status);
            log.LogInformation("Evidence {Id} status={Status}", body.EvidenceId, status);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // ─────────────────────────────────────────────────────────────────────────────
        // VOD BOOKMARK CRUD (Batch 2). The VOD player's Quick Bookmark tool + the
        // bookmark-list edit/delete/tag/quality actions. These are PLAIN note-bookmark
        // writes — clip extraction (ffmpeg) is DEFERRED to Batch 3, so AddBookmarkAsync
        // is never passed clip ranges/paths here. Each reuses an IVodRepository method
        // verbatim. All token-gated + backup-guarded.
        // ─────────────────────────────────────────────────────────────────────────────

        // POST /api/bookmark/add  { gameId, timeS, note?, objectiveId?, promptId? }
        // Quick note-bookmark at the current video time (B key / Add button). Returns the
        // new bookmark id so the frontend can optimistically render the row. Mirrors
        // VodPlayerViewModel.AddBookmarkCommand (sans the clip fields).
        app.MapPost("/api/encounter/save", async (SaveEncounterBody body, WriteServices w, SidecarEventHub hub) =>
        {
            if (body is null) return Results.BadRequest(new { error = "Encounter required." });
            await w.BackupGuard.EnsureBackedUpAsync();
            try
            {
                var id = await w.ReviewedEncounters.SaveAsync(body.GameId, body.EventId,
                    body.RequestId, body.StartS, body.EndS, body.Classification, body.Note);
                // v3.11: the legacy encounter form writes through the corrections ledger now
                // (kept one release as an alias of /api/event/correct). Run rule F and tell
                // every open page the timeline changed.
                await w.EventCorrections.RefreshDerivedAsync(body.GameId);
                hub.Publish("eventsCorrected", new { gameId = body.GameId, correctionId = body.RequestId, op = "encounter" });
                return Results.Json(new { ok = true, id }, jsonOptions);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // ─────────────────────────────────────────────────────────────────────────────
        // v3.11 EVENT CORRECTIONS LEDGER. A timeline fix (retype / retime / attr / remove /
        // add / confirm) is one append-only ledger row keyed on the event's stable event_key,
        // applied in place by Revu.Core (rules D and E) and followed by rule F here (derived
        // recompute + pattern re-materialize). Every validation message is the exact sentence
        // the panel shows (400 { error }). Both writes publish `eventsCorrected` so the VOD
        // player, the review page and the patterns page refetch without touching playback.
        // ─────────────────────────────────────────────────────────────────────────────

        // POST /api/event/correct  { gameId, correctionId, op, subject?, patch?, reason? }
        app.MapPost("/api/event/correct", async (SaveCorrectionBody body, WriteServices w, SidecarEventHub hub, UpdateService updates, ILogger<Program> log) =>
        {
            if (body is null || body.GameId <= 0) return Results.BadRequest(new { error = "Correction required." });
            await w.BackupGuard.EnsureBackedUpAsync();
            try
            {
                var request = CorrectionRequestMapper.From(body, updates.CurrentVersion);
                var r = await w.EventCorrections.SaveAsync(request);
                if (!r.Idempotent) hub.Publish("eventsCorrected", new { gameId = body.GameId, correctionId = r.CorrectionId, op = r.Op });
                log.LogInformation("Event correction {Op} on game {GameId}: {Key} -> {State}", r.Op, body.GameId, r.EventKey, r.State);
                return Results.Json(new { ok = true, id = r.Id, correctionId = r.CorrectionId, op = r.Op, state = r.State,
                    eventId = r.AppliedEventId, eventKey = r.EventKey, idempotent = r.Idempotent, message = r.Message }, jsonOptions);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // POST /api/correction/revert  { gameId, correctionId, reason? }
        app.MapPost("/api/correction/revert", async (RevertCorrectionBody body, WriteServices w, SidecarEventHub hub, UpdateService updates, ILogger<Program> log) =>
        {
            if (body is null || body.GameId <= 0 || string.IsNullOrWhiteSpace(body.CorrectionId))
                return Results.BadRequest(new { error = "Correction required." });
            await w.BackupGuard.EnsureBackedUpAsync();
            try
            {
                var r = await w.EventCorrections.RevertAsync(body.GameId, body.CorrectionId.Trim(), (body.Reason ?? "").Trim(), updates.CurrentVersion);
                if (!r.Idempotent) hub.Publish("eventsCorrected", new { gameId = body.GameId, correctionId = r.CorrectionId, op = r.Op });
                log.LogInformation("Event correction reverted on game {GameId}: {Key} -> {State}", body.GameId, r.EventKey, r.State);
                return Results.Json(new { ok = true, id = r.Id, correctionId = r.CorrectionId, op = r.Op, state = r.State,
                    eventId = r.AppliedEventId, eventKey = r.EventKey, idempotent = r.Idempotent, message = r.Message }, jsonOptions);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // GET /api/corrections?gameId=N — every non-revert ledger row of the game, newest first.
        app.MapGet("/api/corrections", async (long? gameId, IEventCorrectionsRepository corrections) =>
        {
            if (gameId is not > 0) return Results.BadRequest(new { error = "gameId required" });
            var rows = await corrections.GetForGameAsync(gameId.Value);
            return Results.Json(new { ok = true, gameId = gameId.Value, corrections = rows.Select(VodCorrectionMapper.Map).ToList() }, jsonOptions);
        });

        // GET /api/corrections/export[?gameId=N] — the phase-1 local JSON export (unredacted,
        // revert rows included; the panel copies it to the clipboard). gameId omitted = every game.
        app.MapGet("/api/corrections/export", async (long? gameId, IEventCorrectionsRepository corrections, UpdateService updates, ILogger<Program> log) =>
        {
            long? scope = gameId is > 0 ? gameId : null;
            var json = await corrections.ExportAsync(scope, updates.CurrentVersion);
            var count = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    count = items.GetArrayLength();
            }
            catch (JsonException) { /* count stays 0; the payload is still handed over */ }
            var fileName = $"revu-corrections-{(scope is { } g ? g.ToString() : "all")}-{DateTime.Now:yyyyMMdd-HHmm}.json";
            log.LogInformation("Corrections export built ({Count} rows, {Chars} chars)", count, json.Length);
            return Results.Json(new { ok = true, json, count, fileName }, jsonOptions);
        });

        app.MapPost("/api/events/reprocess/{gameId:long}", async (long gameId, WriteServices w, SidecarEventHub hub, CancellationToken ct) =>
        {
            if (await w.Games.GetAsync(gameId) is null) return Results.NotFound();
            await w.Config.LoadAsync();
            await w.BackupGuard.EnsureBackedUpAsync();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var result = await w.MapStateBackfill.RunForGameAsync(gameId, deadline.Token);
            if (result.Updated > 0) hub.Publish("mapStateUpdated", new { gameId, updated = result.Updated });
            return Results.Json(new { ok = result.Scanned > 0 && result.Failed == 0, result, recoveryMode = "shadow", note = "Death-recap candidates are retained for validation; no automatic trade or fight-number claims are enabled." }, jsonOptions);
        });
    }
}
