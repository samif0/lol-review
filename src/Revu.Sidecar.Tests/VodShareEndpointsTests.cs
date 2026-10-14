using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The 4.2 /api/vod-share routes over real HTTP: the isolated host denies all of them, and the
/// normal pipeline binds bodies and returns the contract shapes. Nothing here opens revu.db or
/// starts the worker.
/// </summary>
public sealed class VodShareEndpointsTests
{
    private const string Token = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Revu.VodShareHttp." + Guid.NewGuid().ToString("N"));
        public SidecarHostSession Session { get; }
        public Scratch() => Session = new SidecarHostSession(Path.Combine(Root, "LoLReviewData"), Path.Combine(Root, "Revu"));
        public void Dispose()
        {
            Session.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static async Task<(WebApplication App, HttpClient Client, Scratch Scratch)> StartAsync(bool isolated)
    {
        var scratch = new Scratch();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        builder.Services.AddSidecarServices(isolatedHostTest: true);
        var app = builder.Build();
        app.UseSidecarApi(Token, isolatedHostTest: isolated);
        app.MapSidecarEndpoints(SidecarJson.CreateOptions(), scratch.Session);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return (app, client, scratch);
    }

    private static async Task StopAsync(WebApplication app, HttpClient client, Scratch scratch)
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        scratch.Dispose();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, string path, object body)
    {
        using var res = await client.PostAsJsonAsync(path, body);
        return (res.StatusCode, JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    [Theory]
    [InlineData("GET", "/api/vod-share/status?gameId=1")]
    [InlineData("POST", "/api/vod-share/start")]
    [InlineData("POST", "/api/vod-share/cancel")]
    [InlineData("POST", "/api/vod-share/stop")]
    public async Task TheIsolatedHost_DeniesEveryVodShareRoute(string method, string path)
    {
        Assert.False(IsolatedHostPolicy.Allows(method, path.Split('?')[0]));
        var (app, client, scratch) = await StartAsync(isolated: true);
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST") request.Content = JsonContent.Create(new { gameId = 1 });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally { await StopAsync(app, client, scratch); }
    }

    [Theory]
    [InlineData("/api/vod-share/start")]
    [InlineData("/api/vod-share/cancel")]
    [InlineData("/api/vod-share/stop")]
    public async Task AMissingGameId_Is400(string path)
    {
        var (app, client, scratch) = await StartAsync(isolated: false);
        try
        {
            var (status, body) = await PostAsync(client, path, new { gameId = 0 });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Equal("gameId required", body.GetProperty("error").GetString());

            using var malformed = new StringContent("{ nope", Encoding.UTF8, "application/json");
            using var res = await client.PostAsync(path, malformed);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.False(File.Exists(Path.Combine(scratch.Root, "LoLReviewData", "revu.db")));
        }
        finally { await StopAsync(app, client, scratch); }
    }

    [Fact]
    public async Task Status_RequiresAGameId()
    {
        var (app, client, scratch) = await StartAsync(isolated: false);
        try
        {
            using var zero = await client.GetAsync("/api/vod-share/status?gameId=0");
            Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
            using var missing = await client.GetAsync("/api/vod-share/status");
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }
        finally { await StopAsync(app, client, scratch); }
    }

    [Fact]
    public async Task StartBeforeTheWorkerRuns_IsARefusalNotAnError()
    {
        var (app, client, scratch) = await StartAsync(isolated: false);
        try
        {
            var (status, body) = await PostAsync(client, "/api/vod-share/start",
                new { gameId = 7, quality = "720p", includeNotes = true });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(body.GetProperty("ok").GetBoolean());
            Assert.Equal("starting", body.GetProperty("errorCode").GetString());
            Assert.False(body.GetProperty("needsLogin").GetBoolean());

            var (bad, badBody) = await PostAsync(client, "/api/vod-share/start", new { gameId = 7, quality = "4k" });
            Assert.Equal(HttpStatusCode.BadRequest, bad);
            Assert.False(badBody.GetProperty("ok").GetBoolean());
        }
        finally { await StopAsync(app, client, scratch); }
    }

    [Fact]
    public async Task CancelAndStop_WithNothingShared_ReturnTheirShapes()
    {
        var (app, client, scratch) = await StartAsync(isolated: false);
        try
        {
            var (cancelStatus, cancel) = await PostAsync(client, "/api/vod-share/cancel", new { gameId = 7 });
            Assert.Equal(HttpStatusCode.OK, cancelStatus);
            Assert.Equal("{\"ok\":true,\"cancelled\":false}", cancel.GetRawText());

            var (stopStatus, stop) = await PostAsync(client, "/api/vod-share/stop", new { gameId = 7 });
            Assert.Equal(HttpStatusCode.OK, stopStatus);
            Assert.Equal("{\"ok\":true,\"remoteDeleted\":false,\"queued\":false}", stop.GetRawText());
        }
        finally { await StopAsync(app, client, scratch); }
    }
}
