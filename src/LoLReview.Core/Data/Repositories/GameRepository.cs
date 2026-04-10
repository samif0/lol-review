#nullable enable

using System.Text.Json;
using Microsoft.Data.Sqlite;
using LoLReview.Core.Constants;
using LoLReview.Core.Models;

namespace LoLReview.Core.Data.Repositories;

/// <summary>
/// CRUD operations for the games table — ported from Python GameRepository.
/// </summary>
public sealed class GameRepository : IGameRepository
{
    private readonly IDbConnectionFactory _factory;

    public GameRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string CasualFilter => GameConstants.CasualModeSqlFilter;

    /// <summary>
    /// Map a SqliteDataReader row (positioned on a valid record) to a GameStats model.
    /// Handles NULL columns defensively.
    /// </summary>
    private static GameStats MapGameStats(SqliteDataReader reader)
    {
        var stats = new GameStats
        {
            GameId = reader.IsDBNull(reader.GetOrdinal("game_id")) ? 0 : reader.GetInt64(reader.GetOrdinal("game_id")),
            Timestamp = GetLongOrDefault(reader, "timestamp"),
            GameDuration = GetIntOrDefault(reader, "game_duration"),
            GameMode = GetStringOrDefault(reader, "game_mode"),
            GameType = GetStringOrDefault(reader, "game_type"),
            QueueType = GetStringOrDefault(reader, "queue_type"),
            SummonerName = GetStringOrDefault(reader, "summoner_name"),
            ChampionName = GetStringOrDefault(reader, "champion_name"),
            ChampionId = GetIntOrDefault(reader, "champion_id"),
            TeamId = GetIntOrDefault(reader, "team_id"),
            Position = GetStringOrDefault(reader, "position"),
            Role = GetStringOrDefault(reader, "role"),
            Win = GetIntOrDefault(reader, "win") != 0,
            Kills = GetIntOrDefault(reader, "kills"),
            Deaths = GetIntOrDefault(reader, "deaths"),
            Assists = GetIntOrDefault(reader, "assists"),
            KdaRatio = GetDoubleOrDefault(reader, "kda_ratio"),
            LargestKillingSpree = GetIntOrDefault(reader, "largest_killing_spree"),
            LargestMultiKill = GetIntOrDefault(reader, "largest_multi_kill"),
            DoubleKills = GetIntOrDefault(reader, "double_kills"),
            TripleKills = GetIntOrDefault(reader, "triple_kills"),
            QuadraKills = GetIntOrDefault(reader, "quadra_kills"),
            PentaKills = GetIntOrDefault(reader, "penta_kills"),
            FirstBlood = GetIntOrDefault(reader, "first_blood") != 0,
            TotalDamageDealt = GetIntOrDefault(reader, "total_damage_dealt"),
            TotalDamageToChampions = GetIntOrDefault(reader, "total_damage_to_champions"),
            PhysicalDamageToChampions = GetIntOrDefault(reader, "physical_damage_to_champions"),
            MagicDamageToChampions = GetIntOrDefault(reader, "magic_damage_to_champions"),
            TrueDamageToChampions = GetIntOrDefault(reader, "true_damage_to_champions"),
            TotalDamageTaken = GetIntOrDefault(reader, "total_damage_taken"),
            DamageSelfMitigated = GetIntOrDefault(reader, "damage_self_mitigated"),
            LargestCriticalStrike = GetIntOrDefault(reader, "largest_critical_strike"),
            GoldEarned = GetIntOrDefault(reader, "gold_earned"),
            GoldSpent = GetIntOrDefault(reader, "gold_spent"),
            TotalMinionsKilled = GetIntOrDefault(reader, "total_minions_killed"),
            NeutralMinionsKilled = GetIntOrDefault(reader, "neutral_minions_killed"),
            CsTotal = GetIntOrDefault(reader, "cs_total"),
            CsPerMin = GetDoubleOrDefault(reader, "cs_per_min"),
            VisionScore = GetIntOrDefault(reader, "vision_score"),
            WardsPlaced = GetIntOrDefault(reader, "wards_placed"),
            WardsKilled = GetIntOrDefault(reader, "wards_killed"),
            ControlWardsPurchased = GetIntOrDefault(reader, "control_wards_purchased"),
            TurretKills = GetIntOrDefault(reader, "turret_kills"),
            InhibitorKills = GetIntOrDefault(reader, "inhibitor_kills"),
            DragonKills = GetIntOrDefault(reader, "dragon_kills"),
            BaronKills = GetIntOrDefault(reader, "baron_kills"),
            RiftHeraldKills = GetIntOrDefault(reader, "rift_herald_kills"),
            TotalHeal = GetIntOrDefault(reader, "total_heal"),
            TotalHealsOnTeammates = GetIntOrDefault(reader, "total_heals_on_teammates"),
            TotalDamageShieldedOnTeammates = GetIntOrDefault(reader, "total_damage_shielded_on_teammates"),
            TotalTimeCcDealt = GetIntOrDefault(reader, "total_time_cc_dealt"),
            TimeCcingOthers = GetIntOrDefault(reader, "time_ccing_others"),
            Spell1Casts = GetIntOrDefault(reader, "spell1_casts"),
            Spell2Casts = GetIntOrDefault(reader, "spell2_casts"),
            Spell3Casts = GetIntOrDefault(reader, "spell3_casts"),
            Spell4Casts = GetIntOrDefault(reader, "spell4_casts"),
            Summoner1Id = GetIntOrDefault(reader, "summoner1_id"),
            Summoner2Id = GetIntOrDefault(reader, "summoner2_id"),
            ChampLevel = GetIntOrDefault(reader, "champ_level"),
            TeamKills = GetIntOrDefault(reader, "team_kills"),
            KillParticipation = GetDoubleOrDefault(reader, "kill_participation"),
            EnemyLaner = GetStringOrDefault(reader, "enemy_laner"),

            // Review fields
            ReviewNotes = GetStringOrDefault(reader, "review_notes"),
            Rating = GetIntOrDefault(reader, "rating"),
            Tags = GetStringOrDefault(reader, "tags"),
            Mistakes = GetStringOrDefault(reader, "mistakes"),
            WentWell = GetStringOrDefault(reader, "went_well"),
            FocusNext = GetStringOrDefault(reader, "focus_next"),
            SpottedProblems = GetStringOrDefault(reader, "spotted_problems"),
            OutsideControl = GetStringOrDefault(reader, "outside_control"),
            WithinControl = GetStringOrDefault(reader, "within_control"),
            Attribution = GetStringOrDefault(reader, "attribution"),
            PersonalContribution = GetStringOrDefault(reader, "personal_contribution"),
            IsHidden = GetIntOrDefault(reader, "is_hidden") != 0,
        };

        // items — JSON array of ints
        var itemsJson = GetStringOrDefault(reader, "items");
        if (!string.IsNullOrEmpty(itemsJson))
        {
            try { stats.Items = JsonSerializer.Deserialize<List<int>>(itemsJson) ?? []; }
            catch { stats.Items = []; }
        }

        // raw_stats — JSON dict
        var rawJson = GetStringOrDefault(reader, "raw_stats");
        if (!string.IsNullOrEmpty(rawJson))
        {
            try { stats.RawStats = JsonSerializer.Deserialize<Dictionary<string, object>>(rawJson) ?? []; }
            catch { stats.RawStats = []; }
        }

        return stats;
    }

