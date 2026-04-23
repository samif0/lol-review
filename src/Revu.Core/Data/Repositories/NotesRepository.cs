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
        cmd.CommandText = """
            UPDATE persistent_notes SET content = @content, updated_at = @updatedAt
            WHERE id = (SELECT MIN(id) FROM persistent_notes)
            """;
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }
}
