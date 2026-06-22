using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

/// <summary>
/// GameService stamps the SIGNED-IN Riot PUUID on every captured game before the
/// row is saved. The LCU end-of-game localPlayer.puuid is a UUID-shaped id that
/// does NOT equal the encrypted Riot PUUID our login stores in config; account
/// scoping string-compares the two, so an unreconciled LCU id strands the game
/// behind the dashboard's CurrentAccountFilter ("GAMES 0 even though I played").
/// These tests pin the reconciliation so that regression can't return silently.
/// </summary>
public sealed class GameServicePuuidReconciliationTests
{
    private const string LcuPuuid = "07f45763-5e2d-5b2b-9fef-6f410a1e98c4"; // UUID-shaped (LCU)
    private const string RiotPuuid = "MI8rRotPr5sxilCFBJkVD4ih2-real-encrypted-puuid"; // config

    private static GameService BuildService(TestDatabaseScope scope, string? configPuuid)
    {
        var config = new TestConfigService(new AppConfig { RiotPuuid = configPuuid ?? "" });
        var rules = new RulesRepository(scope.ConnectionFactory);
        return new GameService(
            scope.Games,
            scope.SessionLog,
            rules,
            scope.GameEvents,
            scope.DerivedEvents,
            new StubVodService(),
            config,
            NullLogger<GameService>.Instance);
    }

    private static GameStats RankedGame(long id, string puuid) =>
        new()
        {
            GameId = id,
            Timestamp = id,
            GameDuration = 1800,
            GameMode = "Ranked Solo",
            GameType = "MATCHED_GAME",
            QueueType = "Ranked Solo/Duo",
            SummonerName = "Tester",
            ChampionName = "Ahri",
            ChampionId = 103,
            TeamId = 100,
            Position = "MIDDLE",
            Win = true,
            Kills = 8,
            Deaths = 3,
            Assists = 6,
            Puuid = puuid,
        };

    [Fact]
    public async Task ProcessGameEnd_StampsConfigRiotPuuid_OverLcuPuuid()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = BuildService(scope, RiotPuuid);

        var gameId = await svc.ProcessGameEndAsync(new ProcessGameEndRequest(RankedGame(8001, LcuPuuid)));

        Assert.NotNull(gameId);
        var saved = await scope.Games.GetAsync(gameId!.Value);
        Assert.NotNull(saved);
        Assert.Equal(RiotPuuid, saved!.Puuid);
    }

    [Fact]
    public async Task ProcessGameEnd_LoggedOut_KeepsLcuPuuid()
    {
        // No Riot PUUID configured (logged out): leave whatever the LCU gave —
        // the lenient '' scope still treats it as the user's own history.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = BuildService(scope, configPuuid: "");

        var gameId = await svc.ProcessGameEndAsync(new ProcessGameEndRequest(RankedGame(8002, LcuPuuid)));

        Assert.NotNull(gameId);
        var saved = await scope.Games.GetAsync(gameId!.Value);
        Assert.Equal(LcuPuuid, saved!.Puuid);
    }

    [Fact]
    public async Task ProcessGameEnd_AlreadyRiotPuuid_Unchanged()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = BuildService(scope, RiotPuuid);

        var gameId = await svc.ProcessGameEndAsync(new ProcessGameEndRequest(RankedGame(8003, RiotPuuid)));

        Assert.NotNull(gameId);
        var saved = await scope.Games.GetAsync(gameId!.Value);
        Assert.Equal(RiotPuuid, saved!.Puuid);
    }
}
