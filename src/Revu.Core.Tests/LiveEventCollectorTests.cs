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

        public FakeLiveEventApi(Func<List<JsonElement>> fetchEventsAsync)
        {
            _fetchEventsAsync = fetchEventsAsync;
        }

        public Task<string?> GetActivePlayerNameAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("Tester");

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<JsonElement>?> FetchEventsAsync(CancellationToken ct = default) =>
            Task.FromResult<List<JsonElement>?>(_fetchEventsAsync());
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
