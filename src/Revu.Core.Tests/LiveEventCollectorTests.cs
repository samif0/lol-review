using System.Net;
using System.Text;
using System.Text.Json;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

public sealed class LiveEventCollectorTests
{
    [Fact]
    public void AppendNewRawEvents_AppendsByPositionWhenEventIdsRepeat()
    {
        var destination = new List<JsonElement>();
        var firstSnapshot = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
              { "EventID": 0, "EventName": "MinionsSpawning", "EventTime": 65.0 }
            ]
            """);
        var secondSnapshot = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
              { "EventID": 0, "EventName": "MinionsSpawning", "EventTime": 65.0 },
              { "EventID": 0, "EventName": "ChampionKill", "EventTime": 200.0, "KillerName": "Tester", "VictimName": "Enemy", "Assisters": [] },
              { "EventID": 0, "EventName": "DragonKill", "EventTime": 610.0, "DragonType": "Earth", "Stolen": false, "KillerName": "Tester" }
            ]
            """);

        LiveEventCollector.AppendNewRawEvents(destination, firstSnapshot);
        LiveEventCollector.AppendNewRawEvents(destination, secondSnapshot);

        Assert.Equal(4, destination.Count);
        Assert.Equal("ChampionKill", destination[2].GetProperty("EventName").GetString());
        Assert.Equal("DragonKill", destination[3].GetProperty("EventName").GetString());
    }

    [Fact]
    public void ParseLiveEvents_CapturesKillerAndAssistersOnDeath()
    {
        // A ChampionKill where the player is the victim, with two assisters. The DEATH
        // event must carry both killer and the assisters array (the jungle-gank signal).
        var raw = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "ChampionKill", "EventTime": 300.0,
                "KillerName": "EnemyMid", "VictimName": "Tester",
                "Assisters": ["EnemyJungler", "EnemySupport"] }
            ]
            """);

        var parsed = LiveEventCollector.ParseLiveEvents(raw, "Tester");
        var death = parsed.Single(e => e.EventType == GameEvent.EventTypes.Death);

        using var details = JsonDocument.Parse(death.Details);
        Assert.Equal("EnemyMid", details.RootElement.GetProperty("killer").GetString());
        var assisters = details.RootElement.GetProperty("assisters").EnumerateArray()
            .Select(a => a.GetString()).ToArray();
        Assert.Equal(new[] { "EnemyJungler", "EnemySupport" }, assisters);
    }

    [Fact]
    public void ParseLiveEvents_MatchesRiotIdPlayerName_AgainstBareFeedName()
    {
        // Modern clients return "GameName#TAG" from /activeplayername while the
        // kill feed usually carries the bare game name. Attribution must survive
        // the form divergence in BOTH directions — before the fix these events
        // silently vanished from the timeline.
        var raw = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "ChampionKill", "EventTime": 120.0,
                "KillerName": "Tester", "VictimName": "EnemyTop", "Assisters": [] },
              { "EventID": 0, "EventName": "ChampionKill", "EventTime": 300.0,
                "KillerName": "EnemyMid", "VictimName": "Tester#NA1", "Assisters": [] }
            ]
            """);

        var parsed = LiveEventCollector.ParseLiveEvents(raw, "Tester#NA1");

        Assert.Single(parsed, e => e.EventType == GameEvent.EventTypes.Kill);
        Assert.Single(parsed, e => e.EventType == GameEvent.EventTypes.Death);
    }

    [Theory]
    [InlineData("Tester", "Tester#NA1", true)]
    [InlineData("Tester#NA1", "Tester", true)]
    [InlineData("tester#na1", "TESTER#NA1", true)]
    [InlineData("Other", "Tester#NA1", false)]
    // Two FULLY-QUALIFIED Riot IDs sharing a game name are different players —
    // the bare-name fallback must not merge them.
    [InlineData("Smurf#111", "Smurf#222", false)]
    [InlineData("", "Tester", false)]
    public void NamesMatch_ToleratesRiotIdTagDrift(string feedName, string playerName, bool expected)
    {
        Assert.Equal(expected, LiveEventCollector.NamesMatch(feedName, playerName));
    }

    [Fact]
    public async Task StopAsync_CollectsObjectiveAndPlayerEvents_WhenSnapshotsGrow()
    {
        var snapshots = new Queue<List<JsonElement>>(
        [
            CreateEvents(
                """
                [
                  { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }
                ]
                """),
            CreateEvents(
                """
                [
                  { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
                  { "EventID": 0, "EventName": "TurretKilled", "EventTime": 420.0, "TurretKilled": "Turret_T2_L_03_A", "KillerName": "Ally" },
                  { "EventID": 0, "EventName": "ChampionKill", "EventTime": 510.0, "KillerName": "Tester", "VictimName": "Enemy", "Assisters": [] },
                  { "EventID": 0, "EventName": "DragonKill", "EventTime": 610.0, "DragonType": "Earth", "Stolen": false, "KillerName": "Ally" }
                ]
                """)
        ]);

        // The roster fetch follows the event poll in the same cycle, so it proves the
        // first snapshot was appended; StopAsync's final poll then reads the grown one.
        var firstPollProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeLiveEventApi(
            fetchEventsAsync: () =>
            {
                if (snapshots.Count > 1)
                {
                    return snapshots.Dequeue();
                }

                return snapshots.Peek();
            },
            fetchPlayerListAsync: () =>
            {
                firstPollProcessed.TrySetResult();
                return null;
            });
        var collector = new LiveEventCollector(
            api,
            NullLogger.Instance,
            pollInterval: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);

        try { await firstPollProcessed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            await cts.CancelAsync();
            await runTask;
        }

        var parsedEvents = await collector.StopAsync();

        Assert.Collection(
            parsedEvents,
            evt =>
            {
                Assert.Equal(GameEvent.EventTypes.Turret, evt.EventType);
                Assert.Equal(420, evt.GameTimeS);
            },
            evt =>
            {
                Assert.Equal(GameEvent.EventTypes.Kill, evt.EventType);
                Assert.Equal(510, evt.GameTimeS);
            },
            evt =>
            {
                Assert.Equal(GameEvent.EventTypes.Dragon, evt.EventType);
                Assert.Equal(610, evt.GameTimeS);
            });
    }

    /// <summary>
    /// The production collector carries the live processing catalog, whose detectors
    /// are all unavailable, so nothing asks for player health: the loop never fetches
    /// /activeplayer or /gamestats and wakes on the kill-feed cadence, not every second.
    /// </summary>
    [Fact]
    public async Task StartAsync_WithTheProductionCatalog_NeverSamplesHealth()
    {
        var events = CreateEvents("""[ { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 } ]""");
        var eventCalls = 0;
        var healthCalls = 0;
        var thirdPollProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => { Interlocked.Increment(ref eventCalls); return events; },
            fetchActivePlayerAsync: () => { Interlocked.Increment(ref healthCalls); return null; },
            fetchGameStatsAsync: () => { Interlocked.Increment(ref healthCalls); return null; },
            fetchPlayerListAsync: () =>
            {
                if (Volatile.Read(ref eventCalls) >= 3) thirdPollProcessed.TrySetResult();
                return null;
            });
        var processing = Revu.Core.Services.EventProcessing.LiveProcessingCatalog.Create([new(1, "TRADE")]);
        var collector = new LiveEventCollector(
            api,
            NullLogger.Instance,
            // An hour-long health cadence: only the event cadence can drive three polls.
            pollInterval: TimeSpan.FromHours(1),
            eventPollInterval: TimeSpan.FromMilliseconds(10),
            processing: processing);

        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        try { await thirdPollProcessed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally
        {
            await cts.CancelAsync();
            await runTask;
        }
        var parsed = await collector.StopAsync();

        Assert.Equal(0, Volatile.Read(ref healthCalls));
        Assert.Empty(parsed);
        Assert.NotNull(collector.ProcessingReport);
    }

    [Fact]
    public async Task GetActivePlayerNameAsync_PrefersActivePlayerNameEndpoint()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var payload = request.RequestUri!.AbsolutePath switch
            {
                "/liveclientdata/activeplayername" => "\"Riot Tuxedo\"",
                "/liveclientdata/activeplayer" => """
                    {
                      "riotId": "Riot Tuxedo#TXC1",
                      "riotIdGameName": "Ignored Fallback",
                      "summonerName": "Ignored Summoner"
                    }
                    """,
                _ => throw new InvalidOperationException($"Unexpected request path {request.RequestUri.AbsolutePath}")
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var api = new LiveEventApi(httpClient, NullLogger<LiveEventApi>.Instance);

        var playerName = await api.GetActivePlayerNameAsync();

        Assert.Equal("Riot Tuxedo", playerName);
    }

    [Fact]
    public void ResolveActivePlayerName_PrefersGameNameOverFullRiotId()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "riotId": "Riot Tuxedo#TXC1",
              "riotIdGameName": "Riot Tuxedo",
              "summonerName": "LegacyName"
            }
            """);

        var playerName = LiveEventApi.ResolveActivePlayerName(document.RootElement);

        Assert.Equal("Riot Tuxedo", playerName);
    }

    private static List<JsonElement> CreateEvents(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(static item => item.Clone())];
    }

    /// <summary>
    /// v3.10.1: the lobby is read from /playerlist on the event cadence and exposed
    /// as <see cref="LiveEventCollector.Roster"/>, so game end knows every lane the
    /// matchmaker assigned. The fetch stops once the ten champions are named.
    /// </summary>
    [Fact]
    public async Task StartAsync_CapturesTheRosterFromPlayerList_AndStopsOnceComplete()
    {
        var events = CreateEvents("""[ { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 } ]""");
        var lanes = new[] { "TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY" };
        var own = new[] { "Teemo", "Zaahen", "Riven", "Miss Fortune", "Pantheon" };
        var enemy = new[] { "Gragas", "Hecarim", "Swain", "Yasuo", "Soraka" };
        var rows = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            rows.Add($$$"""{ "championName": "{{{own[i]}}}", "position": "{{{lanes[i]}}}", "team": "ORDER" }""");
            rows.Add($$$"""{ "championName": "{{{enemy[i]}}}", "position": "{{{lanes[i]}}}", "team": "CHAOS" }""");
        }
        using var playerList = JsonDocument.Parse("[" + string.Join(",", rows) + "]");
        var playerListCalls = 0;
        var eventCalls = 0;

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => { Interlocked.Increment(ref eventCalls); return events; },
            fetchPlayerListAsync: () => { Interlocked.Increment(ref playerListCalls); return playerList.RootElement.Clone(); });
        var collector = new LiveEventCollector(
            api,
            NullLogger.Instance,
            pollInterval: TimeSpan.FromMilliseconds(5),
            eventPollInterval: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        // Wait on the condition, not the wall clock (CI runners are slow): the roster
        // landed AND several more event cycles ran, so a re-fetch would have shown up.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((collector.Roster is null || Volatile.Read(ref eventCalls) < 4) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        await cts.CancelAsync();
        await runTask;
        await collector.StopAsync();

        var roster = collector.Roster;
        Assert.NotNull(roster);
        Assert.True(roster!.IsComplete);
        Assert.Equal("BOTTOM", roster.PositionOf("Miss Fortune", 100));
        Assert.Equal("BOTTOM", roster.PositionOf("Yasuo", 200));
        Assert.Equal(1, playerListCalls); // complete on the first answer → never asked again
    }

    /// <summary>The loading screen answers with a short list first; the fuller one replaces it.</summary>
    [Fact]
    public async Task StartAsync_KeepsTheFullestRosterSeen()
    {
        var events = CreateEvents("""[ { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 } ]""");
        using var partial = JsonDocument.Parse("""[ { "championName": "Miss Fortune", "position": "BOTTOM", "team": "ORDER" } ]""");
        using var full = JsonDocument.Parse("""
            [ { "championName": "Miss Fortune", "position": "BOTTOM", "team": "ORDER" },
              { "championName": "Yasuo", "position": "BOTTOM", "team": "CHAOS" } ]
            """);
        // Short list, then the full one, then the short list again forever: the full
        // one must replace the first and survive the later short answers.
        var answers = new Queue<JsonElement>([partial.RootElement.Clone(), full.RootElement.Clone(), partial.RootElement.Clone()]);
        var playerListCalls = 0;

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchPlayerListAsync: () =>
            {
                Interlocked.Increment(ref playerListCalls);
                return answers.Count > 1 ? answers.Dequeue() : answers.Peek();
            });
        var collector = new LiveEventCollector(
            api,
            NullLogger.Instance,
            pollInterval: TimeSpan.FromMilliseconds(5),
            eventPollInterval: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (((collector.Roster?.Players.Count ?? 0) < 2 || Volatile.Read(ref playerListCalls) < 5)
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        await cts.CancelAsync();
        await runTask;

        Assert.True(Volatile.Read(ref playerListCalls) >= 5, "the collector kept offering the short list");
        Assert.NotNull(collector.Roster);
        Assert.Equal(2, collector.Roster!.Players.Count);
        Assert.Equal("BOTTOM", collector.Roster.PositionOf("Yasuo", 200));
    }

    private sealed class FakeLiveEventApi : ILiveEventApi
    {
        private readonly Func<List<JsonElement>> _fetchEventsAsync;
        private readonly Func<JsonElement?>? _fetchActivePlayerAsync;
        private readonly Func<JsonElement?>? _fetchGameStatsAsync;
        private readonly Func<JsonElement?>? _fetchPlayerListAsync;

        public FakeLiveEventApi(
            Func<List<JsonElement>> fetchEventsAsync,
            Func<JsonElement?>? fetchActivePlayerAsync = null,
            Func<JsonElement?>? fetchGameStatsAsync = null,
            Func<JsonElement?>? fetchPlayerListAsync = null)
        {
            _fetchEventsAsync = fetchEventsAsync;
            _fetchActivePlayerAsync = fetchActivePlayerAsync;
            _fetchGameStatsAsync = fetchGameStatsAsync;
            _fetchPlayerListAsync = fetchPlayerListAsync;
        }

        public Task<JsonElement?> FetchPlayerListAsync(CancellationToken ct = default) =>
            Task.FromResult(_fetchPlayerListAsync?.Invoke());

        public Task<string?> GetActivePlayerNameAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("Tester");

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<JsonElement>?> FetchEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<List<JsonElement>?>(_fetchEventsAsync());

        public Task<JsonElement?> FetchActivePlayerAsync(CancellationToken ct = default) =>
            Task.FromResult(_fetchActivePlayerAsync?.Invoke());

        public Task<JsonElement?> FetchGameStatsAsync(CancellationToken ct = default) =>
            Task.FromResult(_fetchGameStatsAsync?.Invoke());
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
