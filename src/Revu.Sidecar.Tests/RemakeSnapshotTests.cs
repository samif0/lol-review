using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// 3.14.1: a remake (League early surrender) through the snapshot builders the pages
/// read. It is labelled "Remake" wherever it is listed, and never counted: no rules
/// banner, no dashboard games, no review queue. The player's toggle is sticky.
/// </summary>
public sealed class RemakeSnapshotTests
{
    private static long Today(int offset) =>
        Math.Max(DateTimeOffset.Now.ToUnixTimeSeconds() - 600, new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds()) + offset;

    /// <summary>Three games today; the third is a remake. Each has its session_log row.</summary>
    private static async Task SeedDayAsync(SidecarWriteScope scope)
    {
        foreach (var (id, offset) in new[] { (7001L, 0), (7002L, 1), (7003L, 2) })
        {
            await scope.SeedGameAsync(gameId: id, win: false, timestamp: Today(offset),
                durationSeconds: id == 7003 ? 386 : 1800);
            await scope.SessionLog.LogGameAsync(id, "Ahri", false);
        }
        await scope.Games.SetRemakeVerdictAsync(7003, true, false, "eog");
    }

    private static ReviewSnapshotBuilder ReviewBuilder(SidecarWriteScope scope) => new(
        scope.Games, scope.Games, scope.Objectives, scope.Prompts, scope.SessionLog,
        scope.Evidence, new GameEventsRepository(scope.ConnectionFactory),
        scope.DeathClassifications, scope.MatchupNotes, scope.ConceptTags, scope.Vod,
        scope.Config, scope.ReviewDrafts, NullLogger<ReviewSnapshotBuilder>.Instance);

    [Fact]
    public async Task RulesSnapshot_NoBannerWhenTheRemakeIsTheThirdGame()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var rules = new RulesRepository(scope.ConnectionFactory);
        await rules.CreateAsync("Three games", ruleType: "max_games", conditionValue: "3");
        await SeedDayAsync(scope);

        var builder = new RulesSnapshotBuilder(rules, scope.Games, new HardStopsRepository(scope.ConnectionFactory),
            NullLogger<RulesSnapshotBuilder>.Instance);
        var dto = await builder.BuildAsync();

        Assert.False(dto.HasViolations);
        Assert.Empty(dto.Violations);

