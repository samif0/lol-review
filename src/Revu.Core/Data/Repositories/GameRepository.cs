#nullable enable

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Revu.Core.Constants;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// CRUD operations for the games table â€” ported from Python GameRepository.
/// </summary>
public sealed partial class GameRepository : IGameRepository, IGameHistoryQuery, IGameAnalyticsQuery, IGameDeletionService
{
    private readonly IDbConnectionFactory _factory;
    private readonly IBackupService _backupService;

    public GameRepository(IDbConnectionFactory factory, IBackupService backupService)
    {
        _factory = factory;
        _backupService = backupService;
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
            // v3.1.2 (schema v9): stable Riot account id; '' on legacy/unstamped
            // rows. GetStringOrDefault swallows GetOrdinal on pre-v9 DBs.
            Puuid = GetStringOrDefault(reader, "puuid"),
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
            ParticipantMap = GetStringOrDefault(reader, "participant_map"),

            // v2.18 (schema v5): laning-at-10, NULL until timeline backfill.
            CsAt10 = GetNullableDouble(reader, "cs_at_10"),
            GoldDiffAt10 = GetNullableInt(reader, "gold_diff_at_10"),
            CsDiffAt10 = GetNullableDouble(reader, "cs_diff_at_10"),

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

        // items â€” JSON array of ints
        var itemsJson = GetStringOrDefault(reader, "items");
        if (!string.IsNullOrEmpty(itemsJson))
        {
            try { stats.Items = JsonSerializer.Deserialize<List<int>>(itemsJson) ?? []; }
            catch { stats.Items = []; }
        }

        // raw_stats â€” JSON dict
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

    // v2.18 (schema v5): nullable reads where NULL means "not backfilled yet",
    // which must stay distinguishable from zero.
    private static double? GetNullableDouble(SqliteDataReader r, string col)
    {
        try
        {
            var ord = r.GetOrdinal(col);
            return r.IsDBNull(ord) ? null : r.GetDouble(ord);
        }
        catch { return null; }
    }

    private static int? GetNullableInt(SqliteDataReader r, string col)
    {
        try
        {
            var ord = r.GetOrdinal(col);
            return r.IsDBNull(ord) ? null : (int)r.GetInt64(ord);
        }
        catch { return null; }
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

    // â”€â”€ Save â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
                raw_stats, enemy_laner, participant_map, puuid
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
                @raw_stats, @enemy_laner, @participant_map, @puuid
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
        // StatsExtractor stamps the role->champion map at EOG precisely so the
        // 2v2 pairing can render before any backfill; dropping it here was why
        // the pairing only appeared after EnemyLanerBackfillService re-ran.
        cmd.Parameters.AddWithValue("@participant_map", stats.ParticipantMap);
        // v3.1.2 (schema v9): stamp the stable Riot account id at capture for
        // account-scoped analytics. SaveAsync is a plain insert guarded by a
        // prior game_id existence check (it returns the existing id before
        // reaching here), so there is no ON CONFLICT path that could wipe a
        // good puuid with a later empty re-capture.
        cmd.Parameters.AddWithValue("@puuid", stats.Puuid ?? "");

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

    // â”€â”€ Update â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    public async Task UpdateParticipantMapAsync(long gameId, string participantMapJson)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET participant_map = @map WHERE game_id = @game_id";
        cmd.Parameters.AddWithValue("@map", participantMapJson ?? "");
        cmd.Parameters.AddWithValue("@game_id", gameId);
        await cmd.ExecuteNonQueryAsync();
    }

}
