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
/// one that writes there and uses its own game ids, fresh on every run so a data root kept
/// between runs (REVU_DATA_ROOT) never holds them already.
/// </summary>
public sealed class MissedGamesFlowTests : IAsyncLifetime
{
    private static readonly long RunBase = 6_100_000_000 + DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 1_000_000 * 100;
    private readonly StrongReferenceMessenger _messenger = new();
    private readonly SidecarEventHub _hub = new();
    private readonly LcuLiveState _live = new();
    private readonly SidecarBackgroundWork _work = new(NullLogger<SidecarBackgroundWork>.Instance);
    private WriteServices _write = null!;
    private GameMonitorService _monitor = null!;
    private SidecarGameFlowCoordinator _flow = null!;

    public async Task InitializeAsync()
    {
        if (!File.Exists(AppDataPaths.DatabasePath))
        {
            // The schema the sidecar's startup builds: a WAL database, the additive schema,
            // then the idempotent seeds and indexes (SidecarStartup).
            Directory.CreateDirectory(AppDataPaths.UserDataRoot);
            var factory = new SqliteConnectionFactory(NullLogger<SqliteConnectionFactory>.Instance, AppDataPaths.DatabasePath);
            using (var connection = factory.CreateConnection())
            using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                await wal.ExecuteNonQueryAsync();
            }
            var schema = new DatabaseInitializer(factory, NullLogger<DatabaseInitializer>.Instance);
            await schema.ApplyAdditiveSchemaAsync();
            await schema.ApplySeedsAndIndexesAsync();
        }