    private static int GetIntOrDefault(SqliteDataReader r, string col)
    {
        try
        {
            var ord = r.GetOrdinal(col);
            if (r.IsDBNull(ord)) return 0;
            // SQLite stores all ints as INT64; safely cast down
            var val = r.GetInt64(ord);
            return val > int.MaxValue ? int.MaxValue : val < int.MinValue ? int.MinValue : (int)val;
        }
        catch { return 0; }
    }

    private static long GetLongOrDefault(SqliteDataReader r, string col)
    {
        try
        {
            var ord = r.GetOrdinal(col);
            return r.IsDBNull(ord) ? 0L : r.GetInt64(ord);
        }
        catch { return 0L; }
    }

    private static double GetDoubleOrDefault(SqliteDataReader r, string col)
    {
        try
        {
            var ord = r.GetOrdinal(col);
            if (r.IsDBNull(ord)) return 0.0;
            // SQLite might store as int or real; handle both
            return Convert.ToDouble(r.GetValue(ord));
        }
        catch { return 0.0; }
    }

    private static string GetStringOrDefault(SqliteDataReader r, string col)
    {
        try
        {
            var ord = r.GetOrdinal(col);
            return r.IsDBNull(ord) ? "" : r.GetString(ord);
        }
        catch { return ""; }
    }

