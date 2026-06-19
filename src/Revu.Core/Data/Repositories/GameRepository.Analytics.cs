#nullable enable

using Revu.Core.Constants;
using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

public sealed partial class GameRepository
{
    public async Task<List<ChampionStats>> GetChampionStatsAsync()
    {
        using var conn = _factory.CreateConnection();

        // We aggregate per RAW champion_name in SQL, then collapse spelling
        // variants of the same champion (e.g. "Kai'Sa" vs "Kaisa", which differ
        // only by an apostrophe the LCU/EOG payloads aren't consistent about) in
        // C# keyed on GameConstants.NormalizeChampionKey. SQLite has no built-in
        // apostrophe-strip to do this in GROUP BY, and re-grouping here lets us
        // emit one row per champion with the canonical Data Dragon display name
        // and correctly games-weighted averages, without mutating stored rows.
        // SUM(metric) is carried so the merge stays exact rather than averaging
        // pre-rounded per-variant averages.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                champion_name,
                COUNT(*) as games_played,
                SUM(win) as wins,
                SUM(kills) as sum_kills,
                SUM(deaths) as sum_deaths,
                SUM(assists) as sum_assists,
                SUM(kda_ratio) as sum_kda,
                SUM(cs_per_min) as sum_cs_min,
                SUM(vision_score) as sum_vision,
                SUM(total_damage_to_champions) as sum_damage
            FROM games
            WHERE 1=1 {CasualFilter}
            GROUP BY champion_name";

        // Accumulate raw-spelling rows into one bucket per normalized champion.
        var buckets = new Dictionary<string, ChampionAccumulator>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var rawName = reader.GetString(reader.GetOrdinal("champion_name"));
            var key = GameConstants.NormalizeChampionKey(rawName);
            if (key.Length == 0) continue;

            if (!buckets.TryGetValue(key, out var acc))
            {
                acc = new ChampionAccumulator { DisplayName = GameConstants.CanonicalChampionName(rawName) };
                buckets[key] = acc;
            }

            acc.Games += reader.GetInt32(reader.GetOrdinal("games_played"));
            acc.Wins += reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins"));
            acc.SumKills += ReadDouble(reader, "sum_kills");
            acc.SumDeaths += ReadDouble(reader, "sum_deaths");
            acc.SumAssists += ReadDouble(reader, "sum_assists");
            acc.SumKda += ReadDouble(reader, "sum_kda");
            acc.SumCsMin += ReadDouble(reader, "sum_cs_min");
            acc.SumVision += ReadDouble(reader, "sum_vision");
            acc.SumDamage += ReadDouble(reader, "sum_damage");
        }

