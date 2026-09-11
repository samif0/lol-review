using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// v3.10.1: end-to-end contract for what the game-flow coordinator does with a
/// just-ended game's matchup. (1) The capture leaves the matchup on the row
/// immediately — from the live roster, else the champ-select snapshot, else
/// role priors, each marked with how sure it is — so the Review hero, the
/// dashboard card and the Matchups journal have it the moment the game ends.
/// (2) The +90s pass then confirms the row from Match-V5 (the exact call the
/// coordinator makes: <see cref="EnemyLanerBackfillService.BackfillGameAsync"/>
/// on the fresh game), correcting an estimate and taking it out of the queue.
/// Real repositories against a temp DB; only the Riot HTTP hop is stubbed.
/// </summary>
public sealed class MatchupPostGamePipelineTests
{
    private const string SelfPuuid = "self-puuid";
    private const long GameId = 5_000_000_101; // synthetic, shaped like a real NA game id

    private static readonly string[] Own = ["Teemo", "Zaahen", "Riven", "Miss Fortune", "Pantheon"];
    private static readonly string[] Enemy = ["Gragas", "Hecarim", "Swain", "Yasuo", "Soraka"];
    private static readonly string[] Lanes = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY"];

    private sealed class StubMatchClient : IRiotMatchClient
    {
        public JsonElement? Match;
        public int MatchCalls;

        public Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default)
        {
            MatchCalls++;
            return Task.FromResult(Match);
        }

