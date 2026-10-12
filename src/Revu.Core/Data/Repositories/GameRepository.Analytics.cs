#nullable enable

namespace Revu.Core.Data.Repositories;

public sealed partial class GameRepository
{
    // LENIENT account scoping (schema v9 games.puuid). When a current puuid is
    // supplied, aggregate queries keep the player's own rows PLUS all legacy
    // rows (puuid NULL or '') and exclude only genuinely-foreign accounts. When
    // currentPuuid is null/empty (not logged in, tests, un-wired callers) the
    // clause is a no-op via the leading "@currentPuuid = ''" short-circuit, so
    // every row is returned and behavior is identical to pre-scoping. This must
    // NEVER hide legacy rows (puuid='').
    private const string PuuidFilter =
        "AND (@currentPuuid = '' OR puuid = @currentPuuid OR puuid IS NULL OR puuid = '')";

    public async Task<ReviewFocus?> GetLastReviewFocusAsync(string? currentPuuid = null)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT focus_next, mistakes, went_well, champion_name, win, timestamp
            FROM games
            WHERE (focus_next != '' OR mistakes != '')
                {CasualFilter} {PuuidFilter}
            ORDER BY timestamp DESC
            LIMIT 1";
        cmd.Parameters.AddWithValue("@currentPuuid", currentPuuid ?? "");

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new ReviewFocus(
                FocusNext: reader.IsDBNull(reader.GetOrdinal("focus_next")) ? "" : reader.GetString(reader.GetOrdinal("focus_next")),
                Mistakes: reader.IsDBNull(reader.GetOrdinal("mistakes")) ? "" : reader.GetString(reader.GetOrdinal("mistakes")),
                WentWell: reader.IsDBNull(reader.GetOrdinal("went_well")) ? "" : reader.GetString(reader.GetOrdinal("went_well")),
                ChampionName: reader.IsDBNull(reader.GetOrdinal("champion_name")) ? "" : reader.GetString(reader.GetOrdinal("champion_name")),
                Win: !reader.IsDBNull(reader.GetOrdinal("win")) && reader.GetInt32(reader.GetOrdinal("win")) != 0,
                Timestamp: reader.IsDBNull(reader.GetOrdinal("timestamp")) ? 0 : reader.GetInt64(reader.GetOrdinal("timestamp"))
            );
        }
        return null;
    }

    public async Task<List<SpottedProblem>> GetRecentSpottedProblemsAsync(int limit = 20, string? currentPuuid = null)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT game_id, champion_name, spotted_problems, date_played, win, enemy_laner
            FROM games
            WHERE spotted_problems IS NOT NULL AND spotted_problems != ''
              {CasualFilter} {PuuidFilter}
              AND {RemakeSql.NotRemake("games.game_id")}
            ORDER BY timestamp DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@currentPuuid", currentPuuid ?? "");

        var list = new List<SpottedProblem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new SpottedProblem(
                GameId: reader.GetInt64(reader.GetOrdinal("game_id")),
                ChampionName: reader.GetString(reader.GetOrdinal("champion_name")),
                SpottedProblems: reader.GetString(reader.GetOrdinal("spotted_problems")),
                DatePlayed: reader.IsDBNull(reader.GetOrdinal("date_played")) ? "" : reader.GetString(reader.GetOrdinal("date_played")),
                Win: !reader.IsDBNull(reader.GetOrdinal("win")) && reader.GetInt32(reader.GetOrdinal("win")) != 0,
                EnemyChampion: reader.IsDBNull(reader.GetOrdinal("enemy_laner")) ? "" : reader.GetString(reader.GetOrdinal("enemy_laner"))
            ));
        }
        return list;
    }
}
