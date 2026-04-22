using CommunityToolkit.Mvvm.Messaging;
using LoLReview.Core.Lcu;
using LoLReview.Core.Models;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoLReview.Core.Tests;

public sealed class GameMonitorServiceTests
{
    [Fact]
    public async Task TickOnceAsync_BacksOffAfterMissingCredentials()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var credentialDiscovery = new FakeCredentialDiscovery();
        var lcuClient = new FakeLcuClient();

        var service = CreateService(
            credentialDiscovery,
            lcuClient,
            messenger,
            new FakeGameEndCaptureService(),
            new FakeMatchHistoryReconciliationService());

        await service.TickOnceAsync();
        await service.TickOnceAsync();

        Assert.Equal(1, credentialDiscovery.FindCredentialsCalls);
        Assert.Empty(collector.ConnectionChanges);
    }

    [Fact]
    public async Task TickOnceAsync_SendsChampSelectStartAndCancelMessages()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var credentialDiscovery = new FakeCredentialDiscovery(new LcuCredentials { Port = 2999, Password = "pw" });
        var lcuClient = new FakeLcuClient
        {
            IsConnected = true,
            QueueId = 420,
            Phases = new Queue<GamePhase>([GamePhase.ChampSelect, GamePhase.Lobby]),
        };

        var service = CreateService(
            credentialDiscovery,
            lcuClient,
            messenger,
            new FakeGameEndCaptureService(),
            new FakeMatchHistoryReconciliationService());

        await service.TickOnceAsync();
        await service.TickOnceAsync();

        Assert.Single(collector.ConnectionChanges);
        Assert.True(collector.ConnectionChanges[0].IsConnected);

        var champSelectStarted = Assert.Single(collector.ChampSelectStarted);
        Assert.Equal(420, champSelectStarted.QueueId);
        Assert.Single(collector.ChampSelectCancelled);
    }

    [Fact]
    public async Task TickOnceAsync_PublishesGameEndedMessageFromCapturedStats()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var credentialDiscovery = new FakeCredentialDiscovery(new LcuCredentials { Port = 2999, Password = "pw" });
        var lcuClient = new FakeLcuClient
        {
            IsConnected = true,
            Phases = new Queue<GamePhase>([GamePhase.EndOfGame]),
        };
        var captureService = new FakeGameEndCaptureService
        {
            Result = TestGameStatsFactory.Create(9001, champion: "Ahri")
        };

        var service = CreateService(
            credentialDiscovery,
            lcuClient,
            messenger,
            captureService,
            new FakeMatchHistoryReconciliationService());

        await service.TickOnceAsync();

        var endedMessage = Assert.Single(collector.GameEnded);
        Assert.Equal(9001, endedMessage.Stats.GameId);
        Assert.Equal("Ahri", endedMessage.Stats.ChampionName);
    }

    [Fact]
    public async Task TickOnceAsync_PublishesGameEndedMessageWhenEnteringPreEndOfGame()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var credentialDiscovery = new FakeCredentialDiscovery(new LcuCredentials { Port = 2999, Password = "pw" });
        var lcuClient = new FakeLcuClient
        {
            IsConnected = true,
            Phases = new Queue<GamePhase>([GamePhase.InProgress, GamePhase.PreEndOfGame]),
        };
        var captureService = new FakeGameEndCaptureService
        {
            Result = TestGameStatsFactory.Create(9002, champion: "KaiSa")
        };

        var service = CreateService(
            credentialDiscovery,
            lcuClient,
            messenger,
            captureService,
            new FakeMatchHistoryReconciliationService());

        await service.TickOnceAsync();
        await service.TickOnceAsync();

        var endedMessage = Assert.Single(collector.GameEnded);
        Assert.Equal(9002, endedMessage.Stats.GameId);
        Assert.Equal("KaiSa", endedMessage.Stats.ChampionName);
    }

    [Fact]
    public async Task TickOnceAsync_ReconcilesMissedGameAfterLeavingPreEndOfGame()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var credentialDiscovery = new FakeCredentialDiscovery(new LcuCredentials { Port = 2999, Password = "pw" });
        var lcuClient = new FakeLcuClient
        {
            IsConnected = true,
            Phases = new Queue<GamePhase>([GamePhase.PreEndOfGame, GamePhase.Lobby]),
        };
        var candidate = new MissedGameCandidate(7002, 1_710_000_000, TestGameStatsFactory.Create(7002));
        var reconciliationService = new FakeMatchHistoryReconciliationService
        {
            Result = [candidate]
        };

        var service = CreateService(
            credentialDiscovery,
            lcuClient,
            messenger,
            new FakeGameEndCaptureService(),
            reconciliationService);

        await service.TickOnceAsync();
        await service.TickOnceAsync();

        var detected = Assert.Single(collector.MissedGamesDetected);
        var missedCandidate = Assert.Single(detected.Games);
        Assert.Equal(7002, missedCandidate.GameId);
    }

    [Fact]
    public async Task TickOnceAsync_PublishesMissedGamesDuringStartupReconciliation()
    {
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var credentialDiscovery = new FakeCredentialDiscovery(new LcuCredentials { Port = 2999, Password = "pw" });
        var lcuClient = new FakeLcuClient
        {
            IsConnected = true,
            Phases = new Queue<GamePhase>([GamePhase.Lobby, GamePhase.Lobby]),
        };
        var candidate = new MissedGameCandidate(7001, 1_710_000_000, TestGameStatsFactory.Create(7001));
        var reconciliationService = new FakeMatchHistoryReconciliationService
        {
            Result = [candidate]
        };

        var service = CreateService(
            credentialDiscovery,
            lcuClient,
            messenger,
            new FakeGameEndCaptureService(),
            reconciliationService);

        await service.TickOnceAsync();
        Assert.Empty(collector.MissedGamesDetected);

        await service.TickOnceAsync();

        var detected = Assert.Single(collector.MissedGamesDetected);
        var missedCandidate = Assert.Single(detected.Games);
        Assert.Equal(7001, missedCandidate.GameId);
    }

    [Fact]
    public async Task TickOnceAsync_DetectsDisconnectAndReconnects()
    {
        // Regression test for the bug where GetGameflowPhaseAsync swallowed exceptions,
        // leaving IsConnected=true permanently and blocking credential re-discovery.
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var credentialDiscovery = new FakeCredentialDiscovery(new LcuCredentials { Port = 2999, Password = "pw" });
        var lcuClient = new FakeLcuClient
        {
            IsConnected = true,
            Phases = new Queue<GamePhase>([GamePhase.Lobby]),
        };

        var service = CreateService(credentialDiscovery, lcuClient, messenger,
            new FakeGameEndCaptureService(), new FakeMatchHistoryReconciliationService());

        // First tick — connects and gets Lobby phase
        await service.TickOnceAsync();
        Assert.Single(collector.ConnectionChanges, m => m.IsConnected);

        // Simulate client going away: next GetGameflowPhaseAsync will throw
        lcuClient.ThrowOnNextPhase = true;

        // Second tick — phase call throws, must detect disconnect
        await service.TickOnceAsync();
        Assert.Contains(collector.ConnectionChanges, m => !m.IsConnected);

        // Third tick — credentials found again, reconnects
        lcuClient.ThrowOnNextPhase = false;
        lcuClient.IsConnected = true;
        lcuClient.Phases = new Queue<GamePhase>([GamePhase.Lobby]);
        await service.TickOnceAsync();

        Assert.Equal(2, collector.ConnectionChanges.Count(static m => m.IsConnected));
    }

    [Fact]
    public async Task TickOnceAsync_FullGameLifecycle_FiresEventsInOrder()
    {
        // Covers the critical path: app start → champ select → in game → post game
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var credentialDiscovery = new FakeCredentialDiscovery(new LcuCredentials { Port = 2999, Password = "pw" });
        var lcuClient = new FakeLcuClient
        {
            IsConnected = true,
            QueueId = 420,
            Phases = new Queue<GamePhase>([
                GamePhase.Lobby,
                GamePhase.ChampSelect,
                GamePhase.InProgress,
                GamePhase.InProgress,
                GamePhase.EndOfGame,
                GamePhase.Lobby,
            ]),
        };
        var captureService = new FakeGameEndCaptureService
        {
            Result = TestGameStatsFactory.Create(5001, champion: "Jinx")
        };

        var service = CreateService(credentialDiscovery, lcuClient, messenger,
            captureService, new FakeMatchHistoryReconciliationService());

        // Tick 1 — Lobby (connect, no events yet except connection)
        await service.TickOnceAsync();
        Assert.Empty(collector.ChampSelectStarted);

        // Tick 2 — ChampSelect entered → pre-game should fire
        await service.TickOnceAsync();
        Assert.Single(collector.ChampSelectStarted);
        Assert.Equal(420, collector.ChampSelectStarted[0].QueueId);
        Assert.Empty(collector.GameEnded);

        // Tick 3 — InProgress entered → game started
        await service.TickOnceAsync();
        Assert.Empty(collector.GameEnded);

        // Tick 4 — still InProgress → nothing new
        await service.TickOnceAsync();
        Assert.Empty(collector.GameEnded);

        // Tick 5 — EndOfGame entered → post-game fires exactly once
        await service.TickOnceAsync();
        var ended = Assert.Single(collector.GameEnded);
        Assert.Equal(5001, ended.Stats.GameId);
        Assert.Equal("Jinx", ended.Stats.ChampionName);

        // Tick 6 — back to Lobby → no second GameEnded
        await service.TickOnceAsync();
        Assert.Single(collector.GameEnded);
    }

    [Fact]
    public async Task TickOnceAsync_PostGameFiresOnce_NotOnEveryPostGameTick()
    {
        // Post-game page must appear exactly once, not re-trigger on each poll
        var messenger = new StrongReferenceMessenger();
        var collector = new MonitorMessageCollector(messenger);
        var credentialDiscovery = new FakeCredentialDiscovery(new LcuCredentials { Port = 2999, Password = "pw" });
        var lcuClient = new FakeLcuClient
        {
            IsConnected = true,
            Phases = new Queue<GamePhase>([
                GamePhase.InProgress,
                GamePhase.WaitingForStats,
                GamePhase.EndOfGame,
                GamePhase.EndOfGame,
            ]),
        };
        var captureService = new FakeGameEndCaptureService
        {
            Result = TestGameStatsFactory.Create(5002, champion: "Lux")
        };

        var service = CreateService(credentialDiscovery, lcuClient, messenger,
            captureService, new FakeMatchHistoryReconciliationService());

        await service.TickOnceAsync(); // InProgress
        await service.TickOnceAsync(); // WaitingForStats → post-game fires
        await service.TickOnceAsync(); // EndOfGame (still post-game) → no second fire
        await service.TickOnceAsync(); // EndOfGame again → still no second fire

        Assert.Single(collector.GameEnded);
    }

    private static GameMonitorService CreateService(
        FakeCredentialDiscovery credentialDiscovery,
        FakeLcuClient lcuClient,
        IMessenger messenger,
        FakeGameEndCaptureService gameEndCaptureService,
        FakeMatchHistoryReconciliationService reconciliationService)
    {
        return new GameMonitorService(
            credentialDiscovery,
            lcuClient,
            new FakeLiveEventApi(),
            gameEndCaptureService,
            reconciliationService,
            messenger,
            NullLogger<GameMonitorService>.Instance);
    }

    private sealed class MonitorMessageCollector
    {
        public MonitorMessageCollector(IMessenger messenger)
        {
            messenger.Register<LcuConnectionChangedMessage>(this, static (recipient, message) => ((MonitorMessageCollector)recipient).ConnectionChanges.Add(message));
            messenger.Register<ChampSelectStartedMessage>(this, static (recipient, message) => ((MonitorMessageCollector)recipient).ChampSelectStarted.Add(message));
            messenger.Register<ChampSelectCancelledMessage>(this, static (recipient, message) => ((MonitorMessageCollector)recipient).ChampSelectCancelled.Add(message));
            messenger.Register<GameEndedMessage>(this, static (recipient, message) => ((MonitorMessageCollector)recipient).GameEnded.Add(message));
            messenger.Register<MissedReviewsDetectedMessage>(this, static (recipient, message) => ((MonitorMessageCollector)recipient).MissedGamesDetected.Add(message));
        }

        public List<LcuConnectionChangedMessage> ConnectionChanges { get; } = [];

        public List<ChampSelectStartedMessage> ChampSelectStarted { get; } = [];

        public List<ChampSelectCancelledMessage> ChampSelectCancelled { get; } = [];

        public List<GameEndedMessage> GameEnded { get; } = [];

        public List<MissedReviewsDetectedMessage> MissedGamesDetected { get; } = [];
    }

    private sealed class FakeCredentialDiscovery : ILcuCredentialDiscovery
    {
        private readonly LcuCredentials? _credentials;

        public FakeCredentialDiscovery(LcuCredentials? credentials = null)
        {
            _credentials = credentials;
        }

        public int FindCredentialsCalls { get; private set; }

        public LcuCredentials? FindCredentials()
        {
            FindCredentialsCalls++;
            return _credentials;
        }

        public LcuCredentials? FindFromProcess() => _credentials;

        public LcuCredentials? FindFromLockfile() => _credentials;
    }

    private sealed class FakeLcuClient : ILcuClient
    {
        public Queue<GamePhase> Phases { get; set; } = new();

        public bool IsConnected { get; set; }

        public int QueueId { get; set; }

        /// <summary>When true, GetGameflowPhaseAsync throws to simulate a lost connection.</summary>
        public bool ThrowOnNextPhase { get; set; }

        public void Configure(LcuCredentials credentials)
        {
        }

        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(IsConnected);

        public Task<System.Text.Json.JsonElement?> GetCurrentSummonerAsync(CancellationToken ct = default) =>
            Task.FromResult<System.Text.Json.JsonElement?>(null);

        public Task<GamePhase> GetGameflowPhaseAsync(CancellationToken ct = default)
        {
            if (ThrowOnNextPhase)
                throw new HttpRequestException("Simulated LCU connection loss");

            if (Phases.Count == 0)
                return Task.FromResult(GamePhase.None);

            var phase = Phases.Count > 1 ? Phases.Dequeue() : Phases.Peek();
            return Task.FromResult(phase);
        }

        public Task<System.Text.Json.JsonElement?> GetEndOfGameStatsAsync(CancellationToken ct = default) =>
            Task.FromResult<System.Text.Json.JsonElement?>(null);

        public Task<int> GetLobbyQueueIdAsync(CancellationToken ct = default) => Task.FromResult(QueueId);

        public Task<List<System.Text.Json.JsonElement>> GetMatchHistoryAsync(int begin = 0, int count = 5, CancellationToken ct = default) =>
            Task.FromResult<List<System.Text.Json.JsonElement>>([]);

        public Task<System.Text.Json.JsonElement?> GetMatchDetailsAsync(long gameId, CancellationToken ct = default) =>
            Task.FromResult<System.Text.Json.JsonElement?>(null);

        public Task<string?> GetChampionNameAsync(int championId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<System.Text.Json.JsonElement?> GetRankedStatsAsync(CancellationToken ct = default) =>
            Task.FromResult<System.Text.Json.JsonElement?>(null);

        public Task<(string MyChampion, string EnemyLaner, string MyPosition)> GetChampSelectInfoAsync(CancellationToken ct = default) =>
            Task.FromResult(("", "", ""));
    }

    private sealed class FakeLiveEventApi : ILiveEventApi
    {
        public Task<string?> GetActivePlayerNameAsync(CancellationToken ct = default) => Task.FromResult<string?>("Tester");

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<List<System.Text.Json.JsonElement>?> FetchEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<List<System.Text.Json.JsonElement>?>([]);
    }

    private sealed class FakeGameEndCaptureService : IGameEndCaptureService
    {
        public GameStats? Result { get; set; }

        public Task<GameStats?> CaptureAsync(IReadOnlyList<GameEvent> liveEvents, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    private sealed class FakeMatchHistoryReconciliationService : IMatchHistoryReconciliationService
    {
        public IReadOnlyList<MissedGameCandidate> Result { get; set; } = [];

        public Task<IReadOnlyList<MissedGameCandidate>> FindMissedGamesAsync(
            Func<long, bool>? checkGameSaved,
            CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }
}
