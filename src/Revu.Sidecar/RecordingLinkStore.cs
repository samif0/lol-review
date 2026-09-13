using Revu.Core.Data;

namespace Revu.Sidecar;

/// <summary>Write-graph-only insert. Automatic recording never replaces a user's VOD.</summary>
public sealed class RecordingLinkStore(IDbConnectionFactory factory)
{
    public async Task<string> LinkAsync(RecordingRegistration request, Func<Task>? beforeLink = null)
    {
        using var conn = factory.CreateConnection();
        using var transaction = conn.BeginTransaction();
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@game", request.GameId);
        command.Parameters.AddWithValue("@path", request.FilePath);
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
            if (!path.Equals(request.FilePath, StringComparison.OrdinalIgnoreCase)) return "retained-existing";
            if (beforeLink is not null) await beforeLink();
            return "linked";
        }

        // Publish timing before the SQL row becomes visible; a crash between them
        // leaves a pending receipt which retries this exact match/file on startup.
        if (beforeLink is not null) await beforeLink();

        command.CommandText = """
            INSERT INTO vod_files (game_id, file_path, file_size, duration_s, matched_at)
            VALUES (@game, @path, @size, @duration, @matched)
            """;
        command.Parameters.AddWithValue("@size", request.FileSize);
        command.Parameters.AddWithValue("@duration", (long)Math.Floor(request.DurationSeconds));
        command.Parameters.AddWithValue("@matched", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync();
        transaction.Commit();
        return "linked";
    }
}

public sealed class RecordingConflictException(string message) : Exception(message);