        public Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default) =>
            Task.FromResult<JsonElement?>(null);
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>The LCU end-of-game payload as the client has emitted it since 2026-08: no positions anywhere.</summary>
    private static JsonElement EogWithoutPositions()
    {
        static string Players(string[] team) => string.Join(",", team.Select(c =>
            $$$"""{ "championName": "{{{c}}}", "selectedPosition": "", "detectedTeamPosition": "", "stats": { "CHAMPIONS_KILLED": 1 } }"""));
        return Parse($$$"""
            {
              "gameId": {{{GameId}}}, "gameLength": 1104, "gameMode": "CLASSIC",
              "queueType": "RANKED_SOLO_5x5", "gameType": "MATCHED_GAME",
              "localPlayer": {
                "teamId": 100, "championName": "Miss Fortune", "championId": 21, "puuid": "{{{SelfPuuid}}}",
                "selectedPosition": "", "detectedTeamPosition": "",
                "stats": { "CHAMPIONS_KILLED": "7", "NUM_DEATHS": "2", "ASSISTS": "9", "WIN": "1" }
              },
              "teams": [
                { "teamId": 100, "stats": { "CHAMPIONS_KILLED": 20 }, "players": [ {{{Players(Own)}}} ] },
                { "teamId": 200, "stats": { "CHAMPIONS_KILLED": 12 }, "players": [ {{{Players(Enemy)}}} ] }
              ]
            }
            """);
    }

    private static LiveRoster Roster()
    {
        var rows = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            rows.Add($$$"""{ "championName": "{{{Own[i]}}}", "position": "{{{Lanes[i]}}}", "team": "ORDER" }""");
            rows.Add($$$"""{ "championName": "{{{Enemy[i]}}}", "position": "{{{Lanes[i]}}}", "team": "CHAOS" }""");
        }
        return LiveRoster.Parse(Parse("[" + string.Join(",", rows) + "]"))!;
    }

    private static JsonElement MatchV5()
    {
        var rows = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var puuid = Own[i] == "Miss Fortune" ? SelfPuuid : $"own{i}";
            rows.Add($$$"""{"puuid":"{{{puuid}}}","teamId":100,"teamPosition":"{{{Lanes[i]}}}","championName":"{{{Own[i].Replace(" ", "")}}}"}""");
            rows.Add($$$"""{"puuid":"enemy{{{i}}}","teamId":200,"teamPosition":"{{{Lanes[i]}}}","championName":"{{{Enemy[i]}}}"}""");
        }
        return Parse($$$"""{"info":{"participants":[{{{string.Join(",", rows)}}}]}}""");
    }

    private static (EnemyLanerBackfillService Service, StubMatchClient Client) Backfill(SidecarWriteScope scope)
    {
        var client = new StubMatchClient { Match = MatchV5() };
        var config = new TestConfigService(new AppConfig { RiotRegion = "na1", RiotPuuid = SelfPuuid });
        return (new EnemyLanerBackfillService(scope.Games, client, config, NullLogger<EnemyLanerBackfillService>.Instance), client);
    }

    private static string Headline(GameStats g) =>
        MatchupDisplay.Build(g.ChampionName, g.EnemyLaner, g.Position, g.ParticipantMap);

    /// <summary>The live path: roster during the game → the row is complete at save time.</summary>
    [Fact]
    public async Task LiveRoster_PutsTheWholeMatchupOnTheRowAtSaveTime_AndMatchV5OnlyConfirms()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var stats = StatsExtractor.ExtractFromEog(EogWithoutPositions(), NullLogger.Instance, Roster())!;
        await scope.Games.SaveAsync(stats);

        // What every page reads right after the gameEnded event.
        var row = (await scope.Games.GetAsync(GameId))!;
        Assert.Equal("BOTTOM", row.Position);
        Assert.Equal("Yasuo", row.EnemyLaner);
        Assert.Equal(MatchupSources.Live, row.MatchupSource);
        Assert.Equal("Miss Fortune+Pantheon vs Yasuo+Soraka", Headline(row));
        Assert.Equal(5, MatchupDisplay.LobbyRows(row.Position, row.ParticipantMap).Count);
        Assert.True(MatchupPrefill.FromGame(row)!.CanCreateOutright);
        Assert.DoesNotContain(GameId, await scope.Games.GetGameIdsMissingEnemyLanerAsync());

        // The +90s pass: same answer, now stamped as Riot's.
        var (service, client) = Backfill(scope);
        Assert.Equal(EnemyLanerBackfillOutcome.Updated, await service.BackfillGameAsync(GameId));
        var confirmed = (await scope.Games.GetAsync(GameId))!;
        Assert.Equal("Yasuo", confirmed.EnemyLaner);
        Assert.Equal(MatchupSources.MatchV5, confirmed.MatchupSource);
        Assert.Equal("Miss Fortune+Pantheon vs Yasuo+Soraka", Headline(confirmed));
        Assert.Equal(1, client.MatchCalls);
    }

    /// <summary>No roster (the app missed the game's live window): champ select fills it, Match-V5 confirms.</summary>
    [Fact]
    public async Task ChampSelectFallback_FillsTheRowAtSaveTime_AndMatchV5Confirms()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var stats = StatsExtractor.ExtractFromEog(EogWithoutPositions(), NullLogger.Instance)!;
        Assert.Equal("", stats.Position);
        var champSelectMap = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownTop"] = "Teemo", ["ownJg"] = "Zaahen", ["ownMid"] = "Riven", ["ownBot"] = "Miss Fortune", ["ownSupp"] = "Pantheon",
            ["enemyTop"] = "Gragas", ["enemyJg"] = "Hecarim", ["enemyMid"] = "Swain", ["enemyBot"] = "Yasuo", ["enemySupp"] = "Soraka",
        });

        // The coordinator's fallback order.
        Assert.True(MatchupFallback.ApplyChampSelect(stats, "BOTTOM", champSelectMap));
        Assert.False(MatchupFallback.ApplyRolePriors(stats)); // nothing left blank
        await scope.Games.SaveAsync(stats);

        var row = (await scope.Games.GetAsync(GameId))!;
        Assert.Equal("Miss Fortune+Pantheon vs Yasuo+Soraka", Headline(row));
        Assert.Equal(MatchupSources.ChampSelect, row.MatchupSource);
        Assert.Contains(GameId, await scope.Games.GetGameIdsMissingEnemyLanerAsync()); // still to confirm

        var (service, _) = Backfill(scope);
        Assert.Equal(EnemyLanerBackfillOutcome.Updated, await service.BackfillGameAsync(GameId));
        var confirmed = (await scope.Games.GetAsync(GameId))!;
        Assert.Equal(MatchupSources.MatchV5, confirmed.MatchupSource);
        Assert.DoesNotContain(GameId, await scope.Games.GetGameIdsMissingEnemyLanerAsync());
    }

    /// <summary>Nothing but the champion lists: role priors estimate it; Match-V5 corrects a wrong guess.</summary>
    [Fact]
    public async Task RolePriorFallback_EstimatesTheRowAtSaveTime_AndMatchV5CorrectsIt()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var stats = StatsExtractor.ExtractFromEog(EogWithoutPositions(), NullLogger.Instance)!;

        Assert.True(MatchupFallback.ApplyRolePriors(stats));
        Assert.Equal("BOTTOM", stats.Position);
        Assert.Equal(MatchupSources.Heuristic, stats.MatchupSource);
        stats.EnemyLaner = "Swain"; // simulate a wrong estimate
        await scope.Games.SaveAsync(stats);

        var row = (await scope.Games.GetAsync(GameId))!;
        Assert.Equal("Miss Fortune", row.ChampionName);
        Assert.NotEqual(row.ChampionName, Headline(row)); // the hero shows a matchup, not a bare name
        Assert.Contains(GameId, await scope.Games.GetGameIdsMissingEnemyLanerAsync());

        var (service, _) = Backfill(scope);
        Assert.Equal(EnemyLanerBackfillOutcome.Updated, await service.BackfillGameAsync(GameId));
        var confirmed = (await scope.Games.GetAsync(GameId))!;
        Assert.Equal("Yasuo", confirmed.EnemyLaner);
        Assert.Equal(MatchupSources.MatchV5, confirmed.MatchupSource);
        Assert.Equal("Miss Fortune+Pantheon vs Yasuo+Soraka", Headline(confirmed));
    }

    /// <summary>The Settings button / startup heal sweep: only rows still waiting are fetched.</summary>
    [Fact]
    public async Task Sweep_FetchesEstimatesAndBlanks_NotConfirmedRows()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var live = StatsExtractor.ExtractFromEog(EogWithoutPositions(), NullLogger.Instance, Roster())!;
        await scope.Games.SaveAsync(live);

        var estimated = TestGameStatsFactory.Create(GameId - 1, champion: "Miss Fortune");
        estimated.Position = "BOTTOM";
        estimated.EnemyLaner = "Swain";
        estimated.ParticipantMap = """{"ownBot":"Miss Fortune","enemyBot":"Swain"}""";
        estimated.MatchupSource = MatchupSources.Heuristic;
        estimated.Puuid = SelfPuuid;
        await scope.Games.SaveAsync(estimated);

        var blank = TestGameStatsFactory.Create(GameId - 2, champion: "Miss Fortune");
        blank.Position = "";
        blank.EnemyLaner = "";
        blank.ParticipantMap = "";
        blank.Puuid = SelfPuuid;
        await scope.Games.SaveAsync(blank);

        var (service, client) = Backfill(scope);
        var result = await service.RunAsync(maxGames: 10);

        Assert.Equal(2, result.Scanned);
        Assert.Equal(2, result.Updated);
        Assert.Equal(2, client.MatchCalls);
        Assert.Equal(MatchupSources.Live, (await scope.Games.GetAsync(GameId))!.MatchupSource);
        Assert.Equal(MatchupSources.MatchV5, (await scope.Games.GetAsync(GameId - 1))!.MatchupSource);
        Assert.Equal(MatchupSources.MatchV5, (await scope.Games.GetAsync(GameId - 2))!.MatchupSource);
        Assert.Empty(await scope.Games.GetGameIdsMissingEnemyLanerAsync());
    }
}
