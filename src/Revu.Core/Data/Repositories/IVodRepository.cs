#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD for vod_files and vod_bookmarks tables.</summary>
public interface IVodRepository
{
    // ── VOD file linking ─────────────────────────────────────────

    Task LinkVodAsync(long gameId, string filePath, long fileSize = 0, long durationSeconds = 0);

    Task<VodSummary?> GetVodAsync(long gameId);

    Task<Dictionary<long, string>> GetVodPathsAsync(IReadOnlyCollection<long> gameIds);

    Task UnlinkVodAsync(long gameId);

    Task<IReadOnlyList<VodSummary>> GetAllVodsAsync();

    // ── Bookmarks ────────────────────────────────────────────────

    Task<long> AddBookmarkAsync(long gameId, int gameTimeSeconds, string note = "",
        IReadOnlyList<string>? tags = null, int? clipStartSeconds = null,
        int? clipEndSeconds = null, string clipPath = "", long? objectiveId = null,
        string quality = "", long? promptId = null);

    Task UpdateBookmarkAsync(long bookmarkId, string? note = null,
        IReadOnlyList<string>? tags = null, int? gameTimeSeconds = null,
        int? clipStartSeconds = null, int? clipEndSeconds = null,
        string? clipPath = null, string? quality = null);

    /// <summary>
    /// Update the objective attached to a bookmark. Pass null to detach.
    /// Separate from <see cref="UpdateBookmarkAsync"/> because long? as a
    /// parameter can't distinguish "don't change" from "set to null" —
    /// and both are valid user intents.
    /// </summary>
    Task SetBookmarkObjectiveAsync(long bookmarkId, long? objectiveId);

    /// <summary>
    /// v2.15.7: set both objective + prompt tags atomically. <paramref name="promptId"/>
    /// is optional; when set, <paramref name="objectiveId"/> should be the prompt's parent
    /// objective so non-prompt-aware queries still see the objective association.
    /// Pass (null, null) to detach entirely.
    /// </summary>
    Task SetBookmarkTagAsync(long bookmarkId, long? objectiveId, long? promptId);

    Task DeleteBookmarkAsync(long bookmarkId);

    Task<IReadOnlyList<VodBookmarkRecord>> GetBookmarksAsync(long gameId);

    Task<IReadOnlyList<VodBookmarkRecord>> GetBookmarksForObjectiveAsync(long objectiveId);

    Task<int> GetBookmarkCountAsync(long gameId);

    Task DeleteAllBookmarksAsync(long gameId);
}
