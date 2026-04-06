#nullable enable

using System.Text.Json;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Lcu;

/// <summary>
/// Extracts structured <see cref="GameStats"/> from end-of-game and match history data.
/// Ported from Python stats.py.
/// </summary>
public static class StatsExtractor
{
    /// <summary>
    /// Extract GameStats from the end-of-game stats block.
    /// The EOG block uses ALL_CAPS field names in the stats sub-object.
    /// </summary>
    public static GameStats? ExtractFromEog(JsonElement eogData, ILogger? logger = null)
    {
        try
        {
            if (eogData.ValueKind != JsonValueKind.Object)
                return null;

            var localPlayer = eogData.GetPropertyObjectOrDefault("localPlayer");
            if (localPlayer is null)
                return null;

            var statsBlock = localPlayer.Value.GetPropertyObjectOrDefault("stats");
            if (statsBlock is null)
                return null;

            var stats = statsBlock.Value;
            var gameId = eogData.GetPropertyLongOrDefault("gameId", 0);
            if (gameId <= 0)
            {
                logger?.LogWarning("Skipping EOG stats with invalid gameId {GameId}", gameId);
                return null;
            }

            var gameLength = eogData.GetPropertyIntOrDefault("gameLength", 0);
            var gameMode = eogData.GetPropertyOrDefault("gameMode", "");
            var gameType = eogData.GetPropertyOrDefault("gameType", "");
            var teamId = localPlayer.Value.GetPropertyIntOrDefault("teamId", 100);

            var kills = GetStatInt(stats, "CHAMPIONS_KILLED");
            var deaths = GetStatInt(stats, "NUM_DEATHS");
            var assists = GetStatInt(stats, "ASSISTS");
            var minions = GetStatInt(stats, "MINIONS_KILLED");
            var jungle = GetStatInt(stats, "NEUTRAL_MINIONS_KILLED");
            var csTotal = minions + jungle;
            var durationMin = Math.Max(gameLength / 60.0, 1.0);

            // Prefer the explicit team kill total when the payload exposes it.
            // Player-level rows are still a useful fallback, but they have been
            // observed to under-count in some EOG payload shapes.
            var teamKillsTotal = 0;
            var teamKillsFromTeamStats = 0;
            var teamKillsFromPlayers = 0;
            var teamPlayerCount = 0;
            if (eogData.TryGetProperty("teams", out var teams) && teams.ValueKind == JsonValueKind.Array)
            {
                foreach (var teamData in teams.EnumerateArray())
                {
                    var dataTeamId = teamData.GetPropertyIntOrDefault("teamId", 0);
                    if (dataTeamId != teamId)
                    {
                        continue;
                    }

                    var teamStats = teamData.GetPropertyObjectOrDefault("stats");
                    if (teamStats is not null)
                    {
                        teamKillsFromTeamStats = GetStatInt(teamStats.Value, "CHAMPIONS_KILLED");
                        if (teamKillsFromTeamStats == 0)
                        {
                            teamKillsFromTeamStats = teamStats.Value.GetPropertyIntOrDefault("kills", 0);
                        }
                    }

                    if (!teamData.TryGetProperty("players", out var players)
                        || players.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var p in players.EnumerateArray())
                    {
                        teamPlayerCount++;
                        var playerKills = 0;
                        var pStats = p.GetPropertyObjectOrDefault("stats");
                        if (pStats is not null)
                        {
                            playerKills = GetStatInt(pStats.Value, "CHAMPIONS_KILLED");
                            if (playerKills == 0)
                                playerKills = pStats.Value.GetPropertyIntOrDefault("kills", 0);
                        }

                        // Fallback: kills directly on the player object
                        if (playerKills == 0)
                            playerKills = GetStatInt(p, "CHAMPIONS_KILLED");
                        if (playerKills == 0)
                            playerKills = p.GetPropertyIntOrDefault("kills", 0);

                        teamKillsFromPlayers += playerKills;
                    }
                }
            }

            if (teamKillsFromTeamStats > 0)
            {
                if (teamKillsFromPlayers > 0 && teamKillsFromPlayers != teamKillsFromTeamStats)
                {
                    logger?.LogWarning(
                        "EOG KP mismatch: teamStatsKills={TeamStatsKills}, playerSumKills={PlayerSumKills}, playerTeamId={TeamId}",
                        teamKillsFromTeamStats, teamKillsFromPlayers, teamId);
                }

                teamKillsTotal = teamKillsFromTeamStats;
            }
            else if (teamPlayerCount < 5
                || (teamKillsFromPlayers > 0 && kills + assists > teamKillsFromPlayers))
            {
                logger?.LogWarning(
                    "EOG KP issue: teamPlayerCount={TeamPlayerCount}, playerSumKills={PlayerSumKills}, playerK={Kills}, playerA={Assists}, playerTeamId={TeamId}",
                    teamPlayerCount, teamKillsFromPlayers, kills, assists, teamId);
                teamKillsTotal = 0;
            }
            else
            {
                teamKillsTotal = teamKillsFromPlayers;
            }

            // Extract enemy team champion names and positions for matchup reference
            var enemyChampions = new List<string>();
            var enemyByPosition = new Dictionary<string, string>();
            var myPosition = localPlayer.Value.GetPropertyOrDefault("selectedPosition", "");

            if (eogData.TryGetProperty("teams", out var teamsForEnemy) && teamsForEnemy.ValueKind == JsonValueKind.Array)
            {
                foreach (var teamData in teamsForEnemy.EnumerateArray())
                {
                    if (teamData.GetPropertyIntOrDefault("teamId", 0) != teamId
                        && teamData.TryGetProperty("players", out var players)
                        && players.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in players.EnumerateArray())
                        {
                            var champ = p.GetPropertyOrDefault("championName", "");
                            if (!string.IsNullOrEmpty(champ))
                            {
                                enemyChampions.Add(champ);
                                var pos = p.GetPropertyOrDefault("selectedPosition", "");
                                if (!string.IsNullOrEmpty(pos))
                                {
                                    enemyByPosition[pos] = champ;
                                }
                            }
                        }
                    }
                }
            }

            var kda = (kills + assists) / Math.Max(deaths, 1.0);
            // Cap KP at 100% — teamKillsTotal can be 0 or under-counted in some game modes
            var kp = teamKillsTotal > 0
                ? Math.Min((kills + assists) / (double)teamKillsTotal * 100.0, 100.0)
                : 0.0;

            // Determine win: the stats block WIN field can be a string "0"/"1" or a number
            var win = false;
            if (stats.TryGetProperty("WIN", out var winProp))
            {
                if (winProp.ValueKind == JsonValueKind.String)
                    win = winProp.GetString() != "0";
                else if (winProp.ValueKind == JsonValueKind.Number)
                    win = winProp.GetInt32() != 0;
                else if (winProp.ValueKind == JsonValueKind.True)
                    win = true;
            }

            var gs = new GameStats
            {
                GameId = gameId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                GameDuration = gameLength,
                GameMode = gameMode,
                GameType = gameType,
                ChampionName = localPlayer.Value.GetPropertyOrDefault("championName", "Unknown"),
                ChampionId = localPlayer.Value.GetPropertyIntOrDefault("championId", 0),
                TeamId = teamId,
                Position = myPosition,
                Win = win,
                Kills = kills,
                Deaths = deaths,
                Assists = assists,
                KdaRatio = Math.Round(kda, 2),
                LargestKillingSpree = GetStatInt(stats, "LARGEST_KILLING_SPREE"),
                LargestMultiKill = GetStatInt(stats, "LARGEST_MULTI_KILL"),
                DoubleKills = GetStatInt(stats, "DOUBLE_KILLS"),
                TripleKills = GetStatInt(stats, "TRIPLE_KILLS"),
                QuadraKills = GetStatInt(stats, "QUADRA_KILLS"),
                PentaKills = GetStatInt(stats, "PENTA_KILLS"),
                TotalDamageDealt = GetStatInt(stats, "TOTAL_DAMAGE_DEALT"),
                TotalDamageToChampions = GetStatInt(stats, "TOTAL_DAMAGE_DEALT_TO_CHAMPIONS"),
                PhysicalDamageToChampions = GetStatInt(stats, "PHYSICAL_DAMAGE_DEALT_TO_CHAMPIONS"),
                MagicDamageToChampions = GetStatInt(stats, "MAGIC_DAMAGE_DEALT_TO_CHAMPIONS"),
                TrueDamageToChampions = GetStatInt(stats, "TRUE_DAMAGE_DEALT_TO_CHAMPIONS"),
                TotalDamageTaken = GetStatInt(stats, "TOTAL_DAMAGE_TAKEN"),
                DamageSelfMitigated = GetStatInt(stats, "DAMAGE_SELF_MITIGATED"),
                LargestCriticalStrike = GetStatInt(stats, "LARGEST_CRITICAL_STRIKE"),
                GoldEarned = GetStatInt(stats, "GOLD_EARNED"),
                GoldSpent = GetStatInt(stats, "GOLD_SPENT"),
                TotalMinionsKilled = minions,
                NeutralMinionsKilled = jungle,
                CsTotal = csTotal,
                CsPerMin = Math.Round(csTotal / durationMin, 1),
                VisionScore = GetStatInt(stats, "VISION_SCORE"),
                WardsPlaced = GetStatInt(stats, "WARD_PLACED"),
                WardsKilled = GetStatInt(stats, "WARD_KILLED"),
                ControlWardsPurchased = GetStatInt(stats, "VISION_WARDS_BOUGHT_IN_GAME"),
                TurretKills = GetStatInt(stats, "TURRETS_KILLED"),
                InhibitorKills = GetStatInt(stats, "BARRACKS_KILLED"),
                TotalHeal = GetStatInt(stats, "TOTAL_HEAL"),
                TotalHealsOnTeammates = GetStatInt(stats, "TOTAL_HEAL_ON_TEAMMATES"),
                TotalTimeCcDealt = GetStatInt(stats, "TOTAL_TIME_CROWD_CONTROL_DEALT"),
                TimeCcingOthers = GetStatInt(stats, "TIME_CCING_OTHERS"),
                Spell1Casts = GetStatInt(stats, "SPELL1_CAST"),
                Spell2Casts = GetStatInt(stats, "SPELL2_CAST"),
                Spell3Casts = GetStatInt(stats, "SPELL3_CAST"),
                Spell4Casts = GetStatInt(stats, "SPELL4_CAST"),
                ChampLevel = GetStatInt(stats, "LEVEL"),
                TeamKills = teamKillsTotal,
                KillParticipation = Math.Round(kp, 1),
                Items = ExtractItems(stats, "ITEM"),
            };

            // Summoner name from EOG data
            gs.SummonerName = localPlayer.Value.GetPropertyOrDefault("summonerName", "Unknown");

            // Store enemy info in raw stats
            gs.RawStats["_enemy_champions"] = enemyChampions;
            gs.RawStats["_enemy_by_position"] = enemyByPosition;

            // Auto-detect lane opponent by matching positions
            if (!string.IsNullOrEmpty(myPosition) && enemyByPosition.TryGetValue(myPosition, out var laneOpponent))
            {
                gs.EnemyLaner = laneOpponent;
            }

            return gs;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to extract EOG stats");
            return null;
        }
    }

    /// <summary>
    /// Extract GameStats from an LCU match history entry.
    /// Match history entries use camelCase field names in the stats sub-object.
    /// </summary>
    public static GameStats? ExtractFromMatchHistory(
        JsonElement game,
        ILogger? logger = null,
        int? preferredParticipantId = null)
    {
        if (game.ValueKind != JsonValueKind.Object)
            return null;

        try
        {
            var gameId = game.GetPropertyLongOrDefault("gameId", 0);
            var gameCreation = game.GetPropertyLongOrDefault("gameCreation", 0);
            var gameDuration = game.GetPropertyIntOrDefault("gameDuration", 0);
            var gameMode = game.GetPropertyOrDefault("gameMode", "");
            var gameType = game.GetPropertyOrDefault("gameType", "");
            var queueId = game.GetPropertyIntOrDefault("queueId", 0);
            var queueLabel = GetQueueLabel(queueId);
            var displayMode = !string.IsNullOrWhiteSpace(queueLabel) ? queueLabel : gameMode;

            // Find the local player's participant data
            JsonElement? participant = null;

            if (game.TryGetProperty("participants", out var participants)
                && participants.ValueKind == JsonValueKind.Array)
            {
                var participantList = participants.EnumerateArray().ToList();

                if (participantList.Count == 1)
                {
                    participant = participantList[0];
                }

                if (participant is null && preferredParticipantId is int participantId && participantId > 0)
                {
                    foreach (var part in participantList)
                    {
                        if (part.GetPropertyIntOrDefault("participantId", -2) == participantId)
                        {
                            participant = part;
                            break;
                        }
                    }
                }

                if (participant is null
                    && game.TryGetProperty("participantIdentities", out var identities)
                    && identities.ValueKind == JsonValueKind.Array)
                {
                    // Find the current player's participantId
                    foreach (var pi in identities.EnumerateArray())
                    {
                        var playerInfo = pi.GetPropertyObjectOrDefault("player");
                        if (playerInfo is not null
                            && playerInfo.Value.GetPropertyBoolOrDefault("currentPlayer", false))
                        {
                            var pid = pi.GetPropertyIntOrDefault("participantId", -1);
                            foreach (var part in participantList)
                            {
                                if (part.GetPropertyIntOrDefault("participantId", -2) == pid)
                                {
                                    participant = part;
                                    break;
                                }
                            }

                            break;
                        }
                    }
                }

                // Fallback: use the first participant
                if (participant is null && participantList.Count > 0)
                {
                    participant = participantList[0];
                }
            }

            if (participant is null)
                return null;

            var p = participant.Value;
            var statsObj = p.GetPropertyObjectOrDefault("stats");
            if (statsObj is null)
                return null;

            var stats = statsObj.Value;
            var teamId = p.GetPropertyIntOrDefault("teamId", 100);

            var kills = stats.GetPropertyIntOrDefault("kills", 0);
            var deaths = stats.GetPropertyIntOrDefault("deaths", 0);
            var assists = stats.GetPropertyIntOrDefault("assists", 0);
            var minions = stats.GetPropertyIntOrDefault("totalMinionsKilled", 0);
            var jungle = stats.GetPropertyIntOrDefault("neutralMinionsKilled", 0);
            var csTotal = minions + jungle;
            var durationMin = Math.Max(gameDuration / 60.0, 1.0);

            // Compute team kills from all participants on same team
            var teamKillsTotal = 0;
            if (game.TryGetProperty("participants", out var allParticipants)
                && allParticipants.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in allParticipants.EnumerateArray())
                {
                    if (part.GetPropertyIntOrDefault("teamId", 0) == teamId)
                    {
                        var partStats = part.GetPropertyObjectOrDefault("stats");
                        if (partStats is not null)
                        {
                            teamKillsTotal += partStats.Value.GetPropertyIntOrDefault("kills", 0);
                        }
                    }
                }
            }

            var kda = (kills + assists) / Math.Max(deaths, 1.0);
            // Cap KP at 100% — teamKillsTotal can be 0 or under-counted in some game modes
            var kp = teamKillsTotal > 0
                ? Math.Min((kills + assists) / (double)teamKillsTotal * 100.0, 100.0)
                : 0.0;

            // Use game creation timestamp (milliseconds) converted to seconds
            var timestamp = gameCreation > 1_000_000_000_000L
                ? gameCreation / 1000
                : gameCreation;

            // Champion name: try participant first, then top-level game field
            var championName = p.GetPropertyOrDefault("championName", "");
            if (string.IsNullOrEmpty(championName))
            {
                championName = game.GetPropertyOrDefault("championName", "Unknown");
            }

            // Lane and role from timeline
            var lane = "";
            var role = "";
            var timeline = p.GetPropertyObjectOrDefault("timeline");
            if (timeline is not null)
            {
                lane = timeline.Value.GetPropertyOrDefault("lane", "");
                role = timeline.Value.GetPropertyOrDefault("role", "");
            }

            var gs = new GameStats
            {
                GameId = gameId,
                Timestamp = timestamp,
                GameDuration = gameDuration,
                GameMode = displayMode,
                GameType = gameType,
                QueueType = !string.IsNullOrWhiteSpace(queueLabel) ? queueLabel : queueId.ToString(),
                ChampionName = championName,
                ChampionId = p.GetPropertyIntOrDefault("championId", 0),
                TeamId = teamId,
                Position = lane,
                Role = role,
                Win = stats.GetPropertyBoolOrDefault("win", false),
                Kills = kills,
                Deaths = deaths,
                Assists = assists,
                KdaRatio = Math.Round(kda, 2),
                LargestKillingSpree = stats.GetPropertyIntOrDefault("largestKillingSpree", 0),
                LargestMultiKill = stats.GetPropertyIntOrDefault("largestMultiKill", 0),
                DoubleKills = stats.GetPropertyIntOrDefault("doubleKills", 0),
                TripleKills = stats.GetPropertyIntOrDefault("tripleKills", 0),
                QuadraKills = stats.GetPropertyIntOrDefault("quadraKills", 0),
                PentaKills = stats.GetPropertyIntOrDefault("pentaKills", 0),
                FirstBlood = stats.GetPropertyBoolOrDefault("firstBloodKill", false),
                TotalDamageDealt = stats.GetPropertyIntOrDefault("totalDamageDealt", 0),
                TotalDamageToChampions = stats.GetPropertyIntOrDefault("totalDamageDealtToChampions", 0),
                PhysicalDamageToChampions = stats.GetPropertyIntOrDefault("physicalDamageDealtToChampions", 0),
                MagicDamageToChampions = stats.GetPropertyIntOrDefault("magicDamageDealtToChampions", 0),
                TrueDamageToChampions = stats.GetPropertyIntOrDefault("trueDamageDealtToChampions", 0),
                TotalDamageTaken = stats.GetPropertyIntOrDefault("totalDamageTaken", 0),
                DamageSelfMitigated = stats.GetPropertyIntOrDefault("damageSelfMitigated", 0),
                LargestCriticalStrike = stats.GetPropertyIntOrDefault("largestCriticalStrike", 0),
                GoldEarned = stats.GetPropertyIntOrDefault("goldEarned", 0),
                GoldSpent = stats.GetPropertyIntOrDefault("goldSpent", 0),
                TotalMinionsKilled = minions,
                NeutralMinionsKilled = jungle,
                CsTotal = csTotal,
                CsPerMin = Math.Round(csTotal / durationMin, 1),
                VisionScore = stats.GetPropertyIntOrDefault("visionScore", 0),
                WardsPlaced = stats.GetPropertyIntOrDefault("wardsPlaced", 0),
                WardsKilled = stats.GetPropertyIntOrDefault("wardsKilled", 0),
                ControlWardsPurchased = stats.GetPropertyIntOrDefault("visionWardsBoughtInGame", 0),
                TurretKills = stats.GetPropertyIntOrDefault("turretKills", 0),
                InhibitorKills = stats.GetPropertyIntOrDefault("inhibitorKills", 0),
                TotalHeal = stats.GetPropertyIntOrDefault("totalHeal", 0),
                TotalHealsOnTeammates = stats.GetPropertyIntOrDefault("totalHealsOnTeammates", 0),
                TotalDamageShieldedOnTeammates = stats.GetPropertyIntOrDefault("totalDamageShieldedOnTeammates", 0),
                TotalTimeCcDealt = stats.GetPropertyIntOrDefault("totalTimeCrowdControlDealt", 0),
                TimeCcingOthers = stats.GetPropertyIntOrDefault("timeCCingOthers", 0),
                Spell1Casts = stats.GetPropertyIntOrDefault("spell1Casts", 0),
                Spell2Casts = stats.GetPropertyIntOrDefault("spell2Casts", 0),
                Spell3Casts = stats.GetPropertyIntOrDefault("spell3Casts", 0),
                Spell4Casts = stats.GetPropertyIntOrDefault("spell4Casts", 0),
                Summoner1Id = p.GetPropertyIntOrDefault("spell1Id", 0),
                Summoner2Id = p.GetPropertyIntOrDefault("spell2Id", 0),
                ChampLevel = stats.GetPropertyIntOrDefault("champLevel", 0),
                TeamKills = teamKillsTotal,
                KillParticipation = Math.Round(kp, 1),
                Items = ExtractItems(stats, "item"),
            };

            return gs;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to extract match history stats");
            return null;
        }
    }

    // ── Private helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Read an integer stat from the stats block, handling both string and numeric JSON values.
    /// The EOG stats block often has numeric values as strings.
    /// </summary>
    private static int GetStatInt(JsonElement stats, string key)
    {
        if (!stats.TryGetProperty(key, out var prop))
            return 0;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var intVal))
            return intVal;

        if (prop.ValueKind == JsonValueKind.String)
        {
            var str = prop.GetString();
            if (int.TryParse(str, out var parsed))
                return parsed;
        }

        return 0;
    }

    /// <summary>
    /// Extract item IDs from ITEM0-ITEM6 (EOG) or item0-item6 (match history).
    /// </summary>
    private static List<int> ExtractItems(JsonElement stats, string prefix)
    {
        var items = new List<int>();
        for (var i = 0; i < 7; i++)
        {
            var itemId = GetStatInt(stats, $"{prefix}{i}");
            if (itemId != 0)
            {
                items.Add(itemId);
            }
        }

        return items;
    }

    private static string GetQueueLabel(int queueId) => queueId switch
    {
        420 => "Ranked Solo",
        440 => "Ranked Flex",
        400 => "Normal Draft",
        430 => "Normal Blind",
        490 => "Quickplay",
        _ => "",
    };
}

/// <summary>
/// Additional extension methods for StatsExtractor JSON parsing.
/// </summary>
internal static class StatsJsonExtensions
{
    public static JsonElement? GetPropertyObjectOrDefault(this JsonElement el, string property)
    {
        if (el.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.Object)
            return prop;
        return null;
    }

    public static long GetPropertyLongOrDefault(this JsonElement el, string property, long defaultValue)
    {
        if (el.TryGetProperty(property, out var prop))
        {
            if (prop.TryGetInt64(out var value))
                return value;

            if (prop.ValueKind == JsonValueKind.String &&
                long.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }
}
