#nullable enable

using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapGames(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ── GET /api/dashboard (token-gated): the read-only snapshot ──────────────────
        app.MapGet("/api/dashboard", async (DashboardSnapshotBuilder builder, CancellationToken ct) =>
        {
            var snapshot = await builder.BuildAsync(ct);
            return Results.Json(snapshot, jsonOptions);
        });

        // ── GET /api/games[?view=queue|today|history|vod][&page=N] (token-gated) ─────
        // Read-only games-workspace snapshot. view selects one of the four list views
        // (Queue=unreviewed 14d / Today / History / VOD-on-disk); unknown/missing →
        // queue. page is the zero-based History page (offset page*30); ignored by the
        // single-shot views. hasMore is true only on History when more pages remain.
        app.MapGet("/api/games", async (string? view, int? page, GamesSnapshotBuilder builder, CancellationToken ct) =>
        {
            var snapshot = await builder.BuildAsync(view, page ?? 0, ct);
            return Results.Json(snapshot, jsonOptions);
        });

        // POST /api/block/start  { intention, withCoach? }
        // v3.3: while a coaching stint is active, the block is stamped with the stint
        // id + the next 1-based block number (sticky in the sessions upsert, so a
        // same-day re-lock retags with_coach but never renumbers).
        app.MapPost("/api/block/start", async (StartBlockBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Intention))
                return Results.BadRequest(new { error = "intention required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            var stint = await w.CoachingStints.GetActiveStintAsync();
            int? stintId = stint?.Id;
            int? blockNumber = stint != null
                ? await w.CoachingStints.GetNextBlockNumberAsync(stint.Id)
                : null;
            // WithCoach omitted → preserve today's existing tag (the sessions upsert
            // overwrites with_coach unconditionally, and an intention-only caller like
            // the champ-select session box must never untag a with-coach block).
            var withCoach = body.WithCoach
                ?? (await w.SessionLog.GetSessionAsync(today()))?.WithCoach
                ?? false;
            await w.SessionLog.SetSessionIntentionAsync(
                today(), body.Intention.Trim(), withCoach, stintId, blockNumber);
            log.LogInformation(
                "Start block: intention set for {Date} (withCoach={WithCoach}, stint={StintId}, block #{BlockNumber})",
                today(), withCoach, stintId, blockNumber);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/stint/start  { name, plannedEndDate? }
        // v3.3: begin a coaching stint. One active stint at a time — starting while
        // one is open is a 400 so the user ends it deliberately (no silent close).
        // plannedEndDate is honored only as strict yyyy-MM-dd (same guard as
        // /api/block/end's date) — anything else stores as open-ended.
        app.MapPost("/api/stint/start", async (StartStintBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(body?.Name))
                return Results.BadRequest(new { error = "name required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            var active = await w.CoachingStints.GetActiveStintAsync();
            if (active != null)
                return Results.BadRequest(new { error = $"a stint is already active ({active.Name}); end it first" });
            var plannedEnd = "";
            if (!string.IsNullOrWhiteSpace(body.PlannedEndDate)
                && DateTime.TryParseExact(body.PlannedEndDate, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
            {
                plannedEnd = body.PlannedEndDate;
            }
            int id;
            try
            {
                id = await w.CoachingStints.StartStintAsync(body.Name.Trim(), today(), plannedEnd);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // The idx_coaching_stints_one_active backstop fired: a concurrent
                // start won the race between our active-check and the insert.
                return Results.BadRequest(new { error = "a stint is already active; end it first" });
            }
            log.LogInformation("Stint started: {Name} (id {Id}, planned end '{PlannedEnd}')", body.Name.Trim(), id, plannedEnd);
            return Results.Json(new { ok = true, id }, jsonOptions);
        });

        // POST /api/stint/end  {}
        // v3.3: close the active stint. Blocks keep their stint stamps — the stint's
        // data outlives it; only the "active" pointer clears.
        app.MapPost("/api/stint/end", async (WriteServices w, ILogger<Program> log) =>
        {
            await w.BackupGuard.EnsureBackedUpAsync();
            var active = await w.CoachingStints.GetActiveStintAsync();
            if (active == null)
                return Results.BadRequest(new { error = "no active stint" });
            await w.CoachingStints.EndStintAsync(active.Id);
            log.LogInformation("Stint ended: {Name} (id {Id})", active.Name, active.Id);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // GET /api/stint — the active stint + block counts for the Settings stint card.
        // READ-only; no backup guard. Uses the WRITE-graph stints repo (its read
        // methods are plain SELECTs — same precedent as GET /api/objectives/active).
        // stint is null when none is running.
        app.MapGet("/api/stint", async (WriteServices w) =>
        {
            var active = await w.CoachingStints.GetActiveStintAsync();
            if (active == null)
                return Results.Json(new { stint = (StintDto?)null }, jsonOptions);
            var counts = await w.CoachingStints.GetBlockCountsAsync(active.Id);
            var dto = new StintDto(
                Id: active.Id,
                Name: active.Name,
                StartDate: active.StartDate,
                PlannedEndDate: active.PlannedEndDate,
                BlocksTotal: counts.Total,
                BlocksWithCoach: counts.WithCoach,
                BlocksSolo: counts.Solo);
            return Results.Json(new { stint = dto }, jsonOptions);
        });

        // POST /api/block/end  { rating, note?, date? }
        // date targets the open block's own row (it can be a prior day when a block carried
        // over unfinished). Only a well-formed yyyy-MM-dd is honored; anything else falls
        // back to today so a malformed value can't write to an arbitrary row.
        app.MapPost("/api/block/end", async (EndBlockBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.Rating < 1 || body.Rating > 10)
                return Results.BadRequest(new { error = "rating must be 1-10" });
            var targetDate = today();
            if (!string.IsNullOrWhiteSpace(body.Date)
                && DateTime.TryParseExact(body.Date, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
            {
                targetDate = body.Date;
            }
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.SessionLog.SaveSessionDebriefAsync(targetDate, body.Rating, body.Note ?? "");
            log.LogInformation("End block: debrief saved ({Rating}/10) for {Date}", body.Rating, targetDate);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/reset — save a tilt-reset ritual result.
        app.MapPost("/api/reset", async (ResetBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Emotion))
                return Results.BadRequest(new { error = "emotion required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.TiltChecks.SaveAsync(
                emotion: body.Emotion.Trim(),
                intensityBefore: body.IntensityBefore,
                intensityAfter: body.IntensityAfter,
                reframeThought: body.ReframeThought ?? "",
                reframeResponse: body.ReframeResponse ?? "",
                ifThenPlan: body.IfThenPlan ?? "");
            log.LogInformation("Tilt reset saved ({Emotion})", body.Emotion);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // ─────────────────────────────────────────────────────────────────────────────
        // DESTRUCTIVE write: delete a game. Unlike the daily-loop writes above this is
        // irreversible inside the app — IGameRepository.DeleteAsync cascades across the
        // child tables in a single transaction AND snapshots a DB backup BEFORE any
        // mutation (returns the backup path). We ALSO take the session-first-write
        // safety backup (belt-and-suspenders). The frontend confirms before calling.
        // ─────────────────────────────────────────────────────────────────────────────
        app.MapPost("/api/game/delete", async (GameIdBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            var backupPath = await w.Games.DeleteAsync(body.GameId);
            log.LogInformation("Game {GameId} deleted (backup at {BackupPath})", body.GameId, backupPath);
            return Results.Json(new { ok = true, backupPath }, jsonOptions);
        });

        // POST /api/game/manual — write a manual game row + log it + record objective
        // assessments. Replicates ManualEntryDialogViewModel.SaveAsync EXACTLY:
        //   (1) IGameRepository.SaveManualAsync(...) -> gameId
        //   (2) IF gameId>0: ISessionLogRepository.LogGameAsync(gameId, champ, win, mental)
        //   (3) foreach objective: IObjectivesRepository.RecordGameAsync(gameId, objId,
        //       practiced, executionNote)
        // MentalRating reaches LogGameAsync only (NOT SaveManualAsync), matching the VM.
        app.MapPost("/api/game/manual", async (ManualGameBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.ChampionName))
                return Results.BadRequest(new { error = "championName required" });
            await w.BackupGuard.EnsureBackedUpAsync();

            var champ = body.ChampionName.Trim();
            var gameMode = string.IsNullOrWhiteSpace(body.GameMode) ? "Manual Entry" : body.GameMode.Trim();

            var gameId = await w.Games.SaveManualAsync(
                championName: champ,
                win: body.Win,
                kills: Math.Max(0, body.Kills),
                deaths: Math.Max(0, body.Deaths),
                assists: Math.Max(0, body.Assists),
                gameMode: gameMode,
                notes: (body.Notes ?? "").Trim(),
                mistakes: (body.Mistakes ?? "").Trim(),
                wentWell: (body.WentWell ?? "").Trim(),
                focusNext: (body.FocusNext ?? "").Trim());

            if (gameId > 0)
            {
                var mental = Math.Clamp(body.MentalRating <= 0 ? 5 : body.MentalRating, 1, 10);
                await w.SessionLog.LogGameAsync(gameId, champ, body.Win, mental);

                foreach (var o in (body.Objectives ?? new List<ManualObjectiveBody>()))
                {
                    if (o is null || o.ObjectiveId <= 0) continue;
                    await w.Objectives.RecordGameAsync(gameId, o.ObjectiveId, o.Practiced, o.ExecutionNote ?? "");
                }
            }

            log.LogInformation("Manual game entry saved: {Champion} ({Result}), game_id={GameId}",
                champ, body.Win ? "Win" : "Loss", gameId);
            return Results.Json(new { ok = true, gameId }, jsonOptions);
        });
    }

    private static string today() => DateTime.Now.ToString("yyyy-MM-dd");
}
