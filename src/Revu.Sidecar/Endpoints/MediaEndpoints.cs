#nullable enable

using System.Text;
using System.Text.Json;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

public static partial class SidecarEndpoints
{
    private static void MapMedia(WebApplication app, JsonSerializerOptions jsonOptions)
    {
        // ── GET /api/vod?gameId=N (token-gated): VOD file path + bookmarks ───────────
        app.MapGet("/api/vod", async (long gameId, VodSnapshotBuilder builder, CancellationToken ct) =>
            Results.Json(await builder.BuildAsync(gameId, ct), jsonOptions));

        app.MapPost("/api/bookmark/add", async (AddBookmarkBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            var id = await BookmarkPersistence.AddAsync(
                w.Vod, w.Objectives,
                gameId: body.GameId,
                gameTimeSeconds: body.TimeS,
                note: body.Note ?? "",
                objectiveId: body.ObjectiveId,
                promptId: body.PromptId,
                reviewDrafts: w.ReviewDrafts);
            log.LogInformation("Bookmark added for game {GameId} @{TimeS}s -> id {Id}", body.GameId, body.TimeS, id);
            return Results.Json(new { ok = true, id }, jsonOptions);
        });

        // POST /api/bookmark/note  { bookmarkId, note }  — edit a bookmark's note.
        app.MapPost("/api/bookmark/note", async (BookmarkNoteBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.BookmarkId <= 0)
                return Results.BadRequest(new { error = "bookmarkId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Vod.UpdateBookmarkAsync(body.BookmarkId, note: body.Note ?? "");
            log.LogInformation("Bookmark {Id} note updated", body.BookmarkId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/bookmark/delete  { bookmarkId }  — remove a bookmark.
        app.MapPost("/api/bookmark/delete", async (BookmarkIdBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.BookmarkId <= 0)
                return Results.BadRequest(new { error = "bookmarkId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Vod.DeleteBookmarkAsync(body.BookmarkId);
            log.LogInformation("Bookmark {Id} deleted", body.BookmarkId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/bookmark/objective  { bookmarkId, objectiveId? }  — attach/detach the
        // objective tag (null detaches). Distinct from /tag: objective-only, no prompt.
        app.MapPost("/api/bookmark/objective", async (BookmarkObjectiveBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.BookmarkId <= 0)
                return Results.BadRequest(new { error = "bookmarkId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await BookmarkPersistence.SetTagAsync(w.Vod, w.Objectives,
                body.BookmarkId, body.ObjectiveId, reviewDrafts: w.ReviewDrafts);
            log.LogInformation("Bookmark {Id} objective={ObjectiveId}", body.BookmarkId, body.ObjectiveId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/bookmark/tag  { bookmarkId, objectiveId?, promptId? }  — set objective
        // + optional prompt atomically (pass both null to detach). Mirrors
        // IVodRepository.SetBookmarkTagAsync.
        app.MapPost("/api/bookmark/tag", async (BookmarkTagBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.BookmarkId <= 0)
                return Results.BadRequest(new { error = "bookmarkId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await BookmarkPersistence.SetTagAsync(w.Vod, w.Objectives,
                body.BookmarkId, body.ObjectiveId, body.PromptId, w.ReviewDrafts);
            log.LogInformation("Bookmark {Id} tag objective={ObjectiveId} prompt={PromptId}", body.BookmarkId, body.ObjectiveId, body.PromptId);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // POST /api/bookmark/quality  { bookmarkId, quality }  — good|neutral|bad (or "").
        app.MapPost("/api/bookmark/quality", async (BookmarkQualityBody body, WriteServices w, ILogger<Program> log) =>
        {
            if (body is null || body.BookmarkId <= 0)
                return Results.BadRequest(new { error = "bookmarkId required" });
            await w.BackupGuard.EnsureBackedUpAsync();
            await w.Vod.UpdateBookmarkAsync(body.BookmarkId, quality: body.Quality ?? "");
            log.LogInformation("Bookmark {Id} quality={Quality}", body.BookmarkId, body.Quality);
            return Results.Json(new { ok = true }, jsonOptions);
        });

        // ─────────────────────────────────────────────────────────────────────────────
        // CLIP EXTRACTION (Batch 3). ffmpeg clip export from the VOD player's Clip tool.
        // Reuses IClipService.ExtractClipAsync (bundled-or-PATH ffmpeg, two-stage copy →
        // re-encode) verbatim, then the SAME IVodRepository.AddBookmarkAsync + Evidence
        // upsert the WinUI ExtractClipCommand runs. The .mp4 lands in ClipsFolder from
        // config. Token-gated + backup-guarded.
        // ─────────────────────────────────────────────────────────────────────────────

        // POST /api/clip/extract  { gameId, vodPath, championName?, startTimeS, endTimeS,
        //   note?, quality?, objectiveId?, promptId? }
        // Mirrors VodPlayerViewModel.ExtractClipAsync EXACTLY: clamp/order the range,
        // default the note to "Clip", extract via ffmpeg into ClipsFolder, then on success
        // add a clip-backed bookmark (returns id), mark the objective practiced if tagged,
        // and upsert the evidence row (polarity = quality or neutral; status = evidence
        // when a quality is set, else needs_review). vodPath + championName ride in the
        // body — the frontend has both from the loaded VOD snapshot, matching the VM which
        // reads them from its loaded VodPath/ChampionName state. Returns the clip path +
        // bookmark id so the frontend can confirm + refetch.
        app.MapPost("/api/clip/extract", async (ExtractClipBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });
            if (string.IsNullOrWhiteSpace(body.VodPath))
                return Results.BadRequest(new { error = "vodPath required" });

            // Order + clamp the range exactly like the VM (start = min, end = max).
            var startS = Math.Max(0, Math.Min(body.StartTimeS, body.EndTimeS));
            var endS = Math.Max(0, Math.Max(body.StartTimeS, body.EndTimeS));
            if (endS - startS < 1)
                return Results.BadRequest(new { error = "clip range must be at least 1s" });
            if (NarrationRequestValidator.ExtractRangeError(startS, endS) is { } tooLong)
                return Results.Json(new { ok = false, error = tooLong }, jsonOptions, statusCode: 422);

            await w.BackupGuard.EnsureBackedUpAsync();

            var note = string.IsNullOrWhiteSpace(body.Note) ? "Clip" : body.Note.Trim();
            var quality = (body.Quality ?? "").Trim().ToLowerInvariant();
            var objectiveId = (body.ObjectiveId is > 0) ? body.ObjectiveId : null;
            var promptId = (body.PromptId is > 0) ? body.PromptId : null;

            // championName for the clip filename: prefer the body; fall back to the game.
            var champion = (body.ChampionName ?? "").Trim();
            if (champion.Length == 0)
            {
                var game = await w.Games.GetAsync(body.GameId);
                champion = game?.ChampionName ?? "";
            }

            var clipsFolder = w.Config.ClipsFolder;
            // The request-aborted token kills ffmpeg and deletes partial output; nothing is
            // persisted for an aborted request. ExtractClipAsync runs the folder eviction with
            // the new file exempt and the protected set (narrated, shared, pinned) applied.
            string? clipPath;
            try
            {
                clipPath = await w.Clips.ExtractClipAsync(body.VodPath, startS, endS, champion, clipsFolder, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                log.LogInformation("Clip extract for game {GameId} cancelled by the caller; nothing saved", body.GameId);
                return Results.Json(new { ok = false, error = "The request was cancelled." }, jsonOptions, statusCode: 499);
            }
            catch (ClipExtractionException ex)
            {
                log.LogWarning("Clip extract failed for game {GameId} ({StartS}-{EndS}s): {Reason}", body.GameId, startS, endS, ex.Reason);
                return Results.Json(new { ok = false, error = NarrationRequestValidator.ExtractLongFailed }, jsonOptions, statusCode: 422);
            }
            if (string.IsNullOrEmpty(clipPath))
            {
                log.LogWarning("Clip extract failed for game {GameId} ({StartS}-{EndS}s) — ffmpeg returned no output", body.GameId, startS, endS);
                return Results.Json(new { ok = false, error = "Clip save failed (is ffmpeg installed?)." }, jsonOptions, statusCode: 422);
            }

            // Bookmark + objective-practiced + evidence row — the SAME tail the auto-clipper
            // runs (Revu.Core ClipPersistence). sourceKey null → the default clip:{bookmarkId}.
            var bookmarkId = await Revu.Core.Services.ClipPersistence.PersistAsync(
                w.Vod, w.Objectives, w.Evidence,
                gameId: body.GameId,
                startS: startS,
                endS: endS,
                clipPath: clipPath,
                note: note,
                quality: quality,
                objectiveId: objectiveId,
                promptId: promptId,
                reviewDrafts: w.ReviewDrafts);

            log.LogInformation("Clip extracted: game {GameId} {StartS}-{EndS}s -> {Path} (bookmark {Id})", body.GameId, startS, endS, clipPath, bookmarkId);
            return Results.Json(new { ok = true, clipPath, bookmarkId }, jsonOptions);
        });

        // POST /api/clip/auto-objectives  { gameId, objectiveId? }
        // On-demand batch clip of every event tied to the user's active learning objectives
        // (when objectiveId is set, only that objective's events). Buffers each to ~45s
        // (30s before to 15s after), reusing the manual clip ffmpeg + persistence path via
        // IAutoClipService. Gated by config.AutoClipObjectivesEnabled (returns reason
        // "disabled" when off). Sequential ffmpeg at BelowNormal priority; idempotent
        // (re-running creates no duplicates). Token-gated + backup-guarded.
        // Up to 12 ffmpeg runs can outlast the client's deadline, so the batch is accepted
        // background work (SidecarJobs): a client that stops waiting no longer kills the clip
        // in progress, a repeat click joins the running batch, and progress plus the final
        // result go out as autoClipProgress SSE events.
        app.MapPost("/api/clip/auto-objectives", (AutoObjectiveClipsBody body, WriteServices w, SidecarJobs jobs,
            SidecarEventHub events, ILogger<Program> log, CancellationToken ct) =>
            AutoClipObjectivesAsync(body, w.AutoClip, w.BackupGuard.EnsureBackedUpAsync, jobs, events, jsonOptions, log, ct));

        // POST /api/clip/upload  { gameId, bookmarkId, championName? }: validates and queues a
        // background share (ClipShareWorker), returning at once. Progress and the result go
        // out as clipShareProgress SSE events; GET /api/clip/share-status is the poll fallback.
        app.MapPost("/api/clip/upload", async (ShareClipBody body, WriteServices w, ClipShareWorker share, ILogger<Program> log) =>
            (await ClipShareRequests.UploadAsync(body, w.Vod, w.ClipNarrations, w.Games, w.Config, share, log))
                .ToResult(jsonOptions));

        // ─────────────────────────────────────────────────────────────────────────────
        // POST /api/clip/delete  { gameId, bookmarkId }  — TRULY delete a saved clip:
        // the uploaded copy (if shared), the on-disk file, and the DB rows (the clip
        // bookmark + any evidence ledger entry that referenced it). Mirrors the clip-
        // upload composition: resolve the bookmark server-side; the frontend only sends
        // ids. Order matters — remote first (needs the share_url + token before the row
        // is gone), then DB, then the local file. Remote delete is best-effort: a logged-
        // out / offline user can still purge their local copy. Backup-guarded.
        // ─────────────────────────────────────────────────────────────────────────────
        app.MapPost("/api/clip/delete", async (DeleteClipBody body, WriteServices w, ClipShareWorker share,
            RemoteClipCleanupStore cleanup, ILogger<Program> log) =>
        {
            if (body is null || body.GameId <= 0 || body.BookmarkId <= 0)
                return Results.BadRequest(new { error = "gameId and bookmarkId required" });

            // 0) A queued or running share of this clip stops first (the upload client drops
            //    its remote copy).
            share.Cancel(body.BookmarkId);

            // Resolve the bookmark server-side (the frontend never sees the file path).
            VodBookmarkRecord? bm;
            try
            {
                var marks = await w.Vod.GetBookmarksAsync(body.GameId);
                bm = marks.FirstOrDefault(m => m.Id == body.BookmarkId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Clip delete: bookmark lookup failed for game {GameId} bm {BookmarkId}", body.GameId, body.BookmarkId);
                return Results.Json(new { ok = false, error = "Couldn't load that clip." }, jsonOptions, statusCode: 422);
            }
            if (bm is null)
                return Results.Json(new { ok = false, error = "Clip not found." }, jsonOptions, statusCode: 404);

            await w.BackupGuard.EnsureBackedUpAsync();

            // 1) Remote copy (best-effort). Derive the public slug from the stored share
            //    URL (revu.lol/<slug>) and ask the proxy to delete it. Owner-only on the
            //    server; failures (offline / logged-out / expired) are logged, NOT fatal —
            //    the user still gets their local clip removed.
            //    A delete that cannot go through now (signed out, offline) is queued for the
            //    next signed-in start.
            var slug = ExtractClipSlug(bm.ShareUrl);
            var remoteDeleted = slug.Length > 0
                && await ClipShareLinks.DeleteOrQueueAsync(slug, w.Config, w.ClipUpload, cleanup, log);

            // 2) DB rows (clip bookmark + evidence tie), one transaction.
            ClipDeletionInfo? info;
            try
            {
                info = await w.Vod.DeleteClipFullAsync(body.BookmarkId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Clip delete: DB delete failed for bm {BookmarkId}", body.BookmarkId);
                return Results.Json(new { ok = false, error = "Couldn't delete the clip." }, jsonOptions, statusCode: 500);
            }

            // A share that finished between the read above and the row delete stored a link
            // the first step never saw: remove that copy too.
            if (slug.Length == 0)
            {
                var lateSlug = ExtractClipSlug(info?.ShareUrl);
                if (lateSlug.Length > 0)
                    remoteDeleted = await ClipShareLinks.DeleteOrQueueAsync(lateSlug, w.Config, w.ClipUpload, cleanup, log);
            }

            // 3) Local file (best-effort, path-guarded). Skip a non-video path — a tampered
            //    clip_path must never make the app delete an arbitrary file.
            var fileDeleted = false;
            var clipPath = info?.ClipPath ?? "";
            if (IsDeletableClipFile(clipPath))
            {
                try
                {
                    if (File.Exists(clipPath)) { File.Delete(clipPath); fileDeleted = true; }
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Clip delete: file delete failed for {Path}", clipPath);
                }
            }

            // 4) Narration voice track (narration folder only) + narrated render (clip guard).
            NarrationFileGuard.DeleteNarrationFiles(info?.NarrationAudioPath, info?.NarratedClipPath, log);

            log.LogInformation("Clip deleted: bm {BookmarkId} (file={FileDeleted}, remote={RemoteDeleted})",
                body.BookmarkId, fileDeleted, remoteDeleted);
            return Results.Json(new { ok = true, fileDeleted, remoteDeleted }, jsonOptions);
        });
    }

    /// <summary>
    /// POST /api/clip/auto-objectives as accepted work: starts (or joins) the game's batch and
    /// waits for it while the client is listening. A client that gives up gets 499 and the
    /// batch keeps going; its final autoClipProgress event (finished = true) tells any open
    /// player to re-read its clips. The reply and every event carry the run's batch id, so a
    /// page that already showed a batch's result can ignore that batch's finished event.
    /// </summary>
    internal static async Task<IResult> AutoClipObjectivesAsync(AutoObjectiveClipsBody body, IAutoClipService autoClip,
        Func<Task> ensureBackedUp, SidecarJobs jobs, SidecarEventHub events, JsonSerializerOptions jsonOptions,
        ILogger log, CancellationToken ct)
    {
        if (body is null || body.GameId <= 0)
            return Results.BadRequest(new { error = "gameId required" });

        await ensureBackedUp();

        var gameId = body.GameId;
        var objectiveId = (body.ObjectiveId is > 0) ? body.ObjectiveId : null;
        var batch = Guid.NewGuid().ToString("N");
        // One batch per game: a repeat click for the same objective (or a retry after a client
        // timeout) joins it instead of planning the same clips twice. A click for another
        // objective (or for all of them) is refused while it runs: joining would answer it with
        // this batch's result and never clip what it asked for.
        var job = jobs.Start($"auto-clip:{gameId}", objectiveId is { } only ? $"objective:{only}" : "all",
            "auto-clip objectives", async stopping =>
        {
            try
            {
                var result = await autoClip.ClipObjectiveEventsAsync(gameId, objectiveId, stopping,
                    new InlineJobProgress<AutoClipProgress>(p => events.Publish("autoClipProgress", new
                    {
                        gameId, objectiveId, batch, done = p.Done, total = p.Total, created = p.Created, skipped = p.Skipped,
                        finished = false,
                    })));
                log.LogInformation("Auto-clip objectives: game {GameId} created {Created} skipped {Skipped} ({Reason})",
                    gameId, result.Created, result.Skipped, result.Reason);
                events.Publish("autoClipProgress", new
                {
                    gameId, objectiveId, batch, created = result.Created, skipped = result.Skipped, reason = result.Reason,
                    finished = true, ok = true,
                });
                return (Batch: batch, Result: result);
            }
            catch (Exception) when (!stopping.IsCancellationRequested)
            {
                events.Publish("autoClipProgress", new { gameId, objectiveId, batch, finished = true, ok = false });
                throw;
            }
        }, out var busy);
        if (busy)
            return Results.Json(new { ok = false, error = "Auto-clip is already running for this game; try again when it finishes." },
                jsonOptions, statusCode: 409);
        if (job is null)
            return Results.Json(new { ok = false, error = "Revu is closing; auto-clip was not started." }, jsonOptions, statusCode: 503);

        try
        {
            var (runBatch, result) = await job.WaitAsync(ct);
            return Results.Json(new { ok = true, batch = runBatch, created = result.Created, skipped = result.Skipped, reason = result.Reason },
                jsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.LogInformation("Auto-clip for game {GameId}: the client stopped waiting; the batch keeps running", gameId);
            return Results.Json(new { ok = false, error = "The request was cancelled." }, jsonOptions, statusCode: 499);
        }
        catch (OperationCanceledException)
        {
            return Results.Json(new { ok = false, error = "Revu is closing; auto-clip stopped. Clips already saved are kept." },
                jsonOptions, statusCode: 503);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Auto-clip objectives failed for game {GameId}", gameId);
            return Results.Json(new { ok = false, error = "Auto-clip failed (is ffmpeg installed?)." }, jsonOptions, statusCode: 422);
        }
    }

    // The public slug in a revu.lol/<slug> share URL (the last non-empty path segment),
    // or "" when the URL is empty / unparseable. Used to target the remote clip delete.
    static string ExtractClipSlug(string? shareUrl) => ClipShareLinks.SlugFromUrl(shareUrl);

    // True only when the path is well-formed and ends in a known clip video extension.
    // Same guard GameRepository's cascade-delete uses: a clip is always a video container,
    // so this blocks a tampered clip_path from deleting a non-clip file.
    static bool IsDeletableClipFile(string clipPath) => NarrationFileGuard.IsDeletableClipPath(clipPath);
}
