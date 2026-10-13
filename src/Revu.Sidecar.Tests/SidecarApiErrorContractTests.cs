using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Every failure the desktop transport can receive carries the {ok:false,error} JSON
/// contract with its real status, including framework replies that used to be empty.
/// </summary>
public sealed class SidecarApiErrorContractTests
{
    private const string Token = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task UnknownRoutesAndEmptyFrameworkRepliesCarryJsonErrors()
    {
        await using var host = await Host.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Get, "/api/not-a-route");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.Equal("The app's request to /api/not-a-route failed (404 Not Found).", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task BindingFailuresKeepTheir400AndAJsonReasonWithOrWithoutThrowOnBadRequest()
    {
        foreach (var throwOnBadRequest in new[] { false, true })
        {
            await using var host = await Host.StartAsync(throwOnBadRequest);
            var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/bookmark/add", """{"gameId":"not-a-number"}""");
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Contains("/api/bookmark/add", body.GetProperty("error").GetString());

            var (malformed, malformedBody) = await host.SendAsync(HttpMethod.Post, "/api/bookmark/add", "{");
            Assert.Equal(HttpStatusCode.BadRequest, malformed);
            Assert.False(malformedBody.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task EndpointChosenErrorBodiesAreNotReplaced()
    {
        await using var host = await Host.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/bookmark/add", """{"gameId":0}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("gameId required", body.GetProperty("error").GetString());

        using var anonymous = new HttpClient { BaseAddress = host.Client.BaseAddress };
        using var unauthorized = await anonymous.GetAsync("/api/host");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var json = JsonDocument.Parse(await unauthorized.Content.ReadAsStringAsync());
        Assert.Equal("Unauthorized", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task HostHealthIsAuthenticatedAndExplainsADegradedDatabase()
    {
        var missing = Path.Combine(Path.GetTempPath(), "Revu.ApiErrors." + Guid.NewGuid().ToString("N"), "LoLReviewData", "revu.db");
        await using (var degraded = await Host.StartAsync(databasePath: missing))
        {
            using var anonymous = new HttpClient { BaseAddress = degraded.Client.BaseAddress };
            using var refused = await anonymous.GetAsync("/api/host/health");
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

            var (status, body) = await degraded.SendAsync(HttpMethod.Get, "/api/host/health");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("degraded", body.GetProperty("status").GetString());
            Assert.StartsWith("Revu couldn't open its database: ", body.GetProperty("error").GetString());
            Assert.Contains("missing", body.GetProperty("error").GetString());

            // The anonymous probe keeps its status-only 503 contract.
            using var probe = await anonymous.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, probe.StatusCode);
            using var probeBody = JsonDocument.Parse(await probe.Content.ReadAsStringAsync());
            Assert.False(probeBody.RootElement.TryGetProperty("error", out _));
        }

        var root = Path.Combine(Path.GetTempPath(), "Revu.ApiErrors." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "revu.db");
        using (var create = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            create.Open();
            using var command = create.CreateCommand();
            command.CommandText = "CREATE TABLE probe(x INTEGER);";
            command.ExecuteNonQuery();
        }
        try
        {
            await using var ready = await Host.StartAsync(databasePath: database);
            var (status, body) = await ready.SendAsync(HttpMethod.Get, "/api/host/health");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("ready", body.GetProperty("status").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _root;
        private readonly SidecarHostSession _session;
        public HttpClient Client { get; }

        private Host(WebApplication app, string root, SidecarHostSession session, HttpClient client)
        {
            _app = app; _root = root; _session = session; Client = client;
        }

        public static async Task<Host> StartAsync(bool throwOnBadRequest = false, string? databasePath = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "Revu.ApiErrors." + Guid.NewGuid().ToString("N"));
            var session = new SidecarHostSession(Path.Combine(root, "LoLReviewData"), Path.Combine(root, "Revu"));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.Logging.ClearProviders();
            builder.Services.AddSidecarServices(isolatedHostTest: true);
            if (throwOnBadRequest) builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
            if (databasePath is not null)
                builder.Services.AddSingleton<IDbConnectionFactory>(new ReadOnlySqliteConnectionFactory(
                    NullLogger<ReadOnlySqliteConnectionFactory>.Instance, databasePath));
            var app = builder.Build();
            app.UseSidecarApi(Token, isolatedHostTest: false);
            app.MapSidecarEndpoints(SidecarJson.CreateOptions(), session);
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return new Host(app, root, session, client);
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string? json = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _session.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
