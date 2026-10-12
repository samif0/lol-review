using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// No finished game is dropped silently: the tracked game whose capture was deferred is
/// saved through the normal game-end path with its own pre-game choices and live timeline,
/// and every other unsaved game waits on Home until the player saves or dismisses it.
///
/// The coordinator writes through <see cref="WriteServices"/>, which opens the process-wide
/// test data root's revu.db (TestDataRoot, never the live app data). This class is the only
/// one that writes there and uses its own game ids.
/// </summary>
public sealed class MissedGamesFlowTests : IAsyncLifetime
{
    private readonly StrongReferenceMessenger _messenger = new();
    private readonly SidecarEventHub _hub = new();
    private readonly LcuLiveState _live = new();
    private readonly SidecarBackgroundWork _work = new(NullLogger<SidecarBackgroundWork>.Instance);
    private WriteServices _write = null!;
    private SidecarGameFlowCoordinator _flow = null!;

    public async Task InitializeAsync()
    {
        if (!File.Exists(AppDataPaths.DatabasePath))
        {
            Directory.CreateDirectory(AppDataPaths.UserDataRoot);
            var factory = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance, AppDataPaths.DatabasePath);
            await new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        }

        _write = new WriteServices(NullLoggerFactory.Instance, _work);
        var lcu = new IdleLcu();
        var monitor = new GameMonitorService(new NoCredentials(), lcu, new ClosedGame(), new NoCapture(),
            new NoHistory(), _messenger, NullLogger<GameMonitorService>.Instance);
        var hardStop = new HardStopEnforcer(_write.Rules, _write.Games, _write.HardStops, lcu, _hub, _live,
            NullLogger<HardStopEnforcer>.Instance);
        _flow = new SidecarGameFlowCoordinator(_messenger, _hub, _live, monitor, _write, hardStop,
            NullLogger<SidecarGameFlowCoordinator>.Instance, _work);
        await _flow.StartAsync(default);
    }

    public async Task DisposeAsync()
    {
        await _flow.StopAsync(default);
        await _work.StopAsync(default); // cancels the post-game passes' delays
        _write.Dispose();
    }

    private static GameStats Game(long gameId, string champion = "Ahri", long? timestamp = null)
    {
        var stats = TestGameStatsFactory.Create(gameId, champion, timestamp: timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        stats.LiveEvents = [new GameEvent { EventType = GameEvent.EventTypes.Kill, GameTimeS = 100, Details = "{\"victim\":\"Zed\"}" }];
        return stats;
    }

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, string what) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(25);
        }
    }

    private static async Task<(int Mood, string Intention, long Events)> SavedRowAsync(long gameId)
    {
        await using var conn = new SqliteConnection($"Data Source={AppDataPaths.DatabasePath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.pre_game_mood, s.pregame_intention,
                   (SELECT COUNT(*) FROM game_events e WHERE e.game_id = s.game_id)
            FROM session_log s WHERE s.game_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", gameId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"session_log row for {gameId}");
        return (reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetInt64(2));
    }

    private static List<SidecarEventHub.SidecarEvent> Drain(System.Threading.Channels.ChannelReader<SidecarEventHub.SidecarEvent> reader)
    {
        var events = new List<SidecarEventHub.SidecarEvent>();
        while (reader.TryRead(out var evt)) events.Add(evt);
        return events;
    }

    [Fact]
    public async Task DeferredCapture_IsSavedAsTheRecoveredGame_WithItsOwnPreGameChoicesAndTimeline()
    {
        const long gameId = 6_100_000_001;
        var (reader, sub) = _hub.Subscribe();
        using var _ = sub;

        _live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        _live.SetMood(4);
        _live.SetIntent("Ward the river at 3:00", "typed", cleared: false);

        // The capture failed: the monitor hands the game to reconciliation.
        _messenger.Send(new GameCaptureDeferredMessage(gameId));
        var pending = Assert.Single(Drain(reader), e => e.Type == "gameEnded");
        Assert.Contains("\"pending\":true", JsonSerializer.Serialize(pending.Payload));

        // The next champ select starts before match history has the game.
        var nextLobby = _live.BeginChampSelect("Jinx", "Caitlyn", "BOTTOM", "");
        _live.SetMood(1);

        _messenger.Send(new GameEndedMessage(Game(gameId), IsRecovered: true));
        await EventuallyAsync(() => _write.Games.GetAsync(gameId), "the recovered game is saved");

        var (mood, intention, events) = await SavedRowAsync(gameId);
        Assert.Equal(4, mood);
        Assert.Equal("Ward the river at 3:00", intention);
        Assert.Equal(1, events);
        // The new lobby keeps its own choices.
        Assert.Equal(nextLobby, _live.SessionKey);
        Assert.Equal(1, _live.PreGameMood);
    }

    [Fact]
    public async Task RecoveredGameWithoutItsOwnPreGameState_NeverBorrowsTheCurrentLobby()
    {
        const long gameId = 6_100_000_002;
        var lobby = _live.BeginChampSelect("Jinx", "Caitlyn", "BOTTOM", "");
        _live.SetMood(2);

        _messenger.Send(new GameEndedMessage(Game(gameId), IsRecovered: true));
        await EventuallyAsync(() => _write.Games.GetAsync(gameId), "the recovered game is saved");

        Assert.Equal(0, (await SavedRowAsync(gameId)).Mood);
        Assert.Equal(lobby, _live.SessionKey);
        Assert.Equal(2, _live.PreGameMood);
    }

    [Fact]
    public async Task WaitingGames_AreListed_SavedThroughTheGameEndPath_OrDismissedForGood()
    {
        const long older = 6_100_000_010, newer = 6_100_000_011;
        var (reader, sub) = _hub.Subscribe();
        using var _ = sub;

        _messenger.Send(new MissedReviewsDetectedMessage(
            [new MissedGameCandidate(older, 1_700_000_000, Game(older, timestamp: 1_700_000_000)),
             new MissedGameCandidate(newer, 1_700_100_000, Game(newer, "Lux", 1_700_100_000))]));
        Assert.Equal([newer, older], _flow.MissedGames().Select(g => g.GameId));
        Assert.Single(Drain(reader), e => e.Type == "missedReviews");

        // The same list again changes nothing on Home.
        _messenger.Send(new MissedReviewsDetectedMessage(
            [new MissedGameCandidate(older, 1_700_000_000, Game(older)), new MissedGameCandidate(newer, 1_700_100_000, Game(newer))]));
        Assert.DoesNotContain(Drain(reader), e => e.Type == "missedReviews");

        Assert.Equal(MissedGameOutcome.Saved, await _flow.SaveMissedGameAsync(newer));
        Assert.NotNull(await _write.Games.GetAsync(newer));
        Assert.Equal(1, (await SavedRowAsync(newer)).Events);
        Assert.Equal([older], _flow.MissedGames().Select(g => g.GameId));

        Assert.Equal(MissedGameOutcome.Dismissed, await _flow.DismissMissedGameAsync(older));
        Assert.Empty(_flow.MissedGames());
        Assert.Null(await _write.Games.GetAsync(older));
        var decisions = new MissedGameDecisionRepository(
            new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance, AppDataPaths.DatabasePath));
        Assert.Contains(older, await decisions.GetDismissedGameIdsAsync([older]));

        var published = Drain(reader).Where(e => e.Type == "missedReviews").ToList();
        Assert.Equal(2, published.Count);
        Assert.Contains("\"games\":[]", JsonSerializer.Serialize(published[^1].Payload, SidecarJson.CreateOptions()));

        Assert.Equal(MissedGameOutcome.NotFound, await _flow.SaveMissedGameAsync(older));
        Assert.Equal(MissedGameOutcome.NotFound, await _flow.DismissMissedGameAsync(6_100_000_099));
    }

    [Fact]
    public async Task ARecoveredGameAlreadySaved_IsNotSavedAgain()
    {
        const long gameId = 6_100_000_020;
        _messenger.Send(new MissedReviewsDetectedMessage([new MissedGameCandidate(gameId, 1_700_000_000, Game(gameId))]));
        Assert.Equal(MissedGameOutcome.Saved, await _flow.SaveMissedGameAsync(gameId));

        var withoutTimeline = Game(gameId);
        withoutTimeline.LiveEvents = [];
        _messenger.Send(new MissedReviewsDetectedMessage([new MissedGameCandidate(gameId, 1_700_000_000, withoutTimeline)]));
        Assert.Equal(MissedGameOutcome.Saved, await _flow.SaveMissedGameAsync(gameId));
        Assert.Equal(1, (await SavedRowAsync(gameId)).Events);
        Assert.Empty(_flow.MissedGames());
    }

    [Fact]
    public async Task TheListRouteAnswersOnAnIsolatedHost_AndSavingStaysDenied()
    {
        var root = Path.Combine(Path.GetTempPath(), "Revu.MissedGames." + Guid.NewGuid().ToString("N"));
        var session = new SidecarHostSession(Path.Combine(root, "LoLReviewData"), Path.Combine(root, "Revu"));
        try
        {
            const string token = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.Logging.ClearProviders();
            builder.Services.AddSidecarServices(isolatedHostTest: true);
            await using var app = builder.Build();
            app.UseSidecarApi(token, isolatedHostTest: true);
            app.MapSidecarEndpoints(SidecarJson.CreateOptions(), session);
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var list = await client.GetAsync("/api/missed-games");
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                Assert.Equal("{\"games\":[]}", await list.Content.ReadAsStringAsync());

                using var body = new StringContent("{\"gameId\":1,\"action\":\"save\"}", System.Text.Encoding.UTF8, "application/json");
                using var save = await client.PostAsync("/api/missed-games/resolve", body);
                Assert.False(save.IsSuccessStatusCode);
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Fakes: the monitor is constructed, never ticked ─────────────────────────
    private sealed class NoCredentials : ILcuCredentialDiscovery
    {
        public LcuCredentials? FindCredentials() => null;
        public LcuCredentials? FindFromProcess() => null;
        public LcuCredentials? FindFromLockfile() => null;
    }

    private sealed class ClosedGame : ILiveEventApi
    {
        public Task<string?> GetActivePlayerNameAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<JsonElement>?> FetchEventsAsync(CancellationToken ct = default) => Task.FromResult<List<JsonElement>?>(null);
    }

    private sealed class NoCapture : IGameEndCaptureService
    {
        public Task<GameStats?> CaptureAsync(IReadOnlyList<GameEvent> liveEvents, LiveRoster? roster, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameStats?>(null);
    }

    private sealed class NoHistory : IMatchHistoryReconciliationService
    {
        public Task<IReadOnlyList<MissedGameCandidate>> FindMissedGamesAsync(Func<long, bool>? checkGameSaved, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MissedGameCandidate>>([]);
    }

    private sealed class IdleLcu : ILcuClient
    {
        public void Configure(LcuCredentials credentials) { }
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<JsonElement?> GetCurrentSummonerAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<GamePhase> GetGameflowPhaseAsync(CancellationToken ct = default) => Task.FromResult(GamePhase.None);
        public Task<JsonElement?> GetEndOfGameStatsAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<bool> CancelMatchmakingAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> DeclineReadyCheckAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> GetLobbyQueueIdAsync(CancellationToken ct = default) => Task.FromResult(420);
        public Task<List<JsonElement>> GetMatchHistoryAsync(int begin = 0, int count = 5, CancellationToken ct = default) => Task.FromResult(new List<JsonElement>());
        public Task<JsonElement?> GetMatchDetailsAsync(long gameId, CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<string?> GetChampionNameAsync(int championId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<JsonElement?> GetRankedStatsAsync(CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);
        public Task<(string MyChampion, string EnemyLaner, string MyPosition)> GetChampSelectInfoAsync(CancellationToken ct = default) => Task.FromResult(("", "", ""));
        public Task<ChampSelectSnapshot> GetChampSelectSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new ChampSelectSnapshot("", "", "", new Dictionary<string, string>()));
    }
}
