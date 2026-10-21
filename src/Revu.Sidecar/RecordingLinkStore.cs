using Revu.Core.Data;

namespace Revu.Sidecar;

/// <summary>Write-graph-only link. Automatic recording never replaces a user's or an
/// imported VOD; the caller may only let one of Revu's own partial links give way.</summary>
public sealed class RecordingLinkStore(IDbConnectionFactory factory)
{
    public async Task<string> LinkAsync(RecordingRegistration request, Func<Task>? beforeLink = null,
        Func<string, bool>? canReplace = null)
    {
        using var conn = factory.CreateConnection();
        using var transaction = conn.BeginTransaction();
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@game", request.GameId);
        command.Parameters.AddWithValue("@path", request.FilePath);
        command.Parameters.AddWithValue("@size", request.FileSize);
        command.Parameters.AddWithValue("@duration", (long)Math.Floor(request.DurationSeconds));
        command.Parameters.AddWithValue("@matched", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.CommandText = "SELECT COUNT(*) FROM games WHERE game_id = @game";
        if (Convert.ToInt64(await command.ExecuteScalarAsync()) == 0) return "pending-match";

        command.CommandText = "SELECT game_id FROM vod_files WHERE file_path = @path COLLATE NOCASE LIMIT 1";
        var owner = await command.ExecuteScalarAsync();
        if (owner is not null and not DBNull && Convert.ToInt64(owner) != request.GameId)
            throw new RecordingConflictException("This recording is already attached to another match.");

        command.CommandText = "SELECT file_path FROM vod_files WHERE game_id = @game";
        var existing = await command.ExecuteScalarAsync();
        if (existing is string path)
        {
            if (path.Equals(request.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                if (beforeLink is not null) await beforeLink();
                return "linked";
            }
            if (canReplace?.Invoke(path) != true) return "retained-existing";
            if (beforeLink is not null) await beforeLink();
            // Only the exact partial the caller approved gives way; anything else keeps its video.
            command.CommandText = """
                UPDATE vod_files SET file_path = @path, file_size = @size, duration_s = @duration, matched_at = @matched
                WHERE game_id = @game AND file_path = @existing COLLATE NOCASE
                """;
            command.Parameters.AddWithValue("@existing", path);
            if (await command.ExecuteNonQueryAsync() != 1) return "retained-existing";
            transaction.Commit();
            return "linked";
        }

        // Publish timing before the SQL row becomes visible; a crash between them
        // leaves a pending receipt which retries this exact match/file on startup.
        if (beforeLink is not null) await beforeLink();

        command.CommandText = """
            INSERT INTO vod_files (game_id, file_path, file_size, duration_s, matched_at)
            VALUES (@game, @path, @size, @duration, @matched)
            """;
        await command.ExecuteNonQueryAsync();
        transaction.Commit();
        return "linked";
    }

    /// <summary>Informational size only, for the exact match/file row this recording linked.
    /// A row the user has since replaced is left untouched.</summary>
    public async Task<int> UpdateFileSizeAsync(RecordingRegistration request)
    {
        using var conn = factory.CreateConnection();
        using var transaction = conn.BeginTransaction();
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE vod_files SET file_size = @size WHERE game_id = @game AND file_path = @path COLLATE NOCASE";
        command.Parameters.AddWithValue("@size", request.FileSize);
        command.Parameters.AddWithValue("@game", request.GameId);
        command.Parameters.AddWithValue("@path", request.FilePath);
        var changed = await command.ExecuteNonQueryAsync();
        transaction.Commit();
        return changed;
    }

    /// <summary>Removes the VOD links to files inside <paramref name="directory"/> that no
    /// longer exist (a storage-limit prune deleted them). Returns the unlinked games.</summary>
    public async Task<IReadOnlyList<long>> UnlinkMissingUnderAsync(string directory)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        using var conn = factory.CreateConnection();
        using var transaction = conn.BeginTransaction();
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT game_id, file_path FROM vod_files";
        var gone = new List<(long GameId, string Path)>();
        using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var path = reader.GetString(1);
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
                    gone.Add((reader.GetInt64(0), path));
            }
        command.CommandText = "DELETE FROM vod_files WHERE game_id = @game AND file_path = @path COLLATE NOCASE";
        var game = command.Parameters.Add("@game", Microsoft.Data.Sqlite.SqliteType.Integer);
        var file = command.Parameters.Add("@path", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var (gameId, path) in gone)
        {
            game.Value = gameId; file.Value = path;
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
        return gone.Select(g => g.GameId).Distinct().ToList();
    }
}

public sealed class RecordingConflictException(string message) : Exception(message);
