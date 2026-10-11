using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using static Revu.Core.Tests.RemakeTestKit;

namespace Revu.Core.Tests;

/// <summary>
/// 3.14.1: the startup sweep and the post-game confirmation. The client's match history
/// answers first, Match-V5 second; a negative verdict is recorded too. When neither source
/// has the flag, nothing is written unless both were asked (then an unknown row ends the
/// retries). Only the player's own participant is ever read.
/// </summary>
public sealed class RemakeCheckServiceTests
{
    private sealed class FakeMatchClient : IRiotMatchClient
    {
        public Dictionary<string, JsonElement> Matches { get; } = [];
        public List<string> Requested { get; } = [];

        public Task<JsonElement?> GetMatchAsync(string matchId, string region, CancellationToken ct = default)
        {
            Requested.Add(matchId);
            return Task.FromResult<JsonElement?>(Matches.TryGetValue(matchId, out var m) ? m : null);
        }

        public Task<JsonElement?> GetTimelineAsync(string matchId, string region, CancellationToken ct = default) =>
            Task.FromResult<JsonElement?>(null);
    }

    private static JsonElement MatchV5(bool? flag) => Json(flag is bool b
        ? $$"""{ "info": { "participants": [ { "puuid": "self-puuid", "teamPosition": "", "gameEndedInEarlySurrender": {{(b ? "true" : "false")}} } ] } }"""
        : """{ "info": { "participants": [ { "puuid": "self-puuid", "teamPosition": "MIDDLE" } ] } }""");

    private static (RemakeCheckService Service, StubLcuClient Lcu, FakeMatchClient Riot, List<TimeSpan> Delays) Build(
        TestDatabaseScope scope, bool signedIn = true, bool lcuConnected = true)
    {
        var config = new TestConfigService(new AppConfig
        {
            RiotRegion = "na1",
            RiotPuuid = "self-puuid",
            RiotSessionToken = signedIn ? "token" : "",
            RiotSessionExpiresAt = signedIn ? DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() : 0,
        });
        var lcu = new StubLcuClient { Connected = lcuConnected, SummonerPuuid = LcuSelf };
        var riot = new FakeMatchClient();
        var delays = new List<TimeSpan>();
        var service = new RemakeCheckService(scope.Games, lcu, riot, config, NullLogger<RemakeCheckService>.Instance,
            (span, _) => { delays.Add(span); return Task.CompletedTask; });
        return (service, lcu, riot, delays);
    }

    private const string LcuSelf = "lcu-self-uuid";

    private static async Task SeedShortGamesAsync(TestDatabaseScope scope, params long[] ids)
    {
        using var conn = scope.OpenConnection();
        var at = DateTimeOffset.Now.AddHours(-ids.Length);
        foreach (var id in ids)
        {
            at = at.AddMinutes(30);
            await InsertGameAsync(conn, id, win: false, at, duration: 386);
        }
    }

    [Fact]
    public async Task Backfill_UsesTheClientFirst()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedShortGamesAsync(scope, 8001);
        var (service, lcu, riot, _) = Build(scope);
        lcu.Details[8001] = StubLcuClient.Game(8001, 386, earlySurrender: true, selfPuuid: LcuSelf);
        riot.Matches["NA1_8001"] = MatchV5(false);

        var result = await service.RunBackfillAsync();