        _write = new WriteServices(NullLoggerFactory.Instance, _work);
        var lcu = new IdleLcu();
        _monitor = new GameMonitorService(new NoCredentials(), lcu, new ClosedGame(), new NoCapture(),
            new NoHistory(), _messenger, NullLogger<GameMonitorService>.Instance);
        var hardStop = new HardStopEnforcer(_write.Rules, _write.Games, _write.HardStops, lcu, _hub, _live,
            NullLogger<HardStopEnforcer>.Instance);
        _flow = new SidecarGameFlowCoordinator(_messenger, _hub, _live, _monitor, _write, hardStop,
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

    private static async Task<SidecarEventHub.SidecarEvent> NextEventAsync(
        System.Threading.Channels.ChannelReader<SidecarEventHub.SidecarEvent> reader, List<SidecarEventHub.SidecarEvent> seen, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            SidecarEventHub.SidecarEvent evt;
            try { evt = await reader.ReadAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"no {type} event; saw {string.Join(", ", seen.Select(e => e.Type))}");
            }
            seen.Add(evt);
            if (evt.Type == type) return evt;
        }
    }

    [Fact]
    public async Task DeferredCapture_IsSavedAsTheRecoveredGame_WithItsOwnPreGameChoicesAndTimeline()
    {
        var gameId = RunBase + 1;
        var (reader, sub) = _hub.Subscribe();
        using var _ = sub;

        _live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        _live.SetMood(4);
        _live.SetIntent("Ward the river at 3:00", "typed", cleared: false);
        _live.SetGameInProgress(true);

        // The game ends; its capture fails and the monitor hands it to reconciliation.
        _messenger.Send(new GameEndObservedMessage(gameId));
        _messenger.Send(new GameCaptureDeferredMessage(gameId));
        var pending = Assert.Single(Drain(reader), e => e.Type == "gameEnded");
        Assert.Contains("\"pending\":true", JsonSerializer.Serialize(pending.Payload));

        // The next champ select starts before match history has the game.
        var nextLobby = _live.BeginChampSelect("Jinx", "Caitlyn", "BOTTOM", "");
        _live.SetMood(1);

        _messenger.Send(new GameEndedMessage(Game(gameId), IsRecovered: true));
        // Its pre-game page stays up: the save is announced without the gameEnded event
        // that takes pages off the live surface.
        var seen = new List<SidecarEventHub.SidecarEvent>();
        var saved = await NextEventAsync(reader, seen, "gameRecovered");
        Assert.Contains("\"saved\":true", JsonSerializer.Serialize(saved.Payload));
        Assert.DoesNotContain(seen, e => e.Type == "gameEnded");

        var (mood, intention, events) = await SavedRowAsync(gameId);
        Assert.Equal(4, mood);
        Assert.Equal("Ward the river at 3:00", intention);
        Assert.Equal(1, events);
        // The new lobby keeps its own choices.
        Assert.Equal(nextLobby, _live.SessionKey);
        Assert.Equal(1, _live.PreGameMood);
    }

    [Fact]
    public async Task ALiveCaptureThatSettlesAfterTheNextChampSelectBegan_KeepsItsOwnPreGameChoices()
    {
        var gameId = RunBase + 3;
        var (reader, subscription) = _hub.Subscribe();
        using var unsubscribe = subscription;
        _live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        _live.SetMood(3);
        _live.SetIntent("Trade on cooldowns", "typed", cleared: false);

        // The game ends; its capture keeps retrying while the player queues again.
        _messenger.Send(new GameEndObservedMessage(gameId));
        var nextLobby = _live.BeginChampSelect("Jinx", "Caitlyn", "BOTTOM", "");
        _live.SetMood(1);

        _messenger.Send(new GameEndedMessage(Game(gameId)));
        var saved = await NextEventAsync(reader, [], "gameEnded");
        Assert.Contains("\"saved\":true", JsonSerializer.Serialize(saved.Payload));

        var (mood, intention, _) = await SavedRowAsync(gameId);
        Assert.Equal(3, mood);
        Assert.Equal("Trade on cooldowns", intention);
        Assert.Equal(nextLobby, _live.SessionKey);
        Assert.Equal(1, _live.PreGameMood);
    }

    [Fact]
    public async Task ASecondEndOfTheSameGame_KeepsItsFirstPreGameChoices_AndStillEndsTheGame()
    {
        var gameId = RunBase + 4;
        var (reader, subscription) = _hub.Subscribe();
        using var unsubscribe = subscription;
        _live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        _live.SetMood(5);

        // A collector finished on a wrong guess that the game was over; the game went on.
        _messenger.Send(new GameEndObservedMessage(gameId));
        _live.SetGameInProgress(true);
        _messenger.Send(new GameEndObservedMessage(gameId)); // the real end

        Assert.False(_live.IsGameInProgress);
        _messenger.Send(new GameEndedMessage(Game(gameId)));
        await NextEventAsync(reader, [], "gameEnded");
        Assert.Equal(5, (await SavedRowAsync(gameId)).Mood);
    }

    [Fact]
    public void ACasualGameEnd_ClosesItsFlow_SoPagesLeaveTheInGameSurface()
    {
        var (reader, sub) = _hub.Subscribe();
        using var _ = sub;
        _live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        _live.SetGameInProgress(true);

        _messenger.Send(new GameEndObservedMessage(0, IsCasual: true));

        Assert.False(_live.IsGameInProgress);
        Assert.Null(_live.SessionKey);
        var ended = Assert.Single(Drain(reader), e => e.Type == "gameEnded");
        Assert.Contains("\"saved\":false", JsonSerializer.Serialize(ended.Payload));
    }

    [Fact]
    public async Task RecoveredGameWithoutItsOwnPreGameState_NeverBorrowsTheCurrentLobby()
    {
        var gameId = RunBase + 2;
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
        long older = RunBase + 10, newer = RunBase + 11;
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
        Assert.Equal(MissedGameOutcome.NotFound, await _flow.DismissMissedGameAsync(RunBase + 99));
    }

    [Fact]
    public async Task ARecoveredGameAlreadySaved_IsNotSavedAgain()
    {
        var gameId = RunBase + 20;
        _messenger.Send(new MissedReviewsDetectedMessage([new MissedGameCandidate(gameId, 1_700_000_000, Game(gameId))]));
        Assert.Equal(MissedGameOutcome.Saved, await _flow.SaveMissedGameAsync(gameId));

        var withoutTimeline = Game(gameId);
        withoutTimeline.LiveEvents = [];
        _messenger.Send(new MissedReviewsDetectedMessage([new MissedGameCandidate(gameId, 1_700_000_000, withoutTimeline)]));
        Assert.Equal(MissedGameOutcome.Saved, await _flow.SaveMissedGameAsync(gameId));
        Assert.Equal(1, (await SavedRowAsync(gameId)).Events);
        Assert.Empty(_flow.MissedGames());
    }

    // What Settings recovery reads from match history: fresh stats, no live timeline.
    private static MissedGameCandidate FromHistory(long gameId)
    {
        var stats = Game(gameId);
        stats.LiveEvents = [];
        return new MissedGameCandidate(gameId, stats.Timestamp, stats);
    }

    [Fact]
    public async Task SettingsRecovery_SavesTheTrackedGameWithItsPreGameChoices_AndAWaitingGameWithItsTimeline()
    {
        long tracked = RunBase + 30, waiting = RunBase + 31;
        var (reader, sub) = _hub.Subscribe();
        using var _ = sub;

        _live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        _live.SetMood(4);
        _live.SetIntent("Ward the river at 3:00", "typed", cleared: false);
        _live.SetGameInProgress(true);
        // The tracked game's capture failed; an older game waits on Home with its timeline.
        _messenger.Send(new GameEndObservedMessage(tracked));
        _messenger.Send(new GameCaptureDeferredMessage(tracked));
        _messenger.Send(new MissedReviewsDetectedMessage(
            [new MissedGameCandidate(waiting, 1_700_000_000, Game(waiting, "Lux", 1_700_000_000))]));
        Drain(reader);

        Assert.Equal(RecoveredGameSave.Saved, await _flow.SaveRecoveredGameAsync(FromHistory(tracked), default));
        var (mood, intention, _) = await SavedRowAsync(tracked);
        Assert.Equal(4, mood);
        Assert.Equal("Ward the river at 3:00", intention);

        Assert.Equal(RecoveredGameSave.Saved, await _flow.SaveRecoveredGameAsync(FromHistory(waiting), default));
        Assert.Equal(1, (await SavedRowAsync(waiting)).Events);
        Assert.Empty(_flow.MissedGames());

        var seen = Drain(reader);
        // Home's card drops the saved game at once.
        Assert.Contains("\"games\":[]",
            JsonSerializer.Serialize(seen.Last(e => e.Type == "missedReviews").Payload, SidecarJson.CreateOptions()));
        // The older game is announced without moving the first-review guide or the live pages.
        Assert.Contains(seen, e => e.Type == "gameRecovered" && JsonSerializer.Serialize(e.Payload).Contains($"\"gameId\":{waiting}"));
        Assert.DoesNotContain(seen, e => e.Type == "gameEnded" && JsonSerializer.Serialize(e.Payload).Contains($"\"gameId\":{waiting}"));

        // Already saved: neither saved again nor reported as recovered.
        Assert.Equal(RecoveredGameSave.NotSaved, await _flow.SaveRecoveredGameAsync(FromHistory(waiting), default));
    }

    [Fact]
    public async Task SettingsRecovery_LeavesAGameTheMonitorHandedOver_ToThatSave()
    {
        var gameId = RunBase + 32;
        _messenger.Send(new MissedReviewsDetectedMessage([new MissedGameCandidate(gameId, 1_700_000_000, Game(gameId))]));
        Assert.True(_monitor.TryClaimForSave(Game(gameId), out _)); // another save holds the game

        Assert.Equal(RecoveredGameSave.NotSaved, await _flow.SaveRecoveredGameAsync(FromHistory(gameId), default));
        Assert.Null(await _write.Games.GetAsync(gameId));
        Assert.Empty(_flow.MissedGames()); // that save lands it; it no longer waits
    }

    [Fact]
    public async Task HomeSaveAndSettingsRecoveryAtOnce_SaveTheGameOnce()
    {
        var gameId = RunBase + 33;
        _messenger.Send(new MissedReviewsDetectedMessage([new MissedGameCandidate(gameId, 1_700_000_000, Game(gameId))]));

        var home = _flow.SaveMissedGameAsync(gameId);
        var settings = _flow.SaveRecoveredGameAsync(FromHistory(gameId), default);
        await Task.WhenAll(home, settings).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True((await home == MissedGameOutcome.Saved) ^ (await settings == RecoveredGameSave.Saved));
        Assert.Equal(1, (await SavedRowAsync(gameId)).Events);
        Assert.Empty(_flow.MissedGames());
    }

    [Fact]
    public async Task SavingAnOlderGameFromHome_IsAnnouncedAsRecovered_NotAsTheGameJustPlayed()
    {
        var gameId = RunBase + 34;
        var (reader, sub) = _hub.Subscribe();
        using var _ = sub;
        _messenger.Send(new MissedReviewsDetectedMessage([new MissedGameCandidate(gameId, 1_700_000_000, Game(gameId))]));

        Assert.Equal(MissedGameOutcome.Saved, await _flow.SaveMissedGameAsync(gameId));

        var seen = Drain(reader);
        var saved = Assert.Single(seen, e => e.Type == "gameRecovered");
        Assert.Contains("\"saved\":true", JsonSerializer.Serialize(saved.Payload));
        Assert.DoesNotContain(seen, e => e.Type == "gameEnded");
    }

    [Fact]
    public async Task AGameWhoseSaveFails_WaitsOnHomeAgain_WithItsPreGameChoicesAndTimeline()
    {
        var gameId = RunBase + 35;
        var (reader, sub) = _hub.Subscribe();
        using var _ = sub;
        _live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        _live.SetMood(3);
        _messenger.Send(new GameEndObservedMessage(gameId));

        // The live save fails (here: a timestamp the database write cannot store).
        var stats = Game(gameId);
        var playedAt = stats.Timestamp;
        stats.Timestamp = long.MaxValue;
        _messenger.Send(new GameEndedMessage(stats));
        await NextEventAsync(reader, [], "missedReviews");
        Assert.Null(await _write.Games.GetAsync(gameId));
        Assert.Equal([gameId], _flow.MissedGames().Select(g => g.GameId));

        // Saved from Home once the fault is gone, with the choices made before the game.
        stats.Timestamp = playedAt;
        Assert.Equal(MissedGameOutcome.Saved, await _flow.SaveMissedGameAsync(gameId));
        var (mood, _, events) = await SavedRowAsync(gameId);
        Assert.Equal(3, mood);
        Assert.Equal(1, events);
        Assert.Empty(_flow.MissedGames());
    }

    [Fact]
    public async Task ASettingsRecoveryWhoseSaveFails_Throws_AndTheGameWaitsOnHome()
    {
        var gameId = RunBase + 36;
        var candidate = FromHistory(gameId);
        var playedAt = candidate.Stats.Timestamp;
        candidate.Stats.Timestamp = long.MaxValue;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _flow.SaveRecoveredGameAsync(candidate, default));
        Assert.Equal([gameId], _flow.MissedGames().Select(g => g.GameId));

        candidate.Stats.Timestamp = playedAt;
        Assert.Equal(MissedGameOutcome.Saved, await _flow.SaveMissedGameAsync(gameId));
        Assert.NotNull(await _write.Games.GetAsync(gameId));
    }

    [Fact]
    public async Task ATrackedGameWhoseSettingsSaveFailed_KeepsItsTimeline_WhenItsAutomaticSaveLands()
    {
        var gameId = RunBase + 37;
        _live.BeginChampSelect("Ahri", "Zed", "MIDDLE", "");
        _live.SetMood(4);
        _live.SetGameInProgress(true);
        _messenger.Send(new GameEndObservedMessage(gameId));
        _messenger.Send(new GameCaptureDeferredMessage(gameId));

        // Settings recovery takes the tracked game with the timeline the monitor kept (Game
        // carries one kill), and its save fails: the timeline now waits on Home, not in the
        // monitor's store.
        var claimed = Game(gameId);
        var playedAt = claimed.Timestamp;
        claimed.Timestamp = long.MaxValue;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _flow.SaveRecoveredGameAsync(new MissedGameCandidate(gameId, playedAt, claimed), default));
        Assert.Equal([gameId], _flow.MissedGames().Select(g => g.GameId));
        claimed.Timestamp = playedAt; // the fault is gone

        // The monitor's search still targets the game: match history lists it, with no timeline
        // left in the store, and the automatic save lands.
        var (reader, sub) = _hub.Subscribe();
        using var _ = sub;
        _messenger.Send(new GameEndedMessage(FromHistory(gameId).Stats, IsRecovered: true));
        var saved = await NextEventAsync(reader, [], "gameEnded");
        Assert.Contains("\"saved\":true", JsonSerializer.Serialize(saved.Payload));

        var (mood, _, events) = await SavedRowAsync(gameId);
        Assert.Equal(4, mood);
        Assert.Equal(1, events);
        Assert.Empty(_flow.MissedGames());
    }

    [Fact]
    public void ACaptureDeferredWithAnUnknownGameId_StillTakesPagesOffTheInGameSurface()
    {
        var (reader, sub) = _hub.Subscribe();
        using var _ = sub;
        _live.SetGameInProgress(true);

        _messenger.Send(new GameEndObservedMessage(0));
        _messenger.Send(new GameCaptureDeferredMessage(0));

        var ended = JsonSerializer.Serialize(Assert.Single(Drain(reader), e => e.Type == "gameEnded").Payload);
        Assert.Contains("\"pending\":true", ended);
        Assert.Contains("\"gameId\":null", ended);
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
