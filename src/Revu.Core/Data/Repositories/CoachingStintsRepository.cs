#nullable enable

using Microsoft.Data.Sqlite;
using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// CRUD for the coaching_stints table (v3.3, schema v12) plus the
/// per-stint block counters read from sessions.
/// </summary>
public sealed class CoachingStintsRepository : ICoachingStintsRepository
{
    private readonly IDbConnectionFactory _factory;

    public CoachingStintsRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<int> StartStintAsync(string name, string startDate, string plannedEndDate = "")
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO coaching_stints (name, start_date, planned_end_date, created_at)
            VALUES (@name, @startDate, @plannedEndDate, @createdAt);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@startDate", startDate);
        cmd.Parameters.AddWithValue("@plannedEndDate", plannedEndDate);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var id = await cmd.ExecuteScalarAsync();
        return id is long l ? (int)l : 0;
    }

    public async Task EndStintAsync(int stintId)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE coaching_stints
            SET ended_at = @endedAt
            WHERE id = @id AND ended_at IS NULL";
        cmd.Parameters.AddWithValue("@endedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@id", stintId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<CoachingStint?> GetActiveStintAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, start_date, planned_end_date, created_at, ended_at
            FROM coaching_stints
            WHERE ended_at IS NULL
            ORDER BY id DESC
            LIMIT 1";

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapStint(reader) : null;
    }

    public async Task<int> GetNextBlockNumberAsync(int stintId)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(MAX(stint_block_number), 0) + 1
            FROM sessions
            WHERE stint_id = @stintId";
        cmd.Parameters.AddWithValue("@stintId", stintId);

        var next = await cmd.ExecuteScalarAsync();
        return next is long l ? (int)l : 1;
    }

    public async Task<CoachingStintBlockCounts> GetBlockCountsAsync(int stintId)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                COUNT(*) AS total,
                COALESCE(SUM(CASE WHEN COALESCE(with_coach, 0) = 1 THEN 1 ELSE 0 END), 0) AS with_coach
            FROM sessions
            WHERE stint_id = @stintId";
        cmd.Parameters.AddWithValue("@stintId", stintId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var withCoach = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            return new CoachingStintBlockCounts(total, withCoach, total - withCoach);
        }

        return new CoachingStintBlockCounts(0, 0, 0);
    }

    private static CoachingStint MapStint(SqliteDataReader reader)
    {
        return new CoachingStint
        {
            Id = reader.GetInt32(0),
            Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
            StartDate = reader.IsDBNull(2) ? "" : reader.GetString(2),
            PlannedEndDate = reader.IsDBNull(3) ? "" : reader.GetString(3),
            CreatedAt = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            EndedAt = reader.IsDBNull(5) ? null : reader.GetInt64(5),
        };
    }
}
