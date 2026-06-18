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

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () =>
            {
                if (snapshots.Count > 1)
                {
                    return snapshots.Dequeue();
                }

                return snapshots.Peek();
            });
        var collector = new LiveEventCollector(
            api,
            NullLogger.Instance,
            pollInterval: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);

        await Task.Delay(35);
        await cts.CancelAsync();
        await runTask;

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

    [Fact]
    public async Task StopAsync_SynthesisesFlashCast_WhenSpell1CooldownGoesFromReadyToOnCooldown()
    {
        // Single growing event stream so the collector's clock has a non-zero reading.
        var events = CreateEvents(
            """
            [
              { "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 },
              { "EventID": 0, "EventName": "MinionsSpawning", "EventTime": 65.0 }
            ]
            """);

        // Sequence of active-player snapshots: first one establishes ready state,
        // second one shows Flash on cooldown — i.e. it was just cast.
        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer(spell1Name: "Flash", spell1Cd: 0.0, spell2Name: "Ignite", spell2Cd: 0.0, gameTime: 120.0),
            CreateActivePlayer(spell1Name: "Flash", spell1Cd: 290.0, spell2Name: "Ignite", spell2Cd: 0.0, gameTime: 130.0),
        ]);

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchActivePlayerAsync: () => snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek());

        var collector = new LiveEventCollector(api, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        await Task.Delay(60);
        await cts.CancelAsync();
        await runTask;

        var parsed = await collector.StopAsync();
        var flashCast = parsed.SingleOrDefault(e => e.EventType == GameEvent.EventTypes.Flash);

        Assert.NotNull(flashCast);
        Assert.True(flashCast!.GameTimeS > 0, $"Expected non-zero game time, got {flashCast.GameTimeS}");
        using var details = JsonDocument.Parse(flashCast.Details);
        Assert.Equal("Flash", details.RootElement.GetProperty("spell").GetString());
        Assert.Equal("spell1", details.RootElement.GetProperty("slot").GetString());
    }

    [Fact]
    public async Task StopAsync_SynthesisesGenericSummonerSpell_WhenNonFlashCastsRecorded()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer(spell1Name: "Flash", spell1Cd: 0.0, spell2Name: "Ignite", spell2Cd: 0.0, gameTime: 80.0),
            CreateActivePlayer(spell1Name: "Flash", spell1Cd: 0.0, spell2Name: "Ignite", spell2Cd: 180.0, gameTime: 95.0),
        ]);

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchActivePlayerAsync: () => snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek());

        var collector = new LiveEventCollector(api, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(10));

        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        await Task.Delay(60);
        await cts.CancelAsync();
        await runTask;

        var parsed = await collector.StopAsync();
        var igniteCast = parsed.SingleOrDefault(e => e.EventType == GameEvent.EventTypes.SummonerSpell);

        Assert.NotNull(igniteCast);
        using var details = JsonDocument.Parse(igniteCast!.Details);
        Assert.Equal("Ignite", details.RootElement.GetProperty("spell").GetString());
        Assert.Equal("spell2", details.RootElement.GetProperty("slot").GetString());
    }

    private static JsonElement CreateActivePlayer(string spell1Name, double spell1Cd, string spell2Name, double spell2Cd, double gameTime, double currentGold = 500.0)
    {
        var json = $$"""
            {
              "gameTime": {{gameTime}},
              "currentGold": {{currentGold}},
              "summonerSpells": {
                "summonerSpellOne": { "displayName": "{{spell1Name}}", "rawCooldown": {{spell1Cd}} },
                "summonerSpellTwo": { "displayName": "{{spell2Name}}", "rawCooldown": {{spell2Cd}} }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task StopAsync_DerivesRecall_WhenGoldDropsLikeAShopPurchase()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // Gold goes 1300 → 200 (spent 1100) at game time 308s: a shop purchase ⇒ the
        // player just recalled. The recall is anchored 8s before the purchase = 300s.
        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer("Flash", 0.0, "Ignite", 0.0, gameTime: 300.0, currentGold: 1300.0),
            CreateActivePlayer("Flash", 0.0, "Ignite", 0.0, gameTime: 308.0, currentGold: 200.0),
        ]);

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchActivePlayerAsync: () => snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek());

        var collector = new LiveEventCollector(api, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        await Task.Delay(60);
        await cts.CancelAsync();
        await runTask;

        var parsed = await collector.StopAsync();
        var recall = parsed.SingleOrDefault(e => e.EventType == GameEvent.EventTypes.Recall);

        Assert.NotNull(recall);
        Assert.Equal(300, recall!.GameTimeS); // 308 purchase − 8s channel
        using var details = JsonDocument.Parse(recall.Details);
        Assert.True(details.RootElement.GetProperty("detected").GetBoolean());
        Assert.Equal(1100, details.RootElement.GetProperty("gold_spent").GetInt32());
    }

    [Fact]
    public async Task StopAsync_DoesNotDeriveRecall_ForSmallGoldDrops()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // Gold 400 → 325 (spent 75 — a control ward bought in lane, no recall). Below
        // the 250g threshold, so no RECALL is emitted.
        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer("Flash", 0.0, "Ignite", 0.0, gameTime: 200.0, currentGold: 400.0),
            CreateActivePlayer("Flash", 0.0, "Ignite", 0.0, gameTime: 210.0, currentGold: 325.0),
        ]);

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchActivePlayerAsync: () => snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek());

        var collector = new LiveEventCollector(api, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        await Task.Delay(60);
        await cts.CancelAsync();
        await runTask;

        var parsed = await collector.StopAsync();
        Assert.DoesNotContain(parsed, e => e.EventType == GameEvent.EventTypes.Recall);
    }

    [Fact]
    public async Task StopAsync_DebouncesRecall_AcrossConsecutiveFountainBuys()
    {
        var events = CreateEvents("""[{ "EventID": 0, "EventName": "GameStart", "EventTime": 0.0 }]""");

        // Two big buys close together (one back, several purchases): 1500→300 at 300s,
        // then 900→100 at 305s. Within the 25s debounce window ⇒ only ONE recall.
        var snapshots = new Queue<JsonElement>(
        [
            CreateActivePlayer("Flash", 0.0, "Ignite", 0.0, gameTime: 295.0, currentGold: 1500.0),
            CreateActivePlayer("Flash", 0.0, "Ignite", 0.0, gameTime: 300.0, currentGold: 300.0),
            CreateActivePlayer("Flash", 0.0, "Ignite", 0.0, gameTime: 305.0, currentGold: 100.0),
        ]);

        var api = new FakeLiveEventApi(
            fetchEventsAsync: () => events,
            fetchActivePlayerAsync: () => snapshots.Count > 1 ? snapshots.Dequeue() : snapshots.Peek());

        var collector = new LiveEventCollector(api, NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();
        var runTask = collector.StartAsync(cts.Token);
        await Task.Delay(80);
        await cts.CancelAsync();
        await runTask;

        var parsed = await collector.StopAsync();
        Assert.Single(parsed, e => e.EventType == GameEvent.EventTypes.Recall);
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

    private sealed class FakeLiveEventApi : ILiveEventApi
    {
        private readonly Func<List<JsonElement>> _fetchEventsAsync;
        private readonly Func<JsonElement?>? _fetchActivePlayerAsync;

        public FakeLiveEventApi(
            Func<List<JsonElement>> fetchEventsAsync,
            Func<JsonElement?>? fetchActivePlayerAsync = null)
        {
            _fetchEventsAsync = fetchEventsAsync;
            _fetchActivePlayerAsync = fetchActivePlayerAsync;
        }

        public Task<string?> GetActivePlayerNameAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("Tester");

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<JsonElement>?> FetchEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<List<JsonElement>?>(_fetchEventsAsync());

        public Task<JsonElement?> FetchActivePlayerAsync(CancellationToken ct = default) =>
            Task.FromResult(_fetchActivePlayerAsync?.Invoke());
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
