using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        var routes = RouteEndpoints(app);
        var actual = MethodAndPaths(routes).Order().ToArray();
        var expected = ExpectedRoutes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order().ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct().Count());
        Assert.All(routes, endpoint => Assert.NotNull(endpoint.RequestDelegate));
        Assert.False(File.Exists(Path.Combine(scratch.Root, "LoLReviewData", "revu.db")));
    }

    [Fact]
    public async Task PracticeDiagnosticRoutes_ExistOnlyWhenOptedIn()
    {
        string[] practice =
        [
            "GET /api/diagnostics/practice/status",
            "POST /api/diagnostics/practice/start",
            "POST /api/diagnostics/practice/stop",
        ];
        using var optedInScratch = new ScratchHost();
        await using var optedIn = BuildApp(optedInScratch, practiceDiagnostics: true);
        var actual = MethodAndPaths(RouteEndpoints(optedIn)).ToHashSet();
        Assert.All(practice, route => Assert.Contains(route, actual));

        using var shippedScratch = new ScratchHost();
        await using var shipped = BuildApp(shippedScratch);
        Assert.DoesNotContain(MethodAndPaths(RouteEndpoints(shipped)), route => route.Contains("/api/diagnostics/"));
    }

    /// <summary>
    /// Every route the renderer command table (desktop/ui/platform/commands.mjs) names
    /// must be mapped here with the same method, so a rename on either side fails
    /// this test instead of failing at runtime in the app.
    /// </summary>
    [Fact]
    public async Task EveryRendererCommandRouteIsMappedWithItsMethod()
    {
        var source = File.ReadAllText(FindRepoFile("desktop", "ui", "platform", "commands.mjs"));
        var definitions = source[source.IndexOf("const definitions = {", StringComparison.Ordinal)..];
        definitions = definitions[..definitions.IndexOf("\n};", StringComparison.Ordinal)];
        var commands = CommandRoute.Matches(definitions)
            .Select(match => (Name: match.Groups["name"].Value,
                Method: match.Groups["helper"].Value switch { "get" => "GET", "post" => "POST", _ => "GET" /* SSE */ },
                Route: match.Groups["route"].Value))
            .ToArray();
        // Every /api/ route literal in the table must have been parsed, so a new
        // helper shape cannot silently skip the check.
        Assert.Equal(Regex.Matches(definitions, "\"/api/").Count, commands.Length);
        Assert.Contains(commands, command => command.Name == "get_review");

        using var scratch = new ScratchHost();
        await using var app = BuildApp(scratch);
        var mapped = MethodAndPaths(RouteEndpoints(app)).ToHashSet();
        Assert.All(commands, command =>
            Assert.True(mapped.Contains(command.Method + " " + command.Route),
                $"{command.Name} -> {command.Method} {command.Route} has no sidecar route"));
    }

    /// <summary>
    /// Every route the isolated-host allowlist names is mapped with its method, so a
    /// retired or renamed route cannot linger in the policy. The one exception is the
    /// practice status route, which exists only when diagnostics are opted in.
    /// </summary>
    [Fact]
    public async Task EveryIsolatedHostPolicyRouteIsMapped()
    {
        using var scratch = new ScratchHost();
        await using var app = BuildApp(scratch);
        var mapped = MethodAndPaths(RouteEndpoints(app)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmapped = IsolatedHostPolicy.ReadRoutes.Select(route => "GET " + route)
            .Concat(IsolatedHostPolicy.DatabaseWriteRoutes.Select(route => "POST " + route))
            .Where(route => !mapped.Contains(route))
            .ToArray();
        Assert.Equal(new[] { "GET /api/diagnostics/practice/status" }, unmapped);
    }

    private static readonly Regex CommandRoute = new(
        "^\\s*(?<name>[a-z_]+):\\s*(?:(?<helper>get|post)\\(\"(?<route>/api/[^\"]+)\"|\\{\"method\":\"(?<helper>SSE)\",\"route\":\"(?<route>/api/[^\"]+)\")",
        RegexOptions.Multiline);

    private static RouteEndpoint[] RouteEndpoints(WebApplication app) => ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

    private static IEnumerable<string> MethodAndPaths(IEnumerable<RouteEndpoint> routes) =>
        routes.SelectMany(endpoint => endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>()
            .HttpMethods.Select(method => method + " " + endpoint.RoutePattern.RawText));

    private static string FindRepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Could not locate " + string.Join('/', parts));
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
    [InlineData("POST", "/api/recording/relayout")]
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

    [Fact]
    public async Task RemakeToggle_IsAllowedOnTheIsolatedHost_AndRejectsAMissingGameIdBeforeTheDatabase()
    {
        // 3.14.1: POST /api/review/remake is a database-only write (allowed when isolated);
        // a body without a game id is a 400 and never opens revu.db.
        using var scratch = new ScratchHost();
        await using var app = BuildApp(scratch);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var content = new StringContent("""{"gameId":0,"isRemake":true}""", System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/review/remake", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(File.Exists(Path.Combine(scratch.Root, "LoLReviewData", "revu.db")));
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task RemoveFromHistory_IsAllowedOnTheIsolatedHost_AndRejectsAMissingGameIdBeforeTheDatabase()
    {
        using var scratch = new ScratchHost();
        await using var app = BuildApp(scratch);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var content = new StringContent("""{"gameId":0,"hidden":true}""", System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/game/hide", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(File.Exists(Path.Combine(scratch.Root, "LoLReviewData", "revu.db")));
        }
        finally { await app.StopAsync(); }
    }

    private const string Token = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
    private static WebApplication BuildApp(ScratchHost scratch, bool practiceDiagnostics = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        // Pin the opt-in either way so a developer's own environment cannot change the table.
        builder.Configuration[SidecarEndpoints.PracticeDiagnosticsSetting] = practiceDiagnostics ? "1" : "0";
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

    // Captured from the pre-extraction Program, minus the routes retired with their
    // renderer commands. Practice diagnostics are opt-in and absent here.
    private const string ExpectedRoutes = """
        GET /api/auth/status
        GET /api/config
        GET /api/corrections/export
        GET /api/dashboard
        GET /api/derived
        GET /api/events
        GET /api/games
        GET /api/health
        GET /api/host
        GET /api/host/health
        GET /api/matchups
        GET /api/matchups/export
        GET /api/missed-games
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
        POST /api/recording/relayout
        GET /api/rules
        POST /api/settings/export
        GET /api/settings/status
        GET /api/stint
        GET /api/tiltcheck
        GET /api/update/check
        GET /api/vod
        GET /api/vod-share/status
        POST /api/vod-share/start
        POST /api/vod-share/cancel
        POST /api/vod-share/stop
        GET /api/clip/share-status
        POST /api/auth/clear-partial
        POST /api/auth/login
        POST /api/auth/logout
        POST /api/auth/resolve
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
        POST /api/clip/narration/delete
        POST /api/clip/narration/mix
        POST /api/clip/narration/save
        POST /api/clip/narration/transcribe
        POST /api/clip/upload
        POST /api/config/save
        POST /api/correction/revert
        POST /api/death/classify
        POST /api/death/clear
        POST /api/event/correct
        POST /api/evidence/objective
        POST /api/evidence/polarity
        POST /api/evidence/prompt
        POST /api/evidence/status
        POST /api/focus-adherence
        POST /api/game/delete
        POST /api/game/hide
        POST /api/game/manual
        POST /api/hardstop/override
        POST /api/host/shutdown
        POST /api/matchup/create
        POST /api/matchup/delete
        POST /api/matchup/from-last-game
        POST /api/matchup/from-game
        POST /api/matchup/notes
        POST /api/matchup/update
        POST /api/missed-games/resolve
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
        POST /api/review/remake
        POST /api/review/save
        POST /api/review/skip
        POST /api/rule/create
        POST /api/rule/delete
        POST /api/rule/enforce
        POST /api/rule/toggle
        POST /api/rule/update
        POST /api/settings/reset
        POST /api/settings/scan-vods
        POST /api/settings/recover-games
        POST /api/settings/restore
        POST /api/stint/end
        POST /api/stint/start
        POST /api/update/download
        """;
}
