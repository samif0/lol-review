using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

/// <summary>
/// Game-end durability: a slow end-of-game capture still saves the game, a failed or missed
/// capture recovers the tracked game from match history with its live timeline, and a League
/// client drop never throws the timeline away.
/// </summary>
public sealed partial class GameMonitorServiceTests
{
    private static readonly LcuCredentials Credentials = new() { Port = 2999, Password = "pw" };

    private static JsonElement Kill(double at, string victim = "Zed") =>
        JsonSerializer.SerializeToElement(new
        {
            EventName = "ChampionKill",
            EventTime = at,
            KillerName = "Tester",
            VictimName = victim,
            Assisters = Array.Empty<string>(),
        });

    private static HashSet<int> KillTimes(GameStats stats) =>
        stats.LiveEvents.Where(e => e.EventType == GameEvent.EventTypes.Kill).Select(e => e.GameTimeS).ToHashSet();

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task CaptureSlowerThanTheTickWait_StillEndsInASavedGame()
    {
        using var db = new TestDatabaseScope();
        await db.InitializeAsync();
        // The save the sidecar's game-end workflow runs first (row, session log, live timeline).
        var games = new GameService(db.Games, db.SessionLog, new RulesRepository(db.ConnectionFactory), db.GameEvents,
            db.DerivedEvents, new TestConfigService(), NullLogger<GameService>.Instance);

        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var saves = new List<Task<long?>>();
        var sink = new object();
        messenger.Register<object, GameEndedMessage>(sink, (_, m) =>
            saves.Add(games.ProcessGameEndAsync(new ProcessGameEndRequest(m.Stats))));

        var lcu = new FakeLcuClient
        {
            IsConnected = true, QueueId = 420, CurrentGameId = 9100,
            Phases = new([GamePhase.ChampSelect, GamePhase.InProgress, GamePhase.WaitingForStats, GamePhase.EndOfGame, GamePhase.Lobby]),
        };
        var stats = TestGameStatsFactory.Create(9100, champion: "Ahri");
        var slowEog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new FakeGameEndCaptureService { Result = stats, Gate = slowEog.Task };
        var history = new FakeMatchHistoryReconciliationService();
        using var service = CreateService(new FakeCredentialDiscovery(Credentials), lcu, messenger, capture, history);

        // The old fixed 5 s deadline cut the capture's 12 x 2 s retry schedule to about three tries.
        Assert.True(service.CaptureRetryBudget >= TimeSpan.FromSeconds(
            GameConstants.EogStatsRetryAttempts * GameConstants.EogStatsRetryDelayS));
        service.CaptureInlineWait = TimeSpan.FromMilliseconds(50);

        await service.TickOnceAsync(); // ChampSelect
        await service.TickOnceAsync(); // InProgress
        var tick = Stopwatch.StartNew();
        await service.TickOnceAsync(); // WaitingForStats: the EOG stats are not there yet
        Assert.True(tick.Elapsed < TimeSpan.FromSeconds(3), "the monitor loop never waits out a slow capture");
        Assert.Empty(collector.GameEnded);
        Assert.Empty(collector.CaptureDeferred);
        Assert.NotNull(service.PendingCapture);

        slowEog.SetResult(); // the stats arrive after that tick moved on
        await service.PendingCapture!;
        await service.TickOnceAsync(); // EndOfGame: the finished capture publishes

        var ended = Assert.Single(collector.GameEnded);
        Assert.Equal(9100, ended.Stats.GameId);
        Assert.False(ended.IsRecovered);
        Assert.Equal(1, capture.Calls);
        Assert.Equal(9100, await Assert.Single(saves));
        Assert.NotNull(await db.Games.GetAsync(9100));

        await service.TickOnceAsync(); // Lobby: a captured game needs no match-history walk
        Assert.Equal(0, history.Calls);
        Assert.Empty(collector.MissedGamesDetected);
        Assert.Empty(collector.CaptureDeferred);
    }