        return buckets.Values
            .Select(acc =>
            {
                var n = Math.Max(acc.Games, 1);
                return new ChampionStats(
                    ChampionName: acc.DisplayName,
                    GamesPlayed: acc.Games,
                    Wins: acc.Wins,
                    Winrate: Math.Round(100.0 * acc.Wins / n, 1),
                    AvgKills: Math.Round(acc.SumKills / n, 1),
                    AvgDeaths: Math.Round(acc.SumDeaths / n, 1),
                    AvgAssists: Math.Round(acc.SumAssists / n, 1),
                    AvgKda: Math.Round(acc.SumKda / n, 2),
                    AvgCsMin: Math.Round(acc.SumCsMin / n, 1),
                    AvgVision: Math.Round(acc.SumVision / n, 1),
                    AvgDamage: Math.Round(acc.SumDamage / n, 0));
            })
            .OrderByDescending(c => c.GamesPlayed)
            .ToList();
    }

    private static double ReadDouble(Microsoft.Data.Sqlite.SqliteDataReader reader, string column)
    {
        var ord = reader.GetOrdinal(column);
        return reader.IsDBNull(ord) ? 0.0 : Convert.ToDouble(reader.GetValue(ord));
    }

    private sealed class ChampionAccumulator
    {
        public string DisplayName = "";
        public int Games;
        public int Wins;
        public double SumKills;
        public double SumDeaths;
        public double SumAssists;
        public double SumKda;
        public double SumCsMin;
        public double SumVision;
        public double SumDamage;
    }

    public async Task<OverallStats> GetOverallStatsAsync()
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                COUNT(*) as total_games,
                SUM(win) as total_wins,
                ROUND(AVG(win) * 100, 1) as winrate,
                ROUND(AVG(kills), 1) as avg_kills,
                ROUND(AVG(deaths), 1) as avg_deaths,
                ROUND(AVG(assists), 1) as avg_assists,
                ROUND(AVG(kda_ratio), 2) as avg_kda,
                ROUND(AVG(cs_per_min), 1) as avg_cs_min,
                ROUND(AVG(vision_score), 1) as avg_vision,
                SUM(penta_kills) as total_pentas,
                SUM(quadra_kills) as total_quadras,
                MAX(kills) as max_kills,
                MAX(kda_ratio) as best_kda
            FROM games
            WHERE 1=1 {CasualFilter}";

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new OverallStats(
                TotalGames: reader.IsDBNull(reader.GetOrdinal("total_games")) ? 0 : reader.GetInt32(reader.GetOrdinal("total_games")),
                TotalWins: reader.IsDBNull(reader.GetOrdinal("total_wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("total_wins")),
                Winrate: reader.IsDBNull(reader.GetOrdinal("winrate")) ? 0 : reader.GetDouble(reader.GetOrdinal("winrate")),
                AvgKills: reader.IsDBNull(reader.GetOrdinal("avg_kills")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_kills")),
                AvgDeaths: reader.IsDBNull(reader.GetOrdinal("avg_deaths")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_deaths")),
                AvgAssists: reader.IsDBNull(reader.GetOrdinal("avg_assists")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_assists")),
                AvgKda: reader.IsDBNull(reader.GetOrdinal("avg_kda")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_kda")),
                AvgCsMin: reader.IsDBNull(reader.GetOrdinal("avg_cs_min")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_cs_min")),
                AvgVision: reader.IsDBNull(reader.GetOrdinal("avg_vision")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_vision")),
                TotalPentas: reader.IsDBNull(reader.GetOrdinal("total_pentas")) ? 0 : reader.GetInt32(reader.GetOrdinal("total_pentas")),
                TotalQuadras: reader.IsDBNull(reader.GetOrdinal("total_quadras")) ? 0 : reader.GetInt32(reader.GetOrdinal("total_quadras")),
                MaxKills: reader.IsDBNull(reader.GetOrdinal("max_kills")) ? 0 : reader.GetInt32(reader.GetOrdinal("max_kills")),
                BestKda: reader.IsDBNull(reader.GetOrdinal("best_kda")) ? 0 : reader.GetDouble(reader.GetOrdinal("best_kda"))
            );
        }

        return new OverallStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    public async Task<ReviewFocus?> GetLastReviewFocusAsync()
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT focus_next, mistakes, went_well, champion_name, win, timestamp
            FROM games
            WHERE (focus_next != '' OR mistakes != '')
                {CasualFilter}
            ORDER BY timestamp DESC
            LIMIT 1";

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

    public async Task<int> GetWinStreakAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT win FROM games WHERE 1=1 {CasualFilter} ORDER BY timestamp DESC LIMIT 50";

        var results = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.IsDBNull(0) ? 0 : reader.GetInt32(0));
        }

        if (results.Count == 0)
            return 0;

        var firstResult = results[0];
        var streak = 0;
        foreach (var win in results)
        {
            if (win == firstResult)
                streak++;
            else
                break;
        }

        return firstResult != 0 ? streak : -streak;
    }

    public async Task<List<AttributionStat>> GetAttributionStatsAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT attribution,
                COUNT(*) as games,
                SUM(win) as wins,
                COUNT(*) - SUM(win) as losses,
                ROUND(AVG(win) * 100, 1) as winrate
            FROM games
            WHERE attribution IS NOT NULL AND attribution != ''
              {CasualFilter}
            GROUP BY attribution
            ORDER BY games DESC";

        var list = new List<AttributionStat>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new AttributionStat(
                Attribution: reader.GetString(reader.GetOrdinal("attribution")),
                Games: reader.GetInt32(reader.GetOrdinal("games")),
                Wins: reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins")),
                Losses: reader.IsDBNull(reader.GetOrdinal("losses")) ? 0 : reader.GetInt32(reader.GetOrdinal("losses")),
                Winrate: reader.IsDBNull(reader.GetOrdinal("winrate")) ? 0 : reader.GetDouble(reader.GetOrdinal("winrate"))
            ));
        }
        return list;
    }

    public async Task<List<SpottedProblem>> GetRecentSpottedProblemsAsync(int limit = 20)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT game_id, champion_name, spotted_problems, date_played, win, enemy_laner
            FROM games
            WHERE spotted_problems IS NOT NULL AND spotted_problems != ''
              {CasualFilter}
            ORDER BY timestamp DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

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

    public async Task<List<ChartDataPoint>> GetRecentForChartsAsync(int limit = 100)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT game_id, win, deaths, timestamp, champion_name, kda_ratio
            FROM games
            WHERE 1=1 {CasualFilter}
            ORDER BY timestamp DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<ChartDataPoint>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ChartDataPoint(
                GameId: reader.GetInt64(reader.GetOrdinal("game_id")),
                Win: !reader.IsDBNull(reader.GetOrdinal("win")) && reader.GetInt32(reader.GetOrdinal("win")) != 0,
                Deaths: reader.IsDBNull(reader.GetOrdinal("deaths")) ? 0 : reader.GetInt32(reader.GetOrdinal("deaths")),
                Timestamp: reader.GetInt64(reader.GetOrdinal("timestamp")),
                ChampionName: reader.IsDBNull(reader.GetOrdinal("champion_name")) ? "" : reader.GetString(reader.GetOrdinal("champion_name")),
                KdaRatio: reader.IsDBNull(reader.GetOrdinal("kda_ratio")) ? 0 : reader.GetDouble(reader.GetOrdinal("kda_ratio"))
            ));
        }

        // Python returns reversed(rows) â€” chronological order
        list.Reverse();
        return list;
    }

    public async Task<List<MatchupStat>> GetMatchupStatsAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT champion_name, enemy_laner,
                    COUNT(*) as games,
                    SUM(win) as wins,
                    ROUND(100.0 * SUM(win) / COUNT(*), 1) as winrate,
                    ROUND(AVG(kda_ratio), 2) as avg_kda,
                    ROUND(AVG(deaths), 1) as avg_deaths
                FROM games
                WHERE enemy_laner IS NOT NULL AND enemy_laner != ''
                    {CasualFilter}
                GROUP BY champion_name, enemy_laner
                HAVING COUNT(*) >= 2
                ORDER BY COUNT(*) DESC";

        var list = new List<MatchupStat>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new MatchupStat(
                ChampionName: reader.GetString(reader.GetOrdinal("champion_name")),
                EnemyLaner: reader.GetString(reader.GetOrdinal("enemy_laner")),
                Games: reader.GetInt32(reader.GetOrdinal("games")),
                Wins: reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins")),
                Winrate: reader.IsDBNull(reader.GetOrdinal("winrate")) ? 0 : reader.GetDouble(reader.GetOrdinal("winrate")),
                AvgKda: reader.IsDBNull(reader.GetOrdinal("avg_kda")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_kda")),
                AvgDeaths: reader.IsDBNull(reader.GetOrdinal("avg_deaths")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_deaths"))
            ));
        }
        return list;
    }

    public async Task<List<PerformanceTrend>> GetPerformanceTrendsAsync(int limit = 50)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT cs_per_min, vision_score, kda_ratio, deaths,
                    kill_participation, champion_name, timestamp, win
                FROM games
                WHERE 1=1 {CasualFilter}
                ORDER BY timestamp DESC
                LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<PerformanceTrend>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new PerformanceTrend(
                CsPerMin: reader.IsDBNull(reader.GetOrdinal("cs_per_min")) ? 0 : reader.GetDouble(reader.GetOrdinal("cs_per_min")),
                VisionScore: reader.IsDBNull(reader.GetOrdinal("vision_score")) ? 0 : reader.GetInt32(reader.GetOrdinal("vision_score")),
                KdaRatio: reader.IsDBNull(reader.GetOrdinal("kda_ratio")) ? 0 : reader.GetDouble(reader.GetOrdinal("kda_ratio")),
                Deaths: reader.IsDBNull(reader.GetOrdinal("deaths")) ? 0 : reader.GetInt32(reader.GetOrdinal("deaths")),
                KillParticipation: reader.IsDBNull(reader.GetOrdinal("kill_participation")) ? 0 : reader.GetDouble(reader.GetOrdinal("kill_participation")),
                ChampionName: reader.IsDBNull(reader.GetOrdinal("champion_name")) ? "" : reader.GetString(reader.GetOrdinal("champion_name")),
                Timestamp: reader.GetInt64(reader.GetOrdinal("timestamp")),
                Win: !reader.IsDBNull(reader.GetOrdinal("win")) && reader.GetInt32(reader.GetOrdinal("win")) != 0
            ));
        }

        // Python returns reversed(rows) â€” chronological order
        list.Reverse();
        return list;
    }

    public async Task<List<RoleStat>> GetRoleStatsAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT position,
                    COUNT(*) as games,
                    SUM(win) as wins,
                    ROUND(100.0 * SUM(win) / COUNT(*), 1) as winrate,
                    ROUND(AVG(kda_ratio), 2) as avg_kda
                FROM games
                WHERE position IS NOT NULL AND position != ''
                    {CasualFilter}
                GROUP BY position
                ORDER BY COUNT(*) DESC";

        var list = new List<RoleStat>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new RoleStat(
                Position: reader.GetString(reader.GetOrdinal("position")),
                Games: reader.GetInt32(reader.GetOrdinal("games")),
                Wins: reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins")),
                Winrate: reader.IsDBNull(reader.GetOrdinal("winrate")) ? 0 : reader.GetDouble(reader.GetOrdinal("winrate")),
                AvgKda: reader.IsDBNull(reader.GetOrdinal("avg_kda")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_kda"))
            ));
        }
        return list;
    }

    public async Task<List<DurationStat>> GetDurationStatsAsync()
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                    CASE
                        WHEN game_duration < 1200 THEN '< 20m'
                        WHEN game_duration < 1800 THEN '20-30m'
                        WHEN game_duration < 2400 THEN '30-40m'
                        ELSE '40m+'
                    END as bucket,
                    COUNT(*) as games,
                    SUM(win) as wins,
                    ROUND(100.0 * SUM(win) / COUNT(*), 1) as winrate
                FROM games
                WHERE game_duration > 0 {CasualFilter}
                GROUP BY bucket
                ORDER BY MIN(game_duration)";

        var list = new List<DurationStat>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new DurationStat(
                Bucket: reader.GetString(reader.GetOrdinal("bucket")),
                Games: reader.GetInt32(reader.GetOrdinal("games")),
                Wins: reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins")),
                Winrate: reader.IsDBNull(reader.GetOrdinal("winrate")) ? 0 : reader.GetDouble(reader.GetOrdinal("winrate"))
            ));
        }
        return list;
    }

    public async Task<RecentStats> GetRecentStatsAsync(int limit = 20)
    {
        using var conn = _factory.CreateConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                    COUNT(*) as games,
                    ROUND(100.0 * SUM(win) / MAX(COUNT(*), 1), 1) as winrate,
                    ROUND(AVG(cs_per_min), 1) as avg_cs_min,
                    ROUND(AVG(vision_score), 1) as avg_vision,
                    ROUND(AVG(deaths), 1) as avg_deaths,
                    ROUND(AVG(kda_ratio), 2) as avg_kda,
                    ROUND(AVG(kills), 1) as avg_kills
                FROM (
                    SELECT * FROM games
                    WHERE 1=1 {CasualFilter}
                    ORDER BY timestamp DESC
                    LIMIT @limit
                )";
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new RecentStats(
                Games: reader.IsDBNull(reader.GetOrdinal("games")) ? 0 : reader.GetInt32(reader.GetOrdinal("games")),
                Winrate: reader.IsDBNull(reader.GetOrdinal("winrate")) ? 0 : reader.GetDouble(reader.GetOrdinal("winrate")),
                AvgCsMin: reader.IsDBNull(reader.GetOrdinal("avg_cs_min")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_cs_min")),
                AvgVision: reader.IsDBNull(reader.GetOrdinal("avg_vision")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_vision")),
                AvgDeaths: reader.IsDBNull(reader.GetOrdinal("avg_deaths")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_deaths")),
                AvgKda: reader.IsDBNull(reader.GetOrdinal("avg_kda")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_kda")),
                AvgKills: reader.IsDBNull(reader.GetOrdinal("avg_kills")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_kills"))
            );
        }

        return new RecentStats(0, 0, 0, 0, 0, 0, 0);
    }
}
