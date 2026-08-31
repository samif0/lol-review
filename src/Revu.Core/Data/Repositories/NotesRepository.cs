#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD for the persistent_notes table (single-row persistent notes).</summary>
public sealed class NotesRepository : INotesRepository
{
    private readonly IDbConnectionFactory _factory;

    public NotesRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<string> GetAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM persistent_notes ORDER BY id LIMIT 1";

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync() && !reader.IsDBNull(0))
        {
            return reader.GetString(0);
        }
        return "";
    }

    public async Task SaveAsync(string content)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // UPDATE the single seed row when it exists; INSERT it when it doesn't.
        // The UPDATE-only version silently dropped every save on a database
        // whose seed row was never created (fresh sidecar-era installs).
        cmd.CommandText = """
            UPDATE persistent_notes SET content = @content, updated_at = @updatedAt
            WHERE id = (SELECT MIN(id) FROM persistent_notes)
            """;
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var updated = await cmd.ExecuteNonQueryAsync();
        if (updated > 0)
        {
            return;
        }

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = "INSERT INTO persistent_notes (content, updated_at) VALUES (@content, @updatedAt)";
        insertCmd.Parameters.AddWithValue("@content", content);
        insertCmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await insertCmd.ExecuteNonQueryAsync();
    }
}