        await scope.SeedGameAsync(gameId: 7004, timestamp: Today(3));
        Assert.True((await builder.BuildAsync()).HasViolations);
    }

    [Fact]
    public async Task Dashboard_TodayAndInboxExcludeTheRemake()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await SeedDayAsync(scope);

        var dto = await new DashboardSnapshotBuilder(
            scope.SessionLog, scope.Objectives, scope.Vod, scope.Evidence, scope.DeathClassifications,
            new RulesRepository(scope.ConnectionFactory), scope.Games, scope.CoachingStints, scope.Config,
            NullLogger<DashboardSnapshotBuilder>.Instance).BuildAsync();

        Assert.Equal(2, dto.Stats.TotalGames);
        Assert.DoesNotContain(dto.Unreviewed.Items, i => i.GameId == 7003);
        Assert.Contains(dto.Unreviewed.Items, i => i.GameId == 7001);
    }

    [Fact]
    public async Task Games_TodayAndHistoryLabelTheRemake_QueueDropsIt()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await SeedDayAsync(scope);
        var builder = new GamesSnapshotBuilder(scope.Games, scope.Vod, scope.Objectives, scope.Config,
            NullLogger<GamesSnapshotBuilder>.Instance);

        foreach (var view in new[] { "today", "history" })
        {
            var rows = (await builder.BuildAsync(view: view)).Items;
            var remake = Assert.Single(rows, r => r.GameId == 7003);
            Assert.True(remake.IsRemake);
            Assert.Equal("Remake", remake.WinLossText);
            Assert.False(Assert.Single(rows, r => r.GameId == 7001).IsRemake);
            Assert.Equal("L", Assert.Single(rows, r => r.GameId == 7001).WinLossText);
        }

        var queue = (await builder.BuildAsync(view: "queue")).Items;
        Assert.DoesNotContain(queue, r => r.GameId == 7003);
        Assert.Contains(queue, r => r.GameId == 7002);
    }

    [Fact]
    public async Task ReviewHeader_ReadsRemake_KeepsTheStoredResult()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await SeedDayAsync(scope);

        var header = (await ReviewBuilder(scope).BuildAsync(7003)).Subject!.Header;

        Assert.Equal("REMAKE", header.ResultText);
        Assert.Equal("Remake", header.WinLossText);
        Assert.False(header.Win);
        Assert.True(header.IsRemake);
        Assert.Equal("eog", header.RemakeSource);

        var normal = (await ReviewBuilder(scope).BuildAsync(7001)).Subject!.Header;
        Assert.Equal("DEFEAT", normal.ResultText);
        Assert.False(normal.IsRemake);
        Assert.Equal("", normal.RemakeSource);

        // An unanswered lookup is not a verdict the toggle shows.
        await scope.Games.SetRemakeVerdictAsync(7001, false, false, "unknown");
        Assert.Equal("", (await ReviewBuilder(scope).BuildAsync(7001)).Subject!.Header.RemakeSource);
    }

    [Fact]
    public async Task PlayerToggle_RoundTrips_IsSticky_AndAnnouncesOnlyAFlip()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await SeedDayAsync(scope);
        var hub = new SidecarEventHub();
        var (reader, sub) = hub.Subscribe();
        using var _ = sub;
        var backups = 0;
        Task Backup() { backups++; return Task.CompletedTask; }

        Assert.Null(await RemakeNotifications.SetByPlayerAsync(99999, true, scope.Games, Backup, hub, null, NullLogger.Instance));
        Assert.Equal(0, backups);

        // Untick the detected remake: the game counts again, and stays counted.
        Assert.False(await RemakeNotifications.SetByPlayerAsync(7003, false, scope.Games, Backup, hub, null, NullLogger.Instance));
        Assert.True(reader.TryRead(out var evt));
        Assert.Equal("remakeUpdated", evt.Type);
        Assert.Equal("""{"gameId":7003,"isRemake":false}""", JsonSerializer.Serialize(evt.Payload));
        await scope.Games.SetRemakeVerdictAsync(7003, true, false, "matchv5");
        var header = (await ReviewBuilder(scope).BuildAsync(7003)).Subject!.Header;
        Assert.False(header.IsRemake);
        Assert.Equal("user", header.RemakeSource);
        Assert.Equal(3, (await scope.Games.GetTodaysRuleGamesAsync()).Count);

        // Same value again: no flip, no event. Then tick it back.
        Assert.False(await RemakeNotifications.SetByPlayerAsync(7003, false, scope.Games, Backup, hub, null, NullLogger.Instance));
        Assert.False(reader.TryRead(out var _));
        Assert.True(await RemakeNotifications.SetByPlayerAsync(7003, true, scope.Games, Backup, hub, null, NullLogger.Instance));
        Assert.True(reader.TryRead(out var again));
        Assert.Equal("remakeUpdated", again.Type);
        Assert.Equal(2, (await scope.Games.GetTodaysRuleGamesAsync()).Count);
        Assert.Equal(3, backups);
    }

    [Theory]
    [InlineData(false, 386, false)] // the payload said "not a remake": prepare at game end
    [InlineData(null, 386, true)]   // short and unsaid: wait for the remake check
    [InlineData(true, 386, true)]   // a remake: the deferred pass skips it
    [InlineData(null, 1800, false)] // too long to be a remake
    public void AutomaticMatchupNote_WaitsForTheRemakeCheckOnShortUnconfirmedGames(
        bool? endedInEarlySurrender, int duration, bool waits)
    {
        Assert.Equal(waits, SidecarGameFlowCoordinator.RemakeNoteMustWait(endedInEarlySurrender, duration));
    }
}
