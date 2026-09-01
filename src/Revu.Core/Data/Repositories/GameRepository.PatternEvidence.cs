#nullable enable

using Revu.Core.Constants;

namespace Revu.Core.Data.Repositories;

public sealed partial class GameRepository
{
    /// <summary>
    /// v3.5 (schema v13): game_ids the pattern-evidence materializer hasn't
    /// processed at <paramref name="currentVersion"/> yet (NULL or older),
    /// scoped to the SAME visible ranked/manual window the pattern detectors
    /// read — materializing outside the window would write rows no query can
    /// surface. Unlike the map-state sweep this is local-only (no Riot calls),
    /// so no unreviewed-games scoping is needed. Newest first so a user who
    /// opens Patterns right after upgrade sees their freshest games first.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetPatternEvidenceBackfillIdsAsync(
        int currentVersion, int windowDays = PatternConstants.WindowDays)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT game_id FROM games
            WHERE (pattern_evidence_v IS NULL OR pattern_evidence_v < @version)
              AND COALESCE(timestamp, 0) >= @cutoff
              {CasualFilter}
            ORDER BY timestamp DESC";
        cmd.Parameters.AddWithValue("@version", currentVersion);
        cmd.Parameters.AddWithValue(
            "@cutoff",
            DateTimeOffset.UtcNow.AddDays(-Math.Max(1, windowDays)).ToUnixTimeSeconds());

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    /// <summary>v3.5 (schema v13): mark a game processed by the pattern-evidence pass.</summary>
    public async Task UpdatePatternEvidenceVersionAsync(long gameId, int version)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET pattern_evidence_v = @version WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
    }
}