    [Fact]
    public async Task FailedCapture_RecoversTheTrackedGameWithItsLiveTimeline_AndLeavesOlderGamesToThePlayer()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var live = new FakeLiveEventApi { Available = true, Events = [Kill(100)] };
        var lcu = new FakeLcuClient
        {
            IsConnected = true, QueueId = 420, CurrentGameId = 8100,
            Phases = new([GamePhase.ChampSelect, GamePhase.InProgress]),
        };
        var capture = new FakeGameEndCaptureService(); // the EOG stats never arrive
        var older = new MissedGameCandidate(7000, 1_700_000_000, TestGameStatsFactory.Create(7000));
        var tracked = new MissedGameCandidate(8100, 1_710_000_000, TestGameStatsFactory.Create(8100, champion: "Ahri"));
        var history = new FakeMatchHistoryReconciliationService { Result = [tracked, older] };
        using var service = CreateService(new FakeCredentialDiscovery(Credentials), lcu, messenger, capture, history, live);

        await service.TickOnceAsync(); // ChampSelect
        await service.TickOnceAsync(); // InProgress: the collector starts
        await WaitUntilAsync(() => live.EventFetches > 0, "the collector read the kill feed");

        lcu.Phases = new([GamePhase.EndOfGame, GamePhase.Lobby]);
        await service.TickOnceAsync(); // EndOfGame: no stats, so the game goes to reconciliation
        Assert.Equal(8100, Assert.Single(collector.CaptureDeferred).GameId);
        Assert.Empty(collector.GameEnded);
        Assert.Contains(100, capture.LastEvents.Where(e => e.EventType == GameEvent.EventTypes.Kill).Select(e => e.GameTimeS));

