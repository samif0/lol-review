#nullable enable

using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

/// <summary>Save VOD bookmarks and credit any objective the user attaches.</summary>
public static class BookmarkPersistence
{
    public static async Task<long> AddAsync(
        IVodRepository vod,
        IObjectivesRepository objectives,
        long gameId,
        int gameTimeSeconds,
        string note = "",
        int? clipStartSeconds = null,
        int? clipEndSeconds = null,
        string clipPath = "",
        long? objectiveId = null,
        string quality = "",
        long? promptId = null,
        IReviewDraftRepository? reviewDrafts = null)
    {
        var bookmarkId = await vod.AddBookmarkAsync(
            gameId, gameTimeSeconds, note,
            clipStartSeconds: clipStartSeconds, clipEndSeconds: clipEndSeconds,
            clipPath: clipPath, objectiveId: objectiveId, quality: quality, promptId: promptId);

        if (objectiveId is > 0)
            await ObjectivePracticePersistence.MarkPracticedAsync(objectives, gameId, objectiveId.Value, reviewDrafts);

        return bookmarkId;
    }

    public static async Task SetTagAsync(
        IVodRepository vod,
        IObjectivesRepository objectives,
        long bookmarkId,
        long? objectiveId,
        long? promptId = null,
        IReviewDraftRepository? reviewDrafts = null)
    {
        await vod.SetBookmarkTagAsync(bookmarkId, objectiveId, promptId);
        if (objectiveId is not > 0)
            return;

        // Resolve the owning game from the saved bookmark, never from client input.
        var bookmarks = await vod.GetBookmarksForObjectiveAsync(objectiveId.Value);
        var bookmark = bookmarks.FirstOrDefault(row => row.Id == bookmarkId && row.ObjectiveId == objectiveId);
        if (bookmark is not null)
            await ObjectivePracticePersistence.MarkPracticedAsync(objectives, bookmark.GameId, objectiveId.Value, reviewDrafts);
    }
}
