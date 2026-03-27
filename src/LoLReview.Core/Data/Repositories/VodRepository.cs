#nullable enable

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD for vod_files and vod_bookmarks tables.</summary>
public sealed class VodRepository : IVodRepository
{
    private readonly IDbConnectionFactory _factory;

    public VodRepository(IDbConnectionFactory factory) => _factory = factory;

    // ── VOD file linking ─────────────────────────────────────────

    public async Task LinkVodAsync(long gameId, string filePath, long fileSize = 0, long durationSeconds = 0)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO vod_files
                (game_id, file_path, file_size, duration_s, matched_at)
            VALUES (@gameId, @filePath, @fileSize, @durationS, @matchedAt)
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@filePath", filePath);
        cmd.Parameters.AddWithValue("@fileSize", fileSize);
        cmd.Parameters.AddWithValue("@durationS", durationSeconds);
        cmd.Parameters.AddWithValue("@matchedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, object?>?> GetVodAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vod_files WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadSingleRowAsync(cmd);
    }

    public async Task UnlinkVodAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vod_files WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAllVodsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vod_files ORDER BY matched_at DESC";
        return await ReadAllRowsAsync(cmd);
    }

    // ── Bookmarks ────────────────────────────────────────────────

    public async Task<long> AddBookmarkAsync(long gameId, int gameTimeSeconds, string note = "",
        IReadOnlyList<string>? tags = null, int? clipStartSeconds = null,
        int? clipEndSeconds = null, string clipPath = "")
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vod_bookmarks
                (game_id, game_time_s, note, tags, clip_start_s, clip_end_s, clip_path, created_at)
            VALUES (@gameId, @gameTimeS, @note, @tags, @clipStartS, @clipEndS, @clipPath, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@gameTimeS", gameTimeSeconds);
        cmd.Parameters.AddWithValue("@note", note);
        cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(tags ?? (IReadOnlyList<string>)[]));
        cmd.Parameters.AddWithValue("@clipStartS", clipStartSeconds.HasValue ? clipStartSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@clipEndS", clipEndSeconds.HasValue ? clipEndSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@clipPath", clipPath);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task UpdateBookmarkAsync(long bookmarkId, string? note = null,
        IReadOnlyList<string>? tags = null, int? gameTimeSeconds = null,
        int? clipStartSeconds = null, int? clipEndSeconds = null,
        string? clipPath = null)
    {
        var updates = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (note is not null)
        {
            updates.Add("note = @note");
            parameters.Add(new SqliteParameter("@note", note));
        }
        if (tags is not null)
        {
            updates.Add("tags = @tags");
            parameters.Add(new SqliteParameter("@tags", JsonSerializer.Serialize(tags)));
        }
        if (gameTimeSeconds is not null)
        {
            updates.Add("game_time_s = @gameTimeS");
            parameters.Add(new SqliteParameter("@gameTimeS", gameTimeSeconds.Value));
        }
        if (clipStartSeconds is not null)
        {
            updates.Add("clip_start_s = @clipStartS");
            parameters.Add(new SqliteParameter("@clipStartS", clipStartSeconds.Value));
        }
        if (clipEndSeconds is not null)
        {
            updates.Add("clip_end_s = @clipEndS");
            parameters.Add(new SqliteParameter("@clipEndS", clipEndSeconds.Value));
        }
        if (clipPath is not null)
        {
            updates.Add("clip_path = @clipPath");
            parameters.Add(new SqliteParameter("@clipPath", clipPath));
        }

        if (updates.Count == 0)
            return;

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE vod_bookmarks SET {string.Join(", ", updates)} WHERE id = @id";
        foreach (var p in parameters)
            cmd.Parameters.Add(p);
        cmd.Parameters.AddWithValue("@id", bookmarkId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteBookmarkAsync(long bookmarkId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vod_bookmarks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", bookmarkId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetBookmarksAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vod_bookmarks WHERE game_id = @gameId ORDER BY game_time_s ASC";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<int> GetBookmarkCountAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM vod_bookmarks WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task DeleteAllBookmarksAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vod_bookmarks WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadAllRowsAsync(SqliteCommand cmd)
    {
        var results = new List<Dictionary<string, object?>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadRow(reader));
        }
        return results;
    }

    private static async Task<Dictionary<string, object?>?> ReadSingleRowAsync(SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadRow(reader) : null;
    }

    private static Dictionary<string, object?> ReadRow(SqliteDataReader reader)
    {
        var dict = new Dictionary<string, object?>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return dict;
    }
}
