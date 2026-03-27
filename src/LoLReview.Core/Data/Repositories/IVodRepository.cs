#nullable enable

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD for vod_files and vod_bookmarks tables.</summary>
public interface IVodRepository
{
    // ── VOD file linking ─────────────────────────────────────────

    Task LinkVodAsync(long gameId, string filePath, long fileSize = 0, long durationSeconds = 0);

    Task<Dictionary<string, object?>?> GetVodAsync(long gameId);

    Task UnlinkVodAsync(long gameId);

    Task<IReadOnlyList<Dictionary<string, object?>>> GetAllVodsAsync();

    // ── Bookmarks ────────────────────────────────────────────────

    Task<long> AddBookmarkAsync(long gameId, int gameTimeSeconds, string note = "",
        IReadOnlyList<string>? tags = null, int? clipStartSeconds = null,
        int? clipEndSeconds = null, string clipPath = "");

    Task UpdateBookmarkAsync(long bookmarkId, string? note = null,
        IReadOnlyList<string>? tags = null, int? gameTimeSeconds = null,
        int? clipStartSeconds = null, int? clipEndSeconds = null,
        string? clipPath = null);

    Task DeleteBookmarkAsync(long bookmarkId);

    Task<IReadOnlyList<Dictionary<string, object?>>> GetBookmarksAsync(long gameId);

    Task<int> GetBookmarkCountAsync(long gameId);

    Task DeleteAllBookmarksAsync(long gameId);
}
