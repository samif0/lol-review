#nullable enable

using Revu.Core.Data.Repositories;

namespace Revu.Sidecar;

/// <summary>Edit a saved moment without extracting or promoting a video clip.</summary>
public static class PatternBookmarkNotes
{
    public static async Task<bool> SaveAsync(
        IVodRepository vod, IEvidenceRepository evidence,
        long gameId, long bookmarkId, string note)
    {
        var bookmark = (await vod.GetBookmarksAsync(gameId)).FirstOrDefault(row => row.Id == bookmarkId);
        if (bookmark is null) return false;

        await vod.UpdateBookmarkAsync(bookmarkId, note: note);
        // A clip's bookmark and ledger entry represent one saved moment. Keep
        // both notes current so the VOD, review and cross-game views agree.
        var linked = (await evidence.GetForGameAsync(gameId, includeDismissed: true))
            .Where(row => row.SourceKind == EvidenceKinds.Clip && row.SourceId == bookmarkId);
        foreach (var row in linked)
            await evidence.UpdateNoteAsync(row.Id, note);
        return true;
    }
}
