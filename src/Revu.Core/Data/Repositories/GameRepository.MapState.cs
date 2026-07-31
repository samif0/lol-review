#nullable enable

namespace Revu.Core.Data.Repositories;

public sealed partial class GameRepository
{
    /// <summary>
    /// v3.2 (schema v11): game_ids the map-state pass hasn't processed at
    /// <paramref name="currentVersion"/> yet (NULL or older analyzer version).
    /// SCOPED TO THE REVIEW QUEUE: only UNREVIEWED games from the last
    /// <paramref name="recentDays"/> days qualify — map-state markers exist to
    /// anchor an upcoming review, and walking a whole multi-hundred-game history
    /// (two Riot calls per game) costs tens of minutes of rate-limited fetching
    /// for games nobody will open again. The unreviewed predicate mirrors
    /// <see cref="GetUnreviewedGamesAsync"/> exactly (any review field, rating,
    /// session_log note/skip, or concept tag counts as reviewed) — with ONE
    /// deliberate divergence: the v3.3 with-coach block exemption is NOT
    /// applied here. Coach-block games leave the review queue (the coach
    /// reviews them outside Revu) but still get map-state enrichment;
    /// otherwise the derived-event data set would have a systematic hole
    /// correlated with coach presence, biasing every stint comparison the
    /// feature exists to enable. Excludes hidden games and casual queues like
    /// the other Match-V5 backfills; newest first so fresh games get their
    /// map state first.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetGameIdsMissingMapStateAsync(int currentVersion, int recentDays = 14)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT game_id FROM games
            WHERE (map_state_v IS NULL OR map_state_v < @version)
              AND timestamp >= @cutoff
              AND NOT (
                    COALESCE(rating, 0) > 0
                 OR COALESCE(review_notes, '') != ''
                 OR COALESCE(mistakes, '') != ''
                 OR COALESCE(went_well, '') != ''
                 OR COALESCE(focus_next, '') != ''
                 OR COALESCE(spotted_problems, '') != ''
                 OR COALESCE(outside_control, '') != ''
                 OR COALESCE(within_control, '') != ''
                 OR COALESCE(attribution, '') != ''
                 OR COALESCE(personal_contribution, '') != ''
                 OR EXISTS (
                        SELECT 1
                        FROM session_log
                        WHERE session_log.game_id = games.game_id
                          AND (
                                COALESCE(session_log.improvement_note, '') != ''
                             OR COALESCE(session_log.mental_handled, '') != ''
                             OR COALESCE(session_log.is_skipped, 0) = 1
                          )
                    )
                 OR EXISTS (
                        SELECT 1
                        FROM game_concept_tags
                        WHERE game_concept_tags.game_id = games.game_id
                    )
              )
              {CasualFilter}
              AND (is_hidden IS NULL OR is_hidden = 0)
            ORDER BY timestamp DESC";
        cmd.Parameters.AddWithValue("@version", currentVersion);
        cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.AddDays(-recentDays).ToUnixTimeSeconds());

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    /// <summary>v3.2 (schema v11): mark a game processed by the map-state pass.</summary>
    public async Task UpdateMapStateVersionAsync(long gameId, int version)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET map_state_v = @version WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
    }
}
