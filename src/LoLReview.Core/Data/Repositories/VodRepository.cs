#nullable enable

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD for vod_files and vod_bookmarks tables.</summary>
public sealed class VodRepository : IVodRepository
{
    private readonly IDbConnectionFactory _factory;

    public VodRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task LinkVodAsync(long gameId, string filePath, long fileSize = 0, long durationSeconds = 0)
    {
        using var conn = _factory.CreateConnection();

        // Refuse to link a file already owned by a different game.
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT game_id FROM vod_files WHERE file_path = @filePath AND game_id != @gameId LIMIT 1";
            checkCmd.Parameters.AddWithValue("@filePath", filePath);
            checkCmd.Parameters.AddWithValue("@gameId", gameId);
            var existingOwner = await checkCmd.ExecuteScalarAsync();
            if (existingOwner is not null and not DBNull)
            {
                throw new InvalidOperationException(
                    $"VOD file '{filePath}' is already linked to game {existingOwner}; refusing to also link to game {gameId}");
            }
        }

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

    public async Task<VodSummary?> GetVodAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vod_files WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadSingleVodAsync(cmd);
    }

    public async Task<Dictionary<long, string>> GetVodPathsAsync(IReadOnlyCollection<long> gameIds)
    {
        if (gameIds.Count == 0)
        {
            return [];
        }

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();

        var placeholders = new List<string>(gameIds.Count);
        var index = 0;
        foreach (var gameId in gameIds)
        {
            var parameterName = $"@gameId{index++}";
            placeholders.Add(parameterName);
            cmd.Parameters.AddWithValue(parameterName, gameId);
        }

        cmd.CommandText = $"""
            SELECT game_id, file_path
            FROM vod_files
            WHERE game_id IN ({string.Join(", ", placeholders)})
            """;

        var paths = new Dictionary<long, string>(gameIds.Count);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(1))
            {
                continue;
            }

            paths[reader.GetInt64(0)] = reader.GetString(1);
        }

        return paths;
    }

    public async Task UnlinkVodAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vod_files WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<VodSummary>> GetAllVodsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vod_files ORDER BY matched_at DESC";
        return await ReadAllVodsAsync(cmd);
    }

    public async Task<long> AddBookmarkAsync(long gameId, int gameTimeSeconds, string note = "",
        IReadOnlyList<string>? tags = null, int? clipStartSeconds = null,
        int? clipEndSeconds = null, string clipPath = "", long? objectiveId = null,
        string quality = "")
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vod_bookmarks
                (game_id, game_time_s, note, tags, clip_start_s, clip_end_s, clip_path, objective_id, quality, created_at)
            VALUES (@gameId, @gameTimeS, @note, @tags, @clipStartS, @clipEndS, @clipPath, @objectiveId, @quality, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@gameTimeS", gameTimeSeconds);
        cmd.Parameters.AddWithValue("@note", note);
        cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(tags ?? (IReadOnlyList<string>)[]));
        cmd.Parameters.AddWithValue("@clipStartS", clipStartSeconds.HasValue ? clipStartSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@clipEndS", clipEndSeconds.HasValue ? clipEndSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@clipPath", clipPath);
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId.HasValue ? objectiveId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@quality", quality);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task UpdateBookmarkAsync(long bookmarkId, string? note = null,
        IReadOnlyList<string>? tags = null, int? gameTimeSeconds = null,
        int? clipStartSeconds = null, int? clipEndSeconds = null,
        string? clipPath = null, string? quality = null)
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

        if (quality is not null)
        {
            updates.Add("quality = @quality");
            parameters.Add(new SqliteParameter("@quality", quality));
        }

        if (updates.Count == 0)
        {
            return;
        }

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE vod_bookmarks SET {string.Join(", ", updates)} WHERE id = @id";
        foreach (var parameter in parameters)
        {
            cmd.Parameters.Add(parameter);
        }

        cmd.Parameters.AddWithValue("@id", bookmarkId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteBookmarkAsync(long bookmarkId)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        await DeleteCoachRowsForBookmarkAsync(conn, tx, bookmarkId);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM vod_bookmarks WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", bookmarkId);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<VodBookmarkRecord>> GetBookmarksAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM vod_bookmarks WHERE game_id = @gameId ORDER BY game_time_s ASC";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadBookmarksAsync(cmd);
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
        using var tx = conn.BeginTransaction();

        await DeleteCoachRowsForGameAsync(conn, tx, gameId);

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM vod_bookmarks WHERE game_id = @gameId";
            cmd.Parameters.AddWithValue("@gameId", gameId);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private static async Task DeleteCoachRowsForBookmarkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long bookmarkId)
    {
        await DeleteCoachLabelsAsync(
            connection,
            transaction,
            """
            DELETE FROM coach_labels
            WHERE moment_id IN (
                SELECT id
                FROM coach_moments
                WHERE bookmark_id = @bookmarkId
            )
            """,
            ("@bookmarkId", bookmarkId));

        await DeleteCoachInferencesAsync(
            connection,
            transaction,
            """
            DELETE FROM coach_inferences
            WHERE moment_id IN (
                SELECT id
                FROM coach_moments
                WHERE bookmark_id = @bookmarkId
            )
            """,
            ("@bookmarkId", bookmarkId));

        using var deleteMomentsCommand = connection.CreateCommand();
        deleteMomentsCommand.Transaction = transaction;
        deleteMomentsCommand.CommandText = """
            DELETE FROM coach_moments
            WHERE bookmark_id = @bookmarkId
            """;
        deleteMomentsCommand.Parameters.AddWithValue("@bookmarkId", bookmarkId);
        await deleteMomentsCommand.ExecuteNonQueryAsync();
    }

    private static async Task DeleteCoachRowsForGameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long gameId)
    {
        await DeleteCoachLabelsAsync(
            connection,
            transaction,
            """
            DELETE FROM coach_labels
            WHERE moment_id IN (
                SELECT id
                FROM coach_moments
                WHERE bookmark_id IN (
                    SELECT id
                    FROM vod_bookmarks
                    WHERE game_id = @gameId
                )
            )
            """,
            ("@gameId", gameId));

        await DeleteCoachInferencesAsync(
            connection,
            transaction,
            """
            DELETE FROM coach_inferences
            WHERE moment_id IN (
                SELECT id
                FROM coach_moments
                WHERE bookmark_id IN (
                    SELECT id
                    FROM vod_bookmarks
                    WHERE game_id = @gameId
                )
            )
            """,
            ("@gameId", gameId));

        using var deleteMomentsCommand = connection.CreateCommand();
        deleteMomentsCommand.Transaction = transaction;
        deleteMomentsCommand.CommandText = """
            DELETE FROM coach_moments
            WHERE bookmark_id IN (
                SELECT id
                FROM vod_bookmarks
                WHERE game_id = @gameId
            )
            """;
        deleteMomentsCommand.Parameters.AddWithValue("@gameId", gameId);
        await deleteMomentsCommand.ExecuteNonQueryAsync();
    }

    private static async Task DeleteCoachLabelsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteCoachInferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<VodSummary>> ReadAllVodsAsync(SqliteCommand cmd)
    {
        var results = new List<VodSummary>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadVod(reader));
        }

        return results;
    }

    private static async Task<VodSummary?> ReadSingleVodAsync(SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadVod(reader) : null;
    }

    private static async Task<IReadOnlyList<VodBookmarkRecord>> ReadBookmarksAsync(SqliteCommand cmd)
    {
        var results = new List<VodBookmarkRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadBookmark(reader));
        }

        return results;
    }

    private static VodSummary ReadVod(SqliteDataReader reader)
    {
        return new VodSummary(
            Id: reader.IsDBNull(reader.GetOrdinal("id")) ? 0 : reader.GetInt64(reader.GetOrdinal("id")),
            GameId: reader.IsDBNull(reader.GetOrdinal("game_id")) ? 0 : reader.GetInt64(reader.GetOrdinal("game_id")),
            FilePath: reader.IsDBNull(reader.GetOrdinal("file_path")) ? "" : reader.GetString(reader.GetOrdinal("file_path")),
            FileSize: reader.IsDBNull(reader.GetOrdinal("file_size")) ? 0 : reader.GetInt64(reader.GetOrdinal("file_size")),
            DurationSeconds: reader.IsDBNull(reader.GetOrdinal("duration_s")) ? 0 : reader.GetInt32(reader.GetOrdinal("duration_s")),
            MatchedAt: reader.IsDBNull(reader.GetOrdinal("matched_at")) ? null : reader.GetInt64(reader.GetOrdinal("matched_at")));
    }

    private static VodBookmarkRecord ReadBookmark(SqliteDataReader reader)
    {
        return new VodBookmarkRecord(
            Id: reader.IsDBNull(reader.GetOrdinal("id")) ? 0 : reader.GetInt64(reader.GetOrdinal("id")),
            GameId: reader.IsDBNull(reader.GetOrdinal("game_id")) ? 0 : reader.GetInt64(reader.GetOrdinal("game_id")),
            GameTimeSeconds: reader.IsDBNull(reader.GetOrdinal("game_time_s")) ? 0 : reader.GetInt32(reader.GetOrdinal("game_time_s")),
            Note: reader.IsDBNull(reader.GetOrdinal("note")) ? "" : reader.GetString(reader.GetOrdinal("note")),
            TagsJson: reader.IsDBNull(reader.GetOrdinal("tags")) ? "[]" : reader.GetString(reader.GetOrdinal("tags")),
            ClipStartSeconds: reader.IsDBNull(reader.GetOrdinal("clip_start_s")) ? null : reader.GetInt32(reader.GetOrdinal("clip_start_s")),
            ClipEndSeconds: reader.IsDBNull(reader.GetOrdinal("clip_end_s")) ? null : reader.GetInt32(reader.GetOrdinal("clip_end_s")),
            ClipPath: reader.IsDBNull(reader.GetOrdinal("clip_path")) ? "" : reader.GetString(reader.GetOrdinal("clip_path")),
            Quality: reader.IsDBNull(reader.GetOrdinal("quality")) ? "" : reader.GetString(reader.GetOrdinal("quality")),
            CreatedAt: reader.IsDBNull(reader.GetOrdinal("created_at")) ? null : reader.GetInt64(reader.GetOrdinal("created_at")));
    }
}
