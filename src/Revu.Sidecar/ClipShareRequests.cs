#nullable enable

using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// POST /api/clip/upload (C6 6.2): validate and queue a background share, returning at
/// once. Independent of ASP.NET so it can be tested.
/// </summary>
internal static class ClipShareRequests
{
    public static async Task<ApiReply> UploadAsync(ShareClipBody? body, IVodRepository vod,
        IClipNarrationRepository narrations, IGameRepository games, IConfigService config,
        ClipShareWorker share, ILogger log)
    {
        if (body is null || body.GameId <= 0 || body.BookmarkId <= 0)
            return ApiReply.Error(400, "gameId and bookmarkId required");

        VodBookmarkRecord? bm;
        try
        {
            bm = (await vod.GetBookmarksAsync(body.GameId).ConfigureAwait(false)).FirstOrDefault(m => m.Id == body.BookmarkId);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Share: bookmark lookup failed for game {GameId} bm {BookmarkId}", body.GameId, body.BookmarkId);
            return ApiReply.Error(422, "Couldn't load that clip.");
        }
        if (bm is null) return ApiReply.Error(404, "Clip not found.");

        var narration = await narrations.GetAsync(bm.Id).ConfigureAwait(false);
        var narratedOnDisk = NarrationDtos.FileOnDisk(narration?.NarratedClipPath);

        // Already shared: return the existing link (no re-upload).
        if (!string.IsNullOrWhiteSpace(bm.ShareUrl))
            return ApiReply.Ok(new { ok = true, alreadyShared = true, url = bm.ShareUrl, narrated = narratedOnDisk.Length > 0, transcriptAttached = false });

        var invalid = NarrationRequestValidator.ShareFileError(bm.ClipStartSeconds, bm.ClipEndSeconds,
            narratedOnDisk, bm.ClipPath, out var file);
        if (invalid is not null)
            return new ApiReply(422, new { ok = false, error = invalid, retryable = false });

        if (!(await ClipShareLinks.SessionAsync(config).ConfigureAwait(false)).SignedIn)
            return ApiReply.Ok(new { ok = false, needsLogin = true, error = "You need to be logged in to share clips." });

        // Champion for the watch page: prefer the body, else the game row.
        var champion = (body.ChampionName ?? "").Trim();
        if (champion.Length == 0)
        {
            try { champion = (await games.GetAsync(body.GameId).ConfigureAwait(false))?.ChampionName ?? ""; }
            catch (Exception ex) { log.LogDebug(ex, "Share: champion lookup failed for game {GameId}", body.GameId); }
        }
        var title = string.IsNullOrWhiteSpace(body.Title) ? (bm.Note ?? "") : body.Title!.Trim();
        var narrated = string.Equals(file, narratedOnDisk, StringComparison.OrdinalIgnoreCase) && narratedOnDisk.Length > 0;

        var jobId = share.Enqueue(body.GameId, bm.Id, champion, title, narrated);
        log.LogInformation("Share queued: bm {BookmarkId} (narrated={Narrated})", bm.Id, narrated);
        return ApiReply.Ok(new { ok = true, accepted = true, jobId, narrated });
    }
}
