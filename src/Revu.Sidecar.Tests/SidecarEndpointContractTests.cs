using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class SidecarEndpointContractTests
{
    [Fact]
    public async Task EveryExistingMethodAndPathStillBindsExactlyOnce()
    {
        using var scratch = new ScratchHost();
        await using var app = BuildApp(scratch);
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        var actual = routes.SelectMany(endpoint => endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>()
            .HttpMethods.Select(method => method + " " + endpoint.RoutePattern.RawText)).Order().ToArray();
        var expected = ExpectedRoutes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order().ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct().Count());
        Assert.All(routes, endpoint => Assert.NotNull(endpoint.RequestDelegate));
        Assert.False(File.Exists(Path.Combine(scratch.Root, "LoLReviewData", "revu.db")));
    }

    [Fact]
    public async Task ShutdownRequiresBearerAndCompletesItsResponseBeforeStoppingAnIsolatedHost()
    {
        using var scratch = new ScratchHost();
        await using var app = BuildApp(scratch);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var unauthorized = await client.PostAsync("/api/host/shutdown", null);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            Assert.False(app.Lifetime.ApplicationStopping.IsCancellationRequested);
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = app.Lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var response = await client.PostAsync("/api/host/shutdown", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public void ResponseOptionsRetainCamelCaseNullsAndUnescapedDisplayText()
    {
        Assert.Equal("{\"displayName\":\"<Revu>\",\"note\":null}",
            JsonSerializer.Serialize(new { DisplayName = "<Revu>", Note = (string?)null }, SidecarJson.CreateOptions()));
    }

    [Theory]
    [InlineData("GET", "/api/recording/context")]
    [InlineData("GET", "/api/recording/session/01234567-89ab-cdef-0123-456789abcdef")]
    [InlineData("POST", "/api/recording/register")]
    public async Task RecordingEndpointsRequireThePrivateHostBearer(string method, string path)
    {
        using var scratch = new ScratchHost();
        await using var app = BuildApp(scratch);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private const string Token = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private static WebApplication BuildApp(ScratchHost scratch)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Services.AddSidecarServices(isolatedHostTest: true);
        var app = builder.Build();
        app.UseSidecarApi(Token, isolatedHostTest: true);
        app.MapSidecarEndpoints(SidecarJson.CreateOptions(), scratch.Session);
        return app;
    }

    private sealed class ScratchHost : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Revu.EndpointContracts." + Guid.NewGuid().ToString("N"));
        public SidecarHostSession Session { get; }
        public ScratchHost() => Session = new SidecarHostSession(Path.Combine(Root, "LoLReviewData"), Path.Combine(Root, "Revu"));
        public void Dispose() { Session.Dispose(); Directory.Delete(Root, recursive: true); }
    }

    // Captured from the pre-extraction Program: 99 unchanged routes plus private host shutdown.
    private const string ExpectedRoutes = """
        GET /api/auth/status
        GET /api/config
        GET /api/corrections
        GET /api/corrections/export
        GET /api/dashboard
        GET /api/derived
        GET /api/diagnostics/practice/status
        GET /api/events
        GET /api/games
        GET /api/health
        GET /api/host
        GET /api/matchups
        GET /api/matchups/export
        GET /api/objective
        GET /api/objective/games
        GET /api/objective/notes
        GET /api/objectives
        GET /api/objectives/active
        GET /api/patterns
        GET /api/pregame
        GET /api/review
        GET /api/review/export
        GET /api/recording/context
        GET /api/recording/session/{sessionId:guid}
        POST /api/recording/register
        GET /api/rules
        GET /api/settings/export
        GET /api/settings/status
        GET /api/stint
        GET /api/tiltcheck
        GET /api/update/check
        GET /api/vod
        POST /api/auth/clear-partial
        POST /api/auth/login
        POST /api/auth/logout
        POST /api/auth/resolve
        POST /api/auth/signup
        POST /api/auth/verify
        POST /api/backfill/start
        POST /api/block/end
        POST /api/block/start
        POST /api/bookmark/add
        POST /api/bookmark/delete
        POST /api/bookmark/note
        POST /api/bookmark/objective
        POST /api/bookmark/quality
        POST /api/bookmark/tag
        POST /api/clip/auto-objectives
        POST /api/clip/delete
        POST /api/clip/extract
        POST /api/clip/upload
        POST /api/config/save
        POST /api/correction/revert
        POST /api/death/classify
        POST /api/death/clear
        POST /api/diagnostics/practice/start
        POST /api/diagnostics/practice/stop
        POST /api/encounter/save
        POST /api/event/correct
        POST /api/events/reprocess/{gameId:long}
        POST /api/evidence/objective
        POST /api/evidence/polarity
        POST /api/evidence/prompt
        POST /api/evidence/status
        POST /api/focus-adherence
        POST /api/game/delete
        POST /api/game/manual
        POST /api/hardstop/override
        POST /api/host/shutdown
        POST /api/matchup/create
        POST /api/matchup/delete
        POST /api/matchup/from-last-game
        POST /api/matchup/notes
        POST /api/matchup/update
        POST /api/objective/complete
        POST /api/objective/create
        POST /api/objective/delete
        POST /api/objective/priority
        POST /api/objective/update
        POST /api/pattern/mark-reviewed
        POST /api/pattern/moment/note
        POST /api/pregame/ifthen
        POST /api/pregame/intent
        POST /api/pregame/mood
        POST /api/pregame/practiced
        POST /api/pregame/prompt/draft
        POST /api/prompt/answer/save
        POST /api/reset
        POST /api/review/delete
        POST /api/review/draft/save
        POST /api/review/save
        POST /api/review/skip
        POST /api/rule/create
        POST /api/rule/delete
        POST /api/rule/enforce
        POST /api/rule/toggle
        POST /api/rule/update
        POST /api/settings/reset
        POST /api/settings/scan-vods
        POST /api/settings/restore
        POST /api/stint/end
        POST /api/stint/start
        POST /api/update/download
        """;
}
