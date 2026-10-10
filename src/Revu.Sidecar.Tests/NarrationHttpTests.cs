using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The 3.14 routes over real HTTP (Kestrel on a loopback port): JSON body binding, status
/// codes and reply shapes through the actual middleware, plus the isolated host's denial.
/// Every request here is rejected before it touches the database, so no revu.db is created
/// and no background worker is started.
/// </summary>
public sealed class NarrationHttpTests
{
    private const string Token = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private static async Task<(WebApplication App, HttpClient Client, Scratch Scratch)> StartAsync(bool isolated)
    {
        var scratch = new Scratch();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Logging.ClearProviders();
        // The isolated service graph registers no hosted services (no LCU monitor), so a
        // non-isolated middleware pipeline can be exercised without touching the game client.
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
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Theory]
    [InlineData("GET", "/api/clip/share-status?bookmarkId=1")]
    [InlineData("POST", "/api/clip/narration/save")]
    [InlineData("POST", "/api/clip/narration/mix")]
    [InlineData("POST", "/api/clip/narration/delete")]
    [InlineData("POST", "/api/clip/narration/transcribe")]
    public async Task TheIsolatedHost_DeniesEveryNarrationRoute(string method, string path)
    {
        var (app, client, scratch) = await StartAsync(isolated: true);
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST") request.Content = JsonContent.Create(new { gameId = 1, bookmarkId = 1 });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally { await StopAsync(app, client, scratch); }
    }

    [Fact]
    public async Task ShareStatus_ReturnsTheIdleShapeInCamelCase()
    {
        var (app, client, scratch) = await StartAsync(isolated: false);
        try
        {
            using var res = await client.GetAsync("/api/clip/share-status?bookmarkId=987654");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("idle", body.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("phase").ValueKind);
            Assert.Equal(0, body.GetProperty("sentBytes").GetInt64());
            Assert.Equal(0, body.GetProperty("totalBytes").GetInt64());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("url").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
            Assert.False(body.GetProperty("retryable").GetBoolean());
            Assert.False(body.GetProperty("needsLogin").GetBoolean());
        }
        finally { await StopAsync(app, client, scratch); }
    }

    [Fact]
    public async Task Save_BindsTheElectronBody_AndRejectsBadFieldsWith400()
    {
        var (app, client, scratch) = await StartAsync(isolated: false);
        try
        {
            var id = Guid.NewGuid().ToString("D");
            // Exactly the keys Electron main sends (C7 meta plus narrationId).
            object Body(string narrationId, int offsetMs) => new
            {
                gameId = 7, bookmarkId = 9, mimeType = "audio/webm;codecs=opus", offsetMs, durationMs = 30_000,
                gameVolume = 0.8, narrationVolume = 1.0, duck = true, narrationId,
            };

            var badId = await PostAsync(client, "/api/clip/narration/save", Body(id.ToUpperInvariant(), 0));
            Assert.Equal(HttpStatusCode.BadRequest, badId.Status);
            Assert.False(badId.Body.GetProperty("ok").GetBoolean());
            Assert.Equal("Invalid narration id.", badId.Body.GetProperty("error").GetString());

            var badOffset = await PostAsync(client, "/api/clip/narration/save", Body(id, 10_001));
            Assert.Equal(HttpStatusCode.BadRequest, badOffset.Status);
            Assert.Equal("Sync offset is out of range.", badOffset.Body.GetProperty("error").GetString());

            // A valid body reaches the voice-track check: the id was bound and the path derived.
            var missing = await PostAsync(client, "/api/clip/narration/save", Body(id, -250));
            Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
            Assert.Equal("Narration audio file not found.", missing.Body.GetProperty("error").GetString());
        }
        finally { await StopAsync(app, client, scratch); }
    }

    [Fact]
    public async Task Mix_Delete_Transcribe_ValidateTheirBodies()
    {
        var (app, client, scratch) = await StartAsync(isolated: false);
        try
        {
            var mix = await PostAsync(client, "/api/clip/narration/mix",
                new { gameId = 7, bookmarkId = 9, offsetMs = 0, gameVolume = 1.6, narrationVolume = 1.0, duck = false });
            Assert.Equal(HttpStatusCode.BadRequest, mix.Status);
            Assert.Equal("Game volume is out of range.", mix.Body.GetProperty("error").GetString());

            foreach (var path in new[] { "/api/clip/narration/delete", "/api/clip/narration/transcribe" })
            {
                var reply = await PostAsync(client, path, new { gameId = 0, bookmarkId = 9 });
                Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
                Assert.Equal("gameId and bookmarkId required", reply.Body.GetProperty("error").GetString());
            }
        }
        finally { await StopAsync(app, client, scratch); }
    }

    [Fact]
    public async Task Extract_OverTenMinutes_Is422WithTheExactText()
    {
        var (app, client, scratch) = await StartAsync(isolated: false);
        try
        {
            var reply = await PostAsync(client, "/api/clip/extract",
                new { gameId = 7, vodPath = "C:\\nowhere\\game.mp4", startTimeS = 30, endTimeS = 631 });
            Assert.Equal((HttpStatusCode)422, reply.Status);
            Assert.False(reply.Body.GetProperty("ok").GetBoolean());
            Assert.Equal("Clips can be up to 10 minutes. Trim the range and try again.", reply.Body.GetProperty("error").GetString());
        }
        finally { await StopAsync(app, client, scratch); }
    }

    [Fact]
    public async Task Upload_OfAFileOverTwoGibibytes_Is422AndNotRetryable()
    {
        using var h = new NarrationHarness();
        await h.InitAsync();
        h.SignIn();
        var (bm, clip) = await h.ClipAsync();
        if (SparseFile.TryResize(clip, 2147483648L + 1) is null) return; // volume without sparse files

        var reply = await ClipShareRequests.UploadAsync(new ShareClipBody(NarrationHarness.GameId, bm, "Ahri", null),
            h.Scope.Vod, h.Narrations, h.Scope.Games, h.Scope.Config, h.Share, NullLogger.Instance);

        Assert.Equal(422, reply.Status);
        var body = Replies.Body(reply);
        Assert.Equal("Clip is too large to share (2 GB max).", body.GetProperty("error").GetString());
        Assert.False(body.GetProperty("retryable").GetBoolean());
        Assert.False(h.Share.IsActive(bm));
    }

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Revu.NarrationHttp." + Guid.NewGuid().ToString("N"));
        public SidecarHostSession Session { get; }
        public Scratch() => Session = new SidecarHostSession(Path.Combine(Root, "LoLReviewData"), Path.Combine(Root, "Revu"));
        public void Dispose()
        {
            Session.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}

/// <summary>Grows an existing file to any length as an NTFS sparse file (no disk space used).</summary>
internal static class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer,
        uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    /// <summary>The path, now sparse and <paramref name="length"/> bytes long, or null when unsupported.</summary>
    public static string? TryResize(string path, long length)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (!DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                return null;
            fs.SetLength(length);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