        await service.TickOnceAsync(); // Lobby: the post-game reconcile finds it
        var recovered = Assert.Single(collector.GameEnded);
        Assert.True(recovered.IsRecovered);
        Assert.Equal(8100, recovered.Stats.GameId);
        Assert.Contains(100, KillTimes(recovered.Stats));
        var waiting = Assert.Single(collector.MissedGamesDetected);
        Assert.Equal([7000L], waiting.Games.Select(g => g.GameId));
    }

    [Fact]
    public async Task CaptureError_DefersTheGameOnce_AndASecondPostGameTickKeepsItsTimeline()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var live = new FakeLiveEventApi { Available = true, Events = [Kill(100)] };
        var lcu = new FakeLcuClient
        {
            IsConnected = true, QueueId = 420, CurrentGameId = 8200,
            Phases = new([GamePhase.ChampSelect, GamePhase.InProgress]),
        };
        var capture = new FakeGameEndCaptureService { Throw = new HttpRequestException("EOG endpoint returned 503") };
        var history = new FakeMatchHistoryReconciliationService
        {
            Result = [new MissedGameCandidate(8200, 1_710_000_000, TestGameStatsFactory.Create(8200))],
        };
        using var service = CreateService(new FakeCredentialDiscovery(Credentials), lcu, messenger, capture, history, live);

        await service.TickOnceAsync();
        await service.TickOnceAsync();
        await WaitUntilAsync(() => live.EventFetches > 0, "the collector read the kill feed");

        lcu.Phases = new([GamePhase.EndOfGame, GamePhase.EndOfGame, GamePhase.Lobby]);
        await service.TickOnceAsync(); // the capture throws: handled, the tick completes
        await service.TickOnceAsync(); // still on the end-of-game screen: no second game-end pass
        Assert.Equal(1, capture.Calls);
        Assert.Single(collector.CaptureDeferred);

        await service.TickOnceAsync();
        var recovered = Assert.Single(collector.GameEnded);
        Assert.True(recovered.IsRecovered);
        Assert.Contains(100, KillTimes(recovered.Stats));
    }

    [Fact]
    public void StoredTimeline_IsNeverReplacedByAnEmptyOne_AndMergesWithoutDuplicates()
    {
        var first = new GameEvent { EventType = GameEvent.EventTypes.Kill, GameTimeS = 100, Details = "{}" };
        var second = new GameEvent { EventType = GameEvent.EventTypes.Death, GameTimeS = 50, Details = "{}" };
        var stored = new List<GameEvent> { first };

        Assert.Same(stored, GameMonitorService.MergeEvents(stored, []));
        var merged = GameMonitorService.MergeEvents(stored, [new() { EventType = first.EventType, GameTimeS = 100, Details = "{}" }, second]);
        Assert.Equal([50, 100], merged.Select(e => e.GameTimeS));
    }

    [Fact]
    public async Task MidGameClientDisconnect_KeepsTheCollectorRunning_SoTheCaptureGetsTheWholeTimeline()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var live = new FakeLiveEventApi { Available = true, Events = [Kill(100)] };
        var lcu = new FakeLcuClient
        {
            IsConnected = true, QueueId = 420, CurrentGameId = 8300,
            Phases = new([GamePhase.ChampSelect, GamePhase.InProgress]),
        };
        var capture = new FakeGameEndCaptureService { Result = TestGameStatsFactory.Create(8300) };
        using var service = CreateService(new FakeCredentialDiscovery(Credentials), lcu, messenger, capture,
            new FakeMatchHistoryReconciliationService(), live);

        await service.TickOnceAsync();
        await service.TickOnceAsync();
        await WaitUntilAsync(() => live.EventFetches > 0, "the collector read the kill feed");

        lcu.ThrowOnNextPhase = true; // one failed phase poll (a client hiccup)
        await service.TickOnceAsync();
        Assert.Contains(collector.ConnectionChanges, m => !m.IsConnected);

        // The game goes on while the client is away; the collector still reads it.
        live.Events = [Kill(100), Kill(200, victim: "Ahri")];

        lcu.ThrowOnNextPhase = false;
        lcu.Phases = new([GamePhase.EndOfGame]);
        await service.TickOnceAsync(); // back, straight onto the end-of-game screen

        var ended = Assert.Single(collector.GameEnded);
        Assert.False(ended.IsRecovered);
        Assert.Equal([100, 200], KillTimes(ended.Stats).Order());
        Assert.Empty(collector.CaptureDeferred);
    }

    [Fact]
    public async Task GameEndingWhileTheClientIsAway_KeepsItsTimeline_ForTheRecoveredSave()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var live = new FakeLiveEventApi { Available = true, Events = [Kill(100)] };
        var lcu = new FakeLcuClient
        {
            IsConnected = true, QueueId = 420, CurrentGameId = 8400,
            Phases = new([GamePhase.ChampSelect, GamePhase.InProgress]),
        };
        var history = new FakeMatchHistoryReconciliationService
        {
            Result = [new MissedGameCandidate(8400, 1_710_000_000, TestGameStatsFactory.Create(8400))],
        };
        using var service = CreateService(new FakeCredentialDiscovery(Credentials), lcu, messenger,
            new FakeGameEndCaptureService(), history, live);

        await service.TickOnceAsync();
        await service.TickOnceAsync();
        await WaitUntilAsync(() => live.EventFetches > 0, "the collector read the kill feed");
        lcu.ThrowOnNextPhase = true;
        await service.TickOnceAsync(); // the client drops mid-game

        // The game ends while the client is away; it comes back to the lobby.
        live.Available = false;
        live.ThrowEvents = true;
        lcu.ThrowOnNextPhase = false;
        lcu.Phases = new([GamePhase.Lobby]);
        await service.TickOnceAsync();

        Assert.Equal(8400, Assert.Single(collector.CaptureDeferred).GameId);
        var recovered = Assert.Single(collector.GameEnded);
        Assert.True(recovered.IsRecovered);
        Assert.Contains(100, KillTimes(recovered.Stats));
    }

    [Fact]
    public async Task CollectorWhoseGameGoesSilentWhileTheClientStaysAway_IsFinished_AndItsTimelineKept()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var live = new FakeLiveEventApi { Available = true, Events = [Kill(100)] };
        var lcu = new FakeLcuClient
        {
            IsConnected = true, QueueId = 420, CurrentGameId = 8500,
            Phases = new([GamePhase.ChampSelect, GamePhase.InProgress]),
        };
        var history = new FakeMatchHistoryReconciliationService();
        using var service = CreateService(new FakeCredentialDiscovery(Credentials), lcu, messenger,
            new FakeGameEndCaptureService(), history, live);
        service.OrphanedCollectorGrace = TimeSpan.Zero;

        await service.TickOnceAsync();
        await service.TickOnceAsync();
        await WaitUntilAsync(() => live.EventFetches > 0, "the collector read the kill feed");
        lcu.ThrowOnNextPhase = true;
        await service.TickOnceAsync(); // the client drops mid-game
        lcu.ThrowOnNextPhase = false;
        lcu.IsConnected = false;       // ...and stays closed

        await service.TickOnceAsync(); // the game still answers: keep collecting
        Assert.Empty(collector.CaptureDeferred);

        live.Available = false;        // the game is over
        live.ThrowEvents = true;
        await service.TickOnceAsync();
        Assert.Equal(8500, Assert.Single(collector.CaptureDeferred).GameId);

        // The client returns later; the reconcile pass recovers the game with its timeline.
        history.Result = [new MissedGameCandidate(8500, 1_710_000_000, TestGameStatsFactory.Create(8500))];
        lcu.IsConnected = true;
        lcu.Phases = new([GamePhase.Lobby]);
        await service.TickOnceAsync();
        var recovered = Assert.Single(collector.GameEnded);
        Assert.True(recovered.IsRecovered);
        Assert.Contains(100, KillTimes(recovered.Stats));
    }

    [Fact]
    public async Task Reconciliation_NeverOffersAGameThisSessionAlreadyPublished()
    {
        // The id lookup failed at game start, so the reconcile armed by the client drop
        // stays armed after the capture; match history lists the game before its save lands.
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var lcu = new FakeLcuClient { IsConnected = true, QueueId = 420, Phases = new([GamePhase.ChampSelect, GamePhase.InProgress]) };
        var capture = new FakeGameEndCaptureService { Result = TestGameStatsFactory.Create(9300) };
        var history = new FakeMatchHistoryReconciliationService
        {
            Result = [new MissedGameCandidate(9300, 1_710_000_000, TestGameStatsFactory.Create(9300))],
        };
        using var service = CreateService(new FakeCredentialDiscovery(Credentials), lcu, messenger, capture, history);

        await service.TickOnceAsync();
        await service.TickOnceAsync();
        lcu.ThrowOnNextPhase = true;
        await service.TickOnceAsync();
        lcu.ThrowOnNextPhase = false;
        lcu.Phases = new([GamePhase.EndOfGame, GamePhase.Lobby]);
        await service.TickOnceAsync();
        Assert.Single(collector.GameEnded);

        await service.TickOnceAsync(); // Lobby: the armed reconcile runs
        Assert.Equal(1, history.Calls);
        Assert.Single(collector.GameEnded);
        Assert.Empty(collector.MissedGamesDetected);
    }

    [Theory]
    [InlineData(480, false)]  // Swiftplay
    [InlineData(700, false)]  // Clash
    [InlineData(440, false)]  // Ranked Flex
    [InlineData(420, true)]   // Ranked Solo/Duo
    [InlineData(-1, true)]    // the queue lookup failed: the save gate decides
    public async Task OnlyRankedSoloOrAnUnknownQueueRunsTheGameEndCapture(int queueId, bool captured)
    {
        var messenger = new StrongReferenceMessenger();
        var lcu = new FakeLcuClient
        {
            IsConnected = true, QueueId = queueId,
            Phases = new([GamePhase.ChampSelect, GamePhase.InProgress, GamePhase.EndOfGame]),
        };
        var capture = new FakeGameEndCaptureService { Result = TestGameStatsFactory.Create(9400) };
        using var service = CreateService(new FakeCredentialDiscovery(Credentials), lcu, messenger, capture,
            new FakeMatchHistoryReconciliationService());

        await service.TickOnceAsync();
        await service.TickOnceAsync();
        await service.TickOnceAsync();

        Assert.Equal(captured ? 1 : 0, capture.Calls);
    }
}
