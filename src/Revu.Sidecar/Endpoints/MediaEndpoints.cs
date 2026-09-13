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
            var id = await w.Vod.AddBookmarkAsync(
                gameId: body.GameId,
                gameTimeSeconds: body.TimeS,
                note: body.Note ?? "",
                objectiveId: body.ObjectiveId,
                promptId: body.PromptId);
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
            await w.Vod.SetBookmarkObjectiveAsync(body.BookmarkId, body.ObjectiveId);
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
            await w.Vod.SetBookmarkTagAsync(body.BookmarkId, body.ObjectiveId, body.PromptId);
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
            var clipPath = await w.Clips.ExtractClipAsync(body.VodPath, startS, endS, champion, clipsFolder);
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
                promptId: promptId);

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
        app.MapPost("/api/clip/auto-objectives", async (AutoObjectiveClipsBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || body.GameId <= 0)
                return Results.BadRequest(new { error = "gameId required" });

            await w.BackupGuard.EnsureBackedUpAsync();

            try
            {
                var objectiveId = (body.ObjectiveId is > 0) ? body.ObjectiveId : null;
                var result = await w.AutoClip.ClipObjectiveEventsAsync(body.GameId, objectiveId, ct);
                log.LogInformation("Auto-clip objectives: game {GameId} created {Created} skipped {Skipped} ({Reason})",
                    body.GameId, result.Created, result.Skipped, result.Reason);
                return Results.Json(new { ok = true, created = result.Created, skipped = result.Skipped, reason = result.Reason }, jsonOptions);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Auto-clip objectives failed for game {GameId}", body.GameId);
                return Results.Json(new { ok = false, error = "Auto-clip failed (is ffmpeg installed?)." }, jsonOptions, statusCode: 422);
            }
        });

        app.MapPost("/api/clip/upload", async (ShareClipBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || body.GameId <= 0 || body.BookmarkId <= 0)
                return Results.BadRequest(new { error = "gameId and bookmarkId required" });

            // Resolve the bookmark server-side: its clip path, share URL, and clip window.
            VodBookmarkRecord? bm;
            try
            {
                var marks = await w.Vod.GetBookmarksAsync(body.GameId);
                bm = marks.FirstOrDefault(m => m.Id == body.BookmarkId);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Share: bookmark lookup failed for game {GameId} bm {BookmarkId}", body.GameId, body.BookmarkId);
                return Results.Json(new { ok = false, error = "Couldn't load that clip." }, jsonOptions, statusCode: 422);
            }
            if (bm is null)
                return Results.Json(new { ok = false, error = "Clip not found." }, jsonOptions, statusCode: 404);

            // Already shared → return the existing URL (mirror the VM: no re-upload).
            if (!string.IsNullOrWhiteSpace(bm.ShareUrl))
                return Results.Json(new { ok = true, shareUrl = bm.ShareUrl, alreadyShared = true }, jsonOptions);

            if (string.IsNullOrWhiteSpace(bm.ClipPath) || !File.Exists(bm.ClipPath))
                return Results.Json(new { ok = false, error = "Clip file not found on disk." }, jsonOptions, statusCode: 422);

            // Duration cap (mirror MaxShareDurationSeconds=90). Use the stored clip window.
            var durationSeconds = (bm.ClipEndSeconds.HasValue && bm.ClipStartSeconds.HasValue)
                ? Math.Max(0, bm.ClipEndSeconds.Value - bm.ClipStartSeconds.Value)
                : 0;
            if (durationSeconds > MaxShareDurationSeconds)
                return Results.Json(new { ok = false, error = "Clips can be up to 90s — trim and re-clip." }, jsonOptions, statusCode: 422);

            // Logged-in check (the upload also enforces this, but we want the clear-session
            // + re-prompt behavior on a missing/expired token, not a generic error).
            var cfg = await w.Config.LoadAsync();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var signedIn = !string.IsNullOrWhiteSpace(cfg.RiotSessionToken) && cfg.RiotSessionExpiresAt > now;
            if (!signedIn)
                return Results.Json(new { ok = false, error = "You need to be logged in to share clips.", needsLogin = true }, jsonOptions, statusCode: 401);

            await w.BackupGuard.EnsureBackedUpAsync();

            // champion for the watch page: prefer the body, else the game row.
            var champion = (body.ChampionName ?? "").Trim();
            if (champion.Length == 0)
            {
                var game = await w.Games.GetAsync(body.GameId);
                champion = game?.ChampionName ?? "";
            }
            var title = string.IsNullOrWhiteSpace(body.Title) ? (bm.Note ?? "") : body.Title!.Trim();

            try
            {
                var result = await w.ClipUpload.UploadAsync(
                    filePath: bm.ClipPath,
                    sessionToken: cfg.RiotSessionToken,
                    title: title,
                    champion: champion,
                    durationSeconds: durationSeconds > 0 ? durationSeconds : (int?)null,
                    progress: null,
                    ct: ct);

                await w.Vod.SetBookmarkShareUrlAsync(body.BookmarkId, result.Url);
                log.LogInformation("Clip shared: bm {BookmarkId} -> {Url}", body.BookmarkId, result.Url);
                return Results.Json(new { ok = true, shareUrl = result.Url, alreadyShared = false }, jsonOptions);
            }
            catch (ClipUploadException ex)
            {
                if (ex.Unauthorized)
                {
                    // Proxy rejected the token (401/403). Do NOT wipe the local session here:
                    // a SINGLE share 401 — which can be transient (a proxy hiccup, a 5xx that
                    // surfaced as unauthorized, brief clock skew on the expiry check) — used to
                    // blank RiotSessionToken + RiotSessionExpiresAt, destroying an otherwise
                    // valid multi-week session and locking the user out of sharing entirely
                    // (every retry then sent an empty token -> guaranteed 401 -> re-wipe loop).
                    // Tell the frontend to re-prompt login, but leave the stored session intact
                    // so a retry (or a genuine re-login) can succeed. Deliberate sign-out
                    // (POST /api/auth/logout) remains the only path that clears the session.
                    log.LogInformation("Share: proxy returned unauthorized; prompting re-login WITHOUT clearing the stored session.");
                    return Results.Json(new { ok = false, error = ex.Message, needsLogin = true }, jsonOptions, statusCode: 401);
                }
                return Results.Json(new { ok = false, error = ex.Message }, jsonOptions, statusCode: 422);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Share: upload failed for bm {BookmarkId}", body.BookmarkId);
                return Results.Json(new { ok = false, error = "Couldn't upload the clip. Try again." }, jsonOptions, statusCode: 502);
            }
        });

        // ─────────────────────────────────────────────────────────────────────────────
        // POST /api/clip/delete  { gameId, bookmarkId }  — TRULY delete a saved clip:
        // the uploaded copy (if shared), the on-disk file, and the DB rows (the clip
        // bookmark + any evidence ledger entry that referenced it). Mirrors the clip-
        // upload composition: resolve the bookmark server-side; the frontend only sends
        // ids. Order matters — remote first (needs the share_url + token before the row
        // is gone), then DB, then the local file. Remote delete is best-effort: a logged-
        // out / offline user can still purge their local copy. Backup-guarded.
        // ─────────────────────────────────────────────────────────────────────────────
        app.MapPost("/api/clip/delete", async (DeleteClipBody body, WriteServices w, ILogger<Program> log, CancellationToken ct) =>
        {
            if (body is null || body.GameId <= 0 || body.BookmarkId <= 0)
                return Results.BadRequest(new { error = "gameId and bookmarkId required" });

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
            var remoteDeleted = false;
            var slug = ExtractClipSlug(bm.ShareUrl);
            if (slug.Length > 0)
            {
                var cfg = await w.Config.LoadAsync();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var signedIn = !string.IsNullOrWhiteSpace(cfg.RiotSessionToken) && cfg.RiotSessionExpiresAt > now;
                if (signedIn)
                {
                    try
                    {
                        await w.ClipUpload.DeleteAsync(slug, cfg.RiotSessionToken, ct);
                        remoteDeleted = true;
                    }
                    catch (Exception ex)
                    {
                        log.LogDebug(ex, "Clip delete: remote delete of {Slug} failed (continuing local cleanup)", slug);
                    }
                }
            }

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

            log.LogInformation("Clip deleted: bm {BookmarkId} (file={FileDeleted}, remote={RemoteDeleted})",
                body.BookmarkId, fileDeleted, remoteDeleted);
            return Results.Json(new { ok = true, fileDeleted, remoteDeleted }, jsonOptions);
        });
    }

    private const int MaxShareDurationSeconds = 90;
    // The public slug in a revu.lol/<slug> share URL (the last non-empty path segment),
    // or "" when the URL is empty / unparseable. Used to target the remote clip delete.
    static string ExtractClipSlug(string? shareUrl)
    {
        if (string.IsNullOrWhiteSpace(shareUrl)) return "";
        var s = shareUrl.Trim().TrimEnd('/');
        var slash = s.LastIndexOf('/');
        var slug = slash >= 0 ? s[(slash + 1)..] : s;
        // Strip any query/fragment the URL might carry.
        var cut = slug.IndexOfAny(['?', '#']);
        if (cut >= 0) slug = slug[..cut];
        return slug.Trim();
    }

    // True only when the path is well-formed and ends in a known clip video extension.
    // Same guard GameRepository's cascade-delete uses: a clip is always a video container,
    // so this blocks a tampered clip_path from deleting a non-clip file.
    static bool IsDeletableClipFile(string clipPath)
    {
        if (string.IsNullOrWhiteSpace(clipPath)) return false;
        string full;
        try { full = Path.GetFullPath(clipPath); }
        catch { return false; }
        var ext = Path.GetExtension(full);
        string[] allowed = [".mp4", ".webm", ".mkv", ".mov"];
        return Array.FindIndex(allowed, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) >= 0;
    }
}