        Assert.Equal((1, 1, 0), (result.Checked, result.Marked, result.Unknown));
        Assert.Empty(riot.Requested);
        var verdict = await scope.Games.GetRemakeVerdictAsync(8001);
        Assert.True(verdict!.IsRemake);
        Assert.Equal("lcu", verdict.Source);
        Assert.Equal(new RemakeChange(8001, true), Assert.Single(result.Changes));
    }

    [Fact]
    public async Task Backfill_FallsBackToMatchV5_Throttled_AndRecordsNegatives()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedShortGamesAsync(scope, 8101, 8102, 8103);
        var (service, lcu, riot, delays) = Build(scope);
        lcu.Details[8101] = Json("""{ "participants": [ { "stats": { "kills": 0 } } ] }""");
        riot.Matches["NA1_8101"] = MatchV5(true);
        riot.Matches["NA1_8102"] = MatchV5(false);

        var result = await service.RunBackfillAsync();

        Assert.Equal((3, 1, 1), (result.Checked, result.Marked, result.Unknown));
        Assert.Equal(3, riot.Requested.Count);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromMilliseconds(600), d));
        Assert.Equal("matchv5", (await scope.Games.GetRemakeVerdictAsync(8101))!.Source);
        var negative = await scope.Games.GetRemakeVerdictAsync(8102);
        Assert.False(negative!.IsRemake);
        // Both sources asked and silent: an unknown row (never a remake) ends the retries.
        var unknown = await scope.Games.GetRemakeVerdictAsync(8103);
        Assert.False(unknown!.IsRemake);
        Assert.Equal("unknown", unknown.Source);
        Assert.Empty(await scope.Games.GetRemakeCandidatesAsync(600, 25));
        Assert.Equal(0, await service.PendingCountAsync());
        // A real verdict still replaces it later.
        Assert.Equal(new RemakeWriteResult(true, true), await MarkAsync(scope, 8103, true, "matchv5"));
    }

    [Fact]
    public async Task Backfill_SignedOut_SilentClient_LeavesThePendingGameAndTakesNoBackup()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedShortGamesAsync(scope, 8151);
        var (service, lcu, riot, _) = Build(scope, signedIn: false);
        var backups = 0;

        var result = await service.RunBackfillAsync(beforeFirstWrite: () => { backups++; return Task.CompletedTask; });

        Assert.Equal((1, 0, 1), (result.Checked, result.Marked, result.Unknown));
        Assert.Equal(new long[] { 8151 }, lcu.DetailCalls);
        Assert.Empty(riot.Requested);
        Assert.Equal(0, backups);
        Assert.Null(await scope.Games.GetRemakeVerdictAsync(8151));
        Assert.Equal(1, await service.PendingCountAsync());
    }

    [Fact]
    public async Task Backfill_BacksUpOnceRightBeforeTheFirstWrite()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedShortGamesAsync(scope, 8161, 8162);
        var (service, lcu, _, _) = Build(scope, signedIn: false);
        lcu.Details[8161] = StubLcuClient.Game(8161, 386, earlySurrender: true, selfPuuid: LcuSelf);
        lcu.Details[8162] = StubLcuClient.Game(8162, 386, earlySurrender: false, selfPuuid: LcuSelf);
        var rowsAtBackup = new List<long>();

        await service.RunBackfillAsync(beforeFirstWrite: async () =>
            rowsAtBackup.Add(await CountAsync(scope, "SELECT COUNT(*) FROM game_remakes")));

        Assert.Equal(new long[] { 0 }, rowsAtBackup);
        Assert.Equal(2, await CountAsync(scope, "SELECT COUNT(*) FROM game_remakes"));
    }

    [Fact]
    public async Task Backfill_IgnoresAMatchWithoutThePlayer()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedShortGamesAsync(scope, 8171);
        var (service, lcu, riot, _) = Build(scope);
        // A stranger's game fetched by id: the client names someone else, Match-V5 too.
        lcu.Details[8171] = StubLcuClient.Game(8171, 386, earlySurrender: true, selfPuuid: "someone-else");
        riot.Matches["NA1_8171"] = Json("""
            { "info": { "participants": [ { "puuid": "someone-else", "gameEndedInEarlySurrender": true } ] } }
            """);

        var result = await service.RunBackfillAsync();

        Assert.Equal(0, result.Marked);
        Assert.Empty(result.Changes);
        Assert.False((await scope.Games.GetRemakeVerdictAsync(8171))!.IsRemake);
        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM game_remakes WHERE is_remake = 1"));
    }

    [Fact]
    public async Task Backfill_NoSessionAndNoClient_MakesNoCallsAndNoWrites()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedShortGamesAsync(scope, 8201);
        var (service, lcu, riot, _) = Build(scope, signedIn: false, lcuConnected: false);

        var result = await service.RunBackfillAsync();

        Assert.Equal((0, 0, 1), (result.Checked, result.Marked, result.Unknown));
        Assert.False(result.LcuConnected);
        Assert.Empty(lcu.DetailCalls);
        Assert.Empty(riot.Requested);
        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM game_remakes"));
    }

    [Fact]
    public async Task Confirm_RecordsMatchV5_SkipsUserAndMatchV5Verdicts()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var (service, _, riot, _) = Build(scope);
        riot.Matches["NA1_8301"] = MatchV5(true);
        riot.Matches["NA1_8302"] = MatchV5(true);
        riot.Matches["NA1_8303"] = MatchV5(true);
        await MarkAsync(scope, 8301, false, "eog");
        await MarkAsync(scope, 8302, false, "user");
        await MarkAsync(scope, 8303, false, "matchv5");

        var corrected = await service.ConfirmAsync(8301, 386);
        Assert.Equal(new RemakeWriteResult(true, true), corrected);
        Assert.True((await scope.Games.GetRemakeVerdictAsync(8301))!.IsRemake);

        Assert.Null(await service.ConfirmAsync(8302, 386));
        Assert.Null(await service.ConfirmAsync(8303, 386));
        Assert.Equal(new[] { "NA1_8301" }, riot.Requested);
        Assert.False((await scope.Games.GetRemakeVerdictAsync(8302))!.IsRemake);
    }

    [Fact]
    public async Task Confirm_SignedOut_UsesTheConnectedClient()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var (service, lcu, riot, _) = Build(scope, signedIn: false);
        lcu.Details[8351] = StubLcuClient.Game(8351, 386, earlySurrender: true, selfPuuid: LcuSelf);

        Assert.Equal(new RemakeWriteResult(true, true), await service.ConfirmAsync(8351, 386));

        var verdict = await scope.Games.GetRemakeVerdictAsync(8351);
        Assert.True(verdict!.IsRemake);
        Assert.Equal("lcu", verdict.Source);
        Assert.Empty(riot.Requested);
    }

    [Fact]
    public async Task Confirm_LongGameWithACaptureVerdict_IsNotLookedUp_SignedOutNeither()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var (service, _, riot, _) = Build(scope);
        await MarkAsync(scope, 8401, false, "eog");
        Assert.Null(await service.ConfirmAsync(8401, 1800));

        var (signedOut, _, signedOutRiot, _) = Build(scope, signedIn: false);
        Assert.Null(await signedOut.ConfirmAsync(8402, 386));
        Assert.Empty(riot.Requested);
        Assert.Empty(signedOutRiot.Requested);

        // Unknown upstream: nothing written.
        Assert.Null(await service.ConfirmAsync(8403, 1800));
        Assert.Null(await scope.Games.GetRemakeVerdictAsync(8403));
    }
}
