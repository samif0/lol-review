using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// v3.9.2: the single-game Match-V5 lookup the matchup journal's "New card from
/// last game" runs when a recovered game's row lacks the opponents. Pins that
/// it writes the same two columns the bulk sweep does, that the journal can
/// pre-fill the full 2v2 afterwards, and the not-configured / failed outcomes.
/// </summary>
public sealed class EnemyLanerBackfillServiceTests
{
    private const string SelfPuuid = "self-puuid";

    /// <summary>Match-V5 shape: info.participants[] with puuid / teamId / teamPosition / championName.</summary>
    private static JsonElement MatchV5(params (string Puuid, int Team, string Position, string Champ)[] rows)
    {
        var participants = string.Join(",", rows.Select(r =>
            $$$"""{"puuid":"{{{r.Puuid}}}","teamId":{{{r.Team}}},"teamPosition":"{{{r.Position}}}","championName":"{{{r.Champ}}}"}"""));
        return JsonDocument.Parse($$$"""{"info":{"participants":[{{{participants}}}]}}""").RootElement;
    }

    private sealed class FakeMatchClient : IRiotMatchClient
    {
        public JsonElement? Match { get; set; }
        public List<string> Requested { get; } = [];

        public Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default)
        {
            Requested.Add($"{region}:{matchId}");
            return Task.FromResult(Match);
        }

        public Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default) =>
            Task.FromResult<JsonElement?>(null);
    }

    private static (EnemyLanerBackfillService Service, FakeMatchClient Client) Build(TestDatabaseScope scope, string region = "na1", string puuid = SelfPuuid)
    {
        var config = new TestConfigService(new AppConfig { RiotRegion = region, RiotPuuid = puuid });
        var client = new FakeMatchClient();
        var service = new EnemyLanerBackfillService(scope.Games, client, config, NullLogger<EnemyLanerBackfillService>.Instance);
        return (service, client);
    }

    /// <summary>A bot-lane game as a pre-3.9.2 build recovered it: position, no map, no opponent.</summary>
    private static async Task<long> SeedRecoveredBotGameAsync(TestDatabaseScope scope, long gameId)
    {
        var game = TestGameStatsFactory.Create(gameId, champion: "Miss Fortune");
        game.Position = "BOTTOM";
        game.ParticipantMap = "";
        game.EnemyLaner = "";
        await scope.Games.SaveAsync(game);
        return gameId;
    }

    [Fact]
    public async Task BackfillGameAsync_WritesTheOpponentAndTheMap_SoTheJournalCanPrefillTheFull2v2()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedRecoveredBotGameAsync(scope, 9101);
        var (service, client) = Build(scope);
        client.Match = MatchV5(
            (SelfPuuid, 100, "BOTTOM", "MissFortune"), ("p2", 100, "UTILITY", "Milio"),
            ("p3", 200, "BOTTOM", "Seraphine"), ("p4", 200, "UTILITY", "Maokai"),
            ("p5", 100, "MIDDLE", "Ahri"), ("p6", 200, "MIDDLE", "Syndra"));

        Assert.Null(MatchupPrefill.FromGame(await scope.Games.GetAsync(gameId))!.EnemyChamps.FirstOrDefault());
        var outcome = await service.BackfillGameAsync(gameId);

        Assert.Equal(EnemyLanerBackfillOutcome.Updated, outcome);
        Assert.Equal(new[] { "na1:NA1_9101" }, client.Requested);
        var game = await scope.Games.GetAsync(gameId);
        Assert.Equal("Seraphine", game!.EnemyLaner);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(game.ParticipantMap)!;
        Assert.Equal("Milio", map["ownSupp"]);
        Assert.Equal("Maokai", map["enemySupp"]);

        var prefill = MatchupPrefill.FromGame(game);
        Assert.NotNull(prefill);
        Assert.True(prefill!.IsComplete);
        Assert.Equal(new[] { "Miss Fortune", "Milio" }, prefill.AllyChamps);
        Assert.Equal(new[] { "Seraphine", "Maokai" }, prefill.EnemyChamps);
    }

    [Fact]
    public async Task BackfillGameAsync_NotConfigured_TouchesNothing()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedRecoveredBotGameAsync(scope, 9102);
        var (service, client) = Build(scope, region: "", puuid: "");

        Assert.Equal(EnemyLanerBackfillOutcome.NotConfigured, await service.BackfillGameAsync(gameId));
        Assert.Empty(client.Requested);
        Assert.Equal("", (await scope.Games.GetAsync(gameId))!.EnemyLaner);
    }

    [Fact]
    public async Task BackfillGameAsync_LookupFailure_IsReported_AndTouchesNothing()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedRecoveredBotGameAsync(scope, 9103);
        var (service, client) = Build(scope);
        client.Match = null;

        Assert.Equal(EnemyLanerBackfillOutcome.Failed, await service.BackfillGameAsync(gameId));
        var game = await scope.Games.GetAsync(gameId);
        Assert.Equal("", game!.EnemyLaner);
        Assert.Equal("", game.ParticipantMap);
    }

    [Fact]
    public async Task BackfillGameAsync_MatchWithoutPositions_IsSkipped()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var gameId = await SeedRecoveredBotGameAsync(scope, 9104);
        var (service, client) = Build(scope);
        // ARAM-shaped: Match-V5 carries empty teamPosition strings.
        client.Match = MatchV5((SelfPuuid, 100, "", "MissFortune"), ("p2", 200, "", "Seraphine"));

        Assert.Equal(EnemyLanerBackfillOutcome.Skipped, await service.BackfillGameAsync(gameId));
        Assert.Equal("", (await scope.Games.GetAsync(gameId))!.EnemyLaner);
    }
}