    /// <summary>Read all rows from a command into a list of GameStats.</summary>
    private static async Task<List<GameStats>> ReadAllGamesAsync(SqliteCommand cmd)
    {
        var list = new List<GameStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(MapGameStats(reader));
        }
        return list;
    }

    private static void ApplyRecentFilters(
        SqliteCommand cmd,
        ICollection<string> whereClauses,
        string? champion,
        bool? win)
    {
        if (!string.IsNullOrWhiteSpace(champion) && champion != "All Champions")
        {
            whereClauses.Add("champion_name = @champion");
            cmd.Parameters.AddWithValue("@champion", champion);
        }

        if (win.HasValue)
        {
            whereClauses.Add("win = @win");
            cmd.Parameters.AddWithValue("@win", win.Value ? 1 : 0);
        }
    }

    // ── Save ─────────────────────────────────────────────────────────────

    public async Task<int> SaveAsync(GameStats stats)
    {
        // Only ranked games are saved (manual entries go through a different path).
        if (!GameConstants.RankedQueueTypes.Contains(stats.QueueType ?? ""))
            return -1;

        using var conn = _factory.CreateConnection();

        // Check for duplicates
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT id FROM games WHERE game_id = @gameId";
            checkCmd.Parameters.AddWithValue("@gameId", stats.GameId);
            var existing = await checkCmd.ExecuteScalarAsync();
            if (existing is not null)
                return Convert.ToInt32(existing);
        }

        var dateStr = DateTimeOffset.FromUnixTimeSeconds(stats.Timestamp)
            .LocalDateTime.ToString("yyyy-MM-dd HH:mm");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO games (
                game_id, timestamp, date_played, game_duration, game_mode,
                game_type, queue_type, summoner_name, champion_name, champion_id,
                team_id, position, role, win,
                kills, deaths, assists, kda_ratio,
                largest_killing_spree, largest_multi_kill,
                double_kills, triple_kills, quadra_kills, penta_kills, first_blood,
                total_damage_dealt, total_damage_to_champions,
                physical_damage_to_champions, magic_damage_to_champions,
                true_damage_to_champions, total_damage_taken,
                damage_self_mitigated, largest_critical_strike,
                gold_earned, gold_spent,
                total_minions_killed, neutral_minions_killed, cs_total, cs_per_min,
                vision_score, wards_placed, wards_killed, control_wards_purchased,
                turret_kills, inhibitor_kills, dragon_kills, baron_kills,
                rift_herald_kills,
                total_heal, total_heals_on_teammates,
                total_damage_shielded_on_teammates,
                total_time_cc_dealt, time_ccing_others,
                spell1_casts, spell2_casts, spell3_casts, spell4_casts,
                summoner1_id, summoner2_id, items,
                champ_level, team_kills, kill_participation,
                raw_stats, enemy_laner
            ) VALUES (
                @game_id, @timestamp, @date_played, @game_duration, @game_mode,
                @game_type, @queue_type, @summoner_name, @champion_name, @champion_id,
                @team_id, @position, @role, @win,
                @kills, @deaths, @assists, @kda_ratio,
                @largest_killing_spree, @largest_multi_kill,
                @double_kills, @triple_kills, @quadra_kills, @penta_kills, @first_blood,
                @total_damage_dealt, @total_damage_to_champions,
                @physical_damage_to_champions, @magic_damage_to_champions,
                @true_damage_to_champions, @total_damage_taken,
                @damage_self_mitigated, @largest_critical_strike,
                @gold_earned, @gold_spent,
                @total_minions_killed, @neutral_minions_killed, @cs_total, @cs_per_min,
                @vision_score, @wards_placed, @wards_killed, @control_wards_purchased,
                @turret_kills, @inhibitor_kills, @dragon_kills, @baron_kills,
                @rift_herald_kills,
                @total_heal, @total_heals_on_teammates,
                @total_damage_shielded_on_teammates,
                @total_time_cc_dealt, @time_ccing_others,
                @spell1_casts, @spell2_casts, @spell3_casts, @spell4_casts,
                @summoner1_id, @summoner2_id, @items,
                @champ_level, @team_kills, @kill_participation,
                @raw_stats, @enemy_laner
            )";

        cmd.Parameters.AddWithValue("@game_id", stats.GameId);
        cmd.Parameters.AddWithValue("@timestamp", stats.Timestamp);
        cmd.Parameters.AddWithValue("@date_played", dateStr);
        cmd.Parameters.AddWithValue("@game_duration", stats.GameDuration);
        cmd.Parameters.AddWithValue("@game_mode", stats.GameMode);
        cmd.Parameters.AddWithValue("@game_type", stats.GameType);
        cmd.Parameters.AddWithValue("@queue_type", stats.QueueType);
        cmd.Parameters.AddWithValue("@summoner_name", stats.SummonerName);
        cmd.Parameters.AddWithValue("@champion_name", stats.ChampionName);
        cmd.Parameters.AddWithValue("@champion_id", stats.ChampionId);
        cmd.Parameters.AddWithValue("@team_id", stats.TeamId);
        cmd.Parameters.AddWithValue("@position", stats.Position);
        cmd.Parameters.AddWithValue("@role", stats.Role);
        cmd.Parameters.AddWithValue("@win", stats.Win ? 1 : 0);
        cmd.Parameters.AddWithValue("@kills", stats.Kills);
        cmd.Parameters.AddWithValue("@deaths", stats.Deaths);
        cmd.Parameters.AddWithValue("@assists", stats.Assists);
        cmd.Parameters.AddWithValue("@kda_ratio", stats.KdaRatio);
        cmd.Parameters.AddWithValue("@largest_killing_spree", stats.LargestKillingSpree);
        cmd.Parameters.AddWithValue("@largest_multi_kill", stats.LargestMultiKill);
        cmd.Parameters.AddWithValue("@double_kills", stats.DoubleKills);
        cmd.Parameters.AddWithValue("@triple_kills", stats.TripleKills);
        cmd.Parameters.AddWithValue("@quadra_kills", stats.QuadraKills);
        cmd.Parameters.AddWithValue("@penta_kills", stats.PentaKills);
        cmd.Parameters.AddWithValue("@first_blood", stats.FirstBlood ? 1 : 0);
        cmd.Parameters.AddWithValue("@total_damage_dealt", stats.TotalDamageDealt);
        cmd.Parameters.AddWithValue("@total_damage_to_champions", stats.TotalDamageToChampions);
        cmd.Parameters.AddWithValue("@physical_damage_to_champions", stats.PhysicalDamageToChampions);
        cmd.Parameters.AddWithValue("@magic_damage_to_champions", stats.MagicDamageToChampions);
        cmd.Parameters.AddWithValue("@true_damage_to_champions", stats.TrueDamageToChampions);
        cmd.Parameters.AddWithValue("@total_damage_taken", stats.TotalDamageTaken);
        cmd.Parameters.AddWithValue("@damage_self_mitigated", stats.DamageSelfMitigated);
        cmd.Parameters.AddWithValue("@largest_critical_strike", stats.LargestCriticalStrike);
        cmd.Parameters.AddWithValue("@gold_earned", stats.GoldEarned);
        cmd.Parameters.AddWithValue("@gold_spent", stats.GoldSpent);
        cmd.Parameters.AddWithValue("@total_minions_killed", stats.TotalMinionsKilled);
        cmd.Parameters.AddWithValue("@neutral_minions_killed", stats.NeutralMinionsKilled);
        cmd.Parameters.AddWithValue("@cs_total", stats.CsTotal);
        cmd.Parameters.AddWithValue("@cs_per_min", stats.CsPerMin);
        cmd.Parameters.AddWithValue("@vision_score", stats.VisionScore);
        cmd.Parameters.AddWithValue("@wards_placed", stats.WardsPlaced);
        cmd.Parameters.AddWithValue("@wards_killed", stats.WardsKilled);
        cmd.Parameters.AddWithValue("@control_wards_purchased", stats.ControlWardsPurchased);
        cmd.Parameters.AddWithValue("@turret_kills", stats.TurretKills);
        cmd.Parameters.AddWithValue("@inhibitor_kills", stats.InhibitorKills);
        cmd.Parameters.AddWithValue("@dragon_kills", stats.DragonKills);
        cmd.Parameters.AddWithValue("@baron_kills", stats.BaronKills);
        cmd.Parameters.AddWithValue("@rift_herald_kills", stats.RiftHeraldKills);
        cmd.Parameters.AddWithValue("@total_heal", stats.TotalHeal);
        cmd.Parameters.AddWithValue("@total_heals_on_teammates", stats.TotalHealsOnTeammates);
        cmd.Parameters.AddWithValue("@total_damage_shielded_on_teammates", stats.TotalDamageShieldedOnTeammates);
        cmd.Parameters.AddWithValue("@total_time_cc_dealt", stats.TotalTimeCcDealt);
        cmd.Parameters.AddWithValue("@time_ccing_others", stats.TimeCcingOthers);
        cmd.Parameters.AddWithValue("@spell1_casts", stats.Spell1Casts);
        cmd.Parameters.AddWithValue("@spell2_casts", stats.Spell2Casts);
        cmd.Parameters.AddWithValue("@spell3_casts", stats.Spell3Casts);
        cmd.Parameters.AddWithValue("@spell4_casts", stats.Spell4Casts);
        cmd.Parameters.AddWithValue("@summoner1_id", stats.Summoner1Id);
        cmd.Parameters.AddWithValue("@summoner2_id", stats.Summoner2Id);
        cmd.Parameters.AddWithValue("@items", JsonSerializer.Serialize(stats.Items));
        cmd.Parameters.AddWithValue("@champ_level", stats.ChampLevel);
        cmd.Parameters.AddWithValue("@team_kills", stats.TeamKills);
        cmd.Parameters.AddWithValue("@kill_participation", stats.KillParticipation);
        cmd.Parameters.AddWithValue("@raw_stats", JsonSerializer.Serialize(stats.RawStats));
        cmd.Parameters.AddWithValue("@enemy_laner", stats.EnemyLaner);

        await cmd.ExecuteNonQueryAsync();

        // Retrieve last inserted rowid
        using var lastIdCmd = conn.CreateCommand();
        lastIdCmd.CommandText = "SELECT last_insert_rowid()";
        var rowId = await lastIdCmd.ExecuteScalarAsync();
        return Convert.ToInt32(rowId);
    }

    public async Task<int> SaveManualAsync(
        string championName,
        bool win,
        int kills = 0,
        int deaths = 0,
        int assists = 0,
        string gameMode = "Manual Entry",
        string notes = "",
        string mistakes = "",
        string wentWell = "",
        string focusNext = "",
        List<string>? tags = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var gameId = (int)now;
        var kdaRatio = Math.Round((kills + assists) / (double)Math.Max(deaths, 1), 2);
        var dateStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO games (
                game_id, timestamp, date_played, game_duration, game_mode,
                game_type, queue_type, summoner_name, champion_name, champion_id,
                team_id, position, role, win,
                kills, deaths, assists, kda_ratio,
                review_notes, tags, mistakes, went_well, focus_next
            ) VALUES (
                @game_id, @timestamp, @date_played, @game_duration, @game_mode,
                @game_type, @queue_type, @summoner_name, @champion_name, @champion_id,
                @team_id, @position, @role, @win,
                @kills, @deaths, @assists, @kda_ratio,
                @review_notes, @tags, @mistakes, @went_well, @focus_next
            )";

        cmd.Parameters.AddWithValue("@game_id", gameId);
        cmd.Parameters.AddWithValue("@timestamp", now);
        cmd.Parameters.AddWithValue("@date_played", dateStr);
        cmd.Parameters.AddWithValue("@game_duration", 0);
        cmd.Parameters.AddWithValue("@game_mode", gameMode);
        cmd.Parameters.AddWithValue("@game_type", "Manual");
        cmd.Parameters.AddWithValue("@queue_type", "Manual");
        cmd.Parameters.AddWithValue("@summoner_name", "Manual Entry");
        cmd.Parameters.AddWithValue("@champion_name", championName);
        cmd.Parameters.AddWithValue("@champion_id", 0);
        cmd.Parameters.AddWithValue("@team_id", 0);
        cmd.Parameters.AddWithValue("@position", "");
        cmd.Parameters.AddWithValue("@role", "");
        cmd.Parameters.AddWithValue("@win", win ? 1 : 0);
        cmd.Parameters.AddWithValue("@kills", kills);
        cmd.Parameters.AddWithValue("@deaths", deaths);
        cmd.Parameters.AddWithValue("@assists", assists);
        cmd.Parameters.AddWithValue("@kda_ratio", kdaRatio);
        cmd.Parameters.AddWithValue("@review_notes", notes);
        cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(tags ?? []));
        cmd.Parameters.AddWithValue("@mistakes", mistakes);
        cmd.Parameters.AddWithValue("@went_well", wentWell);
        cmd.Parameters.AddWithValue("@focus_next", focusNext);

        await cmd.ExecuteNonQueryAsync();
        return gameId;
    }

    // ── Update ───────────────────────────────────────────────────────────

    public async Task UpdateReviewAsync(long gameId, GameReview review)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE games SET
                rating = @rating,
                review_notes = @notes,
                tags = @tags,
                mistakes = @mistakes,
                went_well = @went_well,
                focus_next = @focus_next,
                spotted_problems = @spotted_problems,
                outside_control = @outside_control,
                within_control = @within_control,
                attribution = @attribution,
                personal_contribution = @personal_contribution
            WHERE game_id = @game_id";

        cmd.Parameters.AddWithValue("@rating", review.Rating);
        cmd.Parameters.AddWithValue("@notes", review.Notes);
        cmd.Parameters.AddWithValue("@tags", review.Tags);
        cmd.Parameters.AddWithValue("@mistakes", review.Mistakes);
        cmd.Parameters.AddWithValue("@went_well", review.WentWell);
        cmd.Parameters.AddWithValue("@focus_next", review.FocusNext);
        cmd.Parameters.AddWithValue("@spotted_problems", review.SpottedProblems);
        cmd.Parameters.AddWithValue("@outside_control", review.OutsideControl);
        cmd.Parameters.AddWithValue("@within_control", review.WithinControl);
        cmd.Parameters.AddWithValue("@attribution", review.Attribution);
        cmd.Parameters.AddWithValue("@personal_contribution", review.PersonalContribution);
        cmd.Parameters.AddWithValue("@game_id", gameId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateEnemyLanerAsync(long gameId, string enemyLaner)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET enemy_laner = @enemy_laner WHERE game_id = @game_id";
        cmd.Parameters.AddWithValue("@enemy_laner", enemyLaner);
        cmd.Parameters.AddWithValue("@game_id", gameId);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetHiddenAsync(long gameId, bool hidden)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET is_hidden = @hidden WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@hidden", hidden ? 1 : 0);
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await cmd.ExecuteNonQueryAsync();
    }

    // ── Single reads ─────────────────────────────────────────────────────

    public async Task<GameStats?> GetAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM games WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapGameStats(reader) : null;
    }

    // ── List reads ───────────────────────────────────────────────────────

    public async Task<List<GameStats>> GetRecentAsync(
        int limit = 50,
        int offset = 0,
        string? champion = null,
        bool? win = null)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        var whereClauses = new List<string> { "1=1" };
        ApplyRecentFilters(cmd, whereClauses, champion, win);
        cmd.CommandText =
            $"SELECT * FROM games WHERE {string.Join(" AND ", whereClauses)} {CasualFilter} " +
            "ORDER BY timestamp DESC LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<int> GetRecentCountAsync(string? champion = null, bool? win = null)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        var whereClauses = new List<string> { "1=1" };
        ApplyRecentFilters(cmd, whereClauses, champion, win);
        cmd.CommandText = $"SELECT COUNT(*) FROM games WHERE {string.Join(" AND ", whereClauses)} {CasualFilter}";
        var result = await cmd.ExecuteScalarAsync();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    public async Task<List<GameStats>> GetGamesForDateAsync(string dateStr)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM games WHERE date_played LIKE @datePat {CasualFilter} ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("@datePat", $"{dateStr}%");

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<List<GameStats>> GetTodaysGamesAsync()
    {
        using var conn = _factory.CreateConnection();

        var todayStart = DateTime.Today;
        var todayTimestamp = new DateTimeOffset(todayStart).ToUnixTimeSeconds();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM games WHERE timestamp >= @ts {CasualFilter} ORDER BY timestamp ASC";
        cmd.Parameters.AddWithValue("@ts", todayTimestamp);

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<List<GameStats>> GetLossesAsync(string? champion = null)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        if (!string.IsNullOrEmpty(champion) && champion != "All Champions")
        {
            cmd.CommandText = $"SELECT * FROM games WHERE win = 0 AND champion_name = @champ {CasualFilter} ORDER BY timestamp DESC";
            cmd.Parameters.AddWithValue("@champ", champion);
        }
        else
        {
            cmd.CommandText = $"SELECT * FROM games WHERE win = 0 {CasualFilter} ORDER BY timestamp DESC";
        }

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<List<GameStats>> GetUnreviewedGamesAsync(int days = 3)
    {
        using var conn = _factory.CreateConnection();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT * FROM games
            WHERE timestamp >= @cutoff
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
                          )
                    )
                 OR EXISTS (
                        SELECT 1
                        FROM game_concept_tags
                        WHERE game_concept_tags.game_id = games.game_id
                    )
              )
              {CasualFilter}
            ORDER BY timestamp DESC";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        return await ReadAllGamesAsync(cmd);
    }

    public async Task<List<string>> GetUniqueChampionsAsync(bool lossesOnly = false)
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        if (lossesOnly)
        {
            cmd.CommandText = $"SELECT DISTINCT champion_name FROM games WHERE win = 0 {CasualFilter} ORDER BY champion_name";
        }
        else
        {
            cmd.CommandText = $"SELECT DISTINCT champion_name FROM games WHERE 1=1 {CasualFilter} ORDER BY champion_name";
        }

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    // ── Aggregation reads ────────────────────────────────────────────────

    public async Task<List<ChampionStats>> GetChampionStatsAsync()
    {
        using var conn = _factory.CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                champion_name,
                COUNT(*) as games_played,
                SUM(win) as wins,
                ROUND(AVG(win) * 100, 1) as winrate,
                ROUND(AVG(kills), 1) as avg_kills,
                ROUND(AVG(deaths), 1) as avg_deaths,
                ROUND(AVG(assists), 1) as avg_assists,
                ROUND(AVG(kda_ratio), 2) as avg_kda,
                ROUND(AVG(cs_per_min), 1) as avg_cs_min,
                ROUND(AVG(vision_score), 1) as avg_vision,
                ROUND(AVG(total_damage_to_champions), 0) as avg_damage
            FROM games
            WHERE 1=1 {CasualFilter}
            GROUP BY champion_name
            ORDER BY games_played DESC";

        var list = new List<ChampionStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new ChampionStats(
                ChampionName: reader.GetString(reader.GetOrdinal("champion_name")),
                GamesPlayed: reader.GetInt32(reader.GetOrdinal("games_played")),
                Wins: reader.IsDBNull(reader.GetOrdinal("wins")) ? 0 : reader.GetInt32(reader.GetOrdinal("wins")),
                Winrate: reader.IsDBNull(reader.GetOrdinal("winrate")) ? 0 : reader.GetDouble(reader.GetOrdinal("winrate")),
                AvgKills: reader.IsDBNull(reader.GetOrdinal("avg_kills")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_kills")),
                AvgDeaths: reader.IsDBNull(reader.GetOrdinal("avg_deaths")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_deaths")),
                AvgAssists: reader.IsDBNull(reader.GetOrdinal("avg_assists")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_assists")),
                AvgKda: reader.IsDBNull(reader.GetOrdinal("avg_kda")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_kda")),
                AvgCsMin: reader.IsDBNull(reader.GetOrdinal("avg_cs_min")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_cs_min")),
                AvgVision: reader.IsDBNull(reader.GetOrdinal("avg_vision")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_vision")),
                AvgDamage: reader.IsDBNull(reader.GetOrdinal("avg_damage")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_damage"))
            ));
        }
        return list;
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
            SELECT focus_next, mistakes, went_well
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
                WentWell: reader.IsDBNull(reader.GetOrdinal("went_well")) ? "" : reader.GetString(reader.GetOrdinal("went_well"))
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
            SELECT game_id, champion_name, spotted_problems, date_played, win
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
                Win: !reader.IsDBNull(reader.GetOrdinal("win")) && reader.GetInt32(reader.GetOrdinal("win")) != 0
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

        // Python returns reversed(rows) — chronological order
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

        // Python returns reversed(rows) — chronological order
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
