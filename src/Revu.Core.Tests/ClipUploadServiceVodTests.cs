using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The VOD side of the proxy client against a fake proxy that speaks contract section 5:
/// status parsing (including an old proxy), the always-multipart upload under /vod-shares,
/// the markers PUT, and the error code mapping of section 7.2.
/// </summary>
public sealed class ClipUploadServiceVodTests : IDisposable
{
    private const long MiB = 1024 * 1024;
    private const long PartSize = 16 * MiB;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "vodup-" + Guid.NewGuid().ToString("N"));

    public ClipUploadServiceVodTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed record Seen(string Method, string Path, string Query, long? ContentLength, long BodyLength, string? Text,
        string? MediaType, string? Auth);

    private sealed class FakeProxy : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = new();
        public required Func<Seen, HttpResponseMessage> Respond { get; init; }
        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            var body = req.Content is null ? Array.Empty<byte>() : await req.Content.ReadAsByteArrayAsync(ct);
            var media = req.Content?.Headers.ContentType?.MediaType;
            var seen = new Seen(req.Method.Method, req.RequestUri!.AbsolutePath, req.RequestUri.Query,
                req.Content?.Headers.ContentLength, body.Length, media == "application/json" ? Encoding.UTF8.GetString(body) : null,
                media, req.Headers.Authorization?.ToString());
            lock (Requests) Requests.Add(seen);
            return Respond(seen);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static Func<Seen, HttpResponseMessage> HappyVodProxy(Func<Seen, HttpResponseMessage?>? overrideWith = null) =>
        seen =>
        {
            var custom = overrideWith?.Invoke(seen);
            if (custom is not null) return custom;
            if (seen.Method == "POST" && seen.Path == "/vod-shares/uploads")
            {
                var size = long.Parse(System.Web.HttpUtility.ParseQueryString(seen.Query)["size"]!);
                return Json(HttpStatusCode.Created, new
                {
                    id = "vod1234", part_size = PartSize, part_count = (int)((size + PartSize - 1) / PartSize),
                    expires_at = 1_760_421_600, share_expires_at = 1_760_659_200, url = "https://revu.lol/v/vod1234",
                });
            }
            if (seen.Method == "PUT" && seen.Path.StartsWith("/vod-shares/vod1234/parts/"))
                return Json(HttpStatusCode.OK, new { part_number = int.Parse(seen.Path.Split('/')[^1]), etag = "e" + seen.Path.Split('/')[^1] });
            if (seen.Method == "POST" && seen.Path == "/vod-shares/vod1234/complete")
                return Json(HttpStatusCode.Created, new { id = "vod1234", url = "https://revu.lol/v/vod1234", expires_at = 1_760_659_200 });
            if (seen.Method == "PUT" && seen.Path == "/vod-shares/vod1234/markers")
                return Json(HttpStatusCode.OK, new { ok = true, markers = 2, notes = 0 });
            if (seen.Method == "DELETE") return Json(HttpStatusCode.OK, new { ok = true });
            return Json(HttpStatusCode.NotFound, new { error = "not_found" });
        };

    private static (ClipUploadService Service, FakeProxy Proxy) Create(Func<Seen, HttpResponseMessage> respond)
    {
        var proxy = new FakeProxy { Respond = respond };
        var service = new ClipUploadService(new HttpClient(proxy) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<ClipUploadService>.Instance, "https://proxy.test")
        {
            PartRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            CompleteRetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
            VodPartOutageDelays = [TimeSpan.Zero],
        };
        return (service, proxy);
    }

    private string MakeFile(long bytes)
    {
        var path = Path.Combine(_dir, $"game-{bytes}.mp4");
        using var fs = new FileStream(path, FileMode.Create);
        fs.SetLength(bytes);
        return path;
    }

    private sealed class Collect : IProgress<ClipUploadProgress>
    {
        public List<ClipUploadProgress> Items { get; } = new();
        public void Report(ClipUploadProgress value) { lock (Items) Items.Add(value); }
    }

    // ── Status ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_Allowed_ParsesLimitsUsagePoolAndShares()
    {
        var (service, proxy) = Create(_ => Json(HttpStatusCode.OK, new
        {
            allowed = true, sharing = true, serving = true, now = 1_760_400_000,
            limits = new
            {
                max_bytes = 2147483648L, max_seconds = 7200, ttl_seconds = 259200, max_live = 3, max_pending = 1,
                max_starts_per_day = 6, part_size = 16777216, markers_max_bytes = 49152, notes_max_bytes = 14336, daily_play_cap = 200,
            },
            usage = new { live = 1, pending = 0, starts_today = 2, day_resets_at = 1_760_486_400 },
            pool = new
            {
                total_budget_bytes = 9_000_000_000, vod_budget_bytes = 7_000_000_000, used_bytes = 1_535_583_333,
                vod_used_bytes = 1_315_583_333, free_bytes = 5_684_416_667,
                releases = new[] { new { at = 1_760_403_600, bytes = 220_000_000, vod_bytes = 0 } },
            },
            shares = new[] { new { id = "abc1234", status = "ready", quality = "1080p", size_bytes = 1_315_583_333, created_at = 1_760_390_000, expires_at = 1_760_649_200 } },
        }));

        var status = await service.GetVodShareStatusAsync("tok");

        var req = Assert.Single(proxy.Requests);
        Assert.Equal(("GET", "/vod-shares/status", "Bearer tok"), (req.Method, req.Path, req.Auth));
        Assert.True(status.Supported && status.Allowed && status.Sharing && status.Serving);
        Assert.Equal(1_760_400_000, status.Now);
        Assert.Equal(new VodShareServerLimits(2147483648L, 7200, 259200, 3, 1, 6, 16777216, 49152, 14336, 200), status.Limits);
        Assert.Equal(new VodShareServerUsage(1, 0, 2, 1_760_486_400), status.Usage);
        Assert.Equal(5_684_416_667, status.Pool!.FreeBytes);
        Assert.Equal(new VodSharePoolRelease(1_760_403_600, 220_000_000, 0), Assert.Single(status.Pool.Releases));
        Assert.Equal(new VodShareServerShare("abc1234", "ready", "1080p", 1_315_583_333, 1_760_390_000, 1_760_649_200),
            Assert.Single(status.Shares));
    }

    [Fact]
    public async Task Status_NotAllowed_HasNoLimitsUsageOrPool()
    {
        var (service, _) = Create(_ => Json(HttpStatusCode.OK, new
        {
            allowed = false, sharing = true, serving = true, now = 1_760_400_000, shares = Array.Empty<object>(),
        }));
        var status = await service.GetVodShareStatusAsync("tok");
        Assert.True(status.Supported);
        Assert.False(status.Allowed);
        Assert.True(status.Sharing);
        Assert.Null(status.Limits);
        Assert.Null(status.Usage);
        Assert.Null(status.Pool);
        Assert.Empty(status.Shares);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task Status_FromAnOldProxy_IsUnsupported(HttpStatusCode code)
    {
        var (service, _) = Create(_ => Json(code, new { error = "not_found" }));
        var status = await service.GetVodShareStatusAsync("tok");
        Assert.False(status.Supported);
        Assert.False(status.Allowed);
        Assert.False(status.Sharing);
    }

    [Fact]
    public async Task Status_401_IsUnauthorized_5xxAndNetworkAreNetwork()
    {
        var (unauth, _) = Create(_ => Json(HttpStatusCode.Unauthorized, new { error = "unauthorized" }));
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => unauth.GetVodShareStatusAsync("tok"));
        Assert.Equal(("unauthorized", true), (ex.Code, ex.Unauthorized));

        var (down, _) = Create(_ => Json(HttpStatusCode.ServiceUnavailable, new { error = "auth_unavailable" }));
        Assert.Equal("network", (await Assert.ThrowsAsync<ClipUploadException>(() => down.GetVodShareStatusAsync("tok"))).Code);

        var (offline, proxy) = Create(_ => throw new InvalidOperationException());
        proxy.Throw = new HttpRequestException("offline");
        Assert.Equal("network", (await Assert.ThrowsAsync<ClipUploadException>(() => offline.GetVodShareStatusAsync("tok"))).Code);

        Assert.Equal("unauthorized", (await Assert.ThrowsAsync<ClipUploadException>(() => offline.GetVodShareStatusAsync(""))).Code);
    }

    // ── Upload ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_IsAlwaysMultipart_UnderVodShares_WithExactParts()
    {
        var (service, proxy) = Create(HappyVodProxy());
        var size = 40 * MiB;   // under the clip legacy threshold: VODs still go multipart
        var file = MakeFile(size);
        var progress = new Collect();
        var assigned = new List<string>();

        var result = await service.UploadVodAsync(file, "tok", new VodUploadRequest("720p", 2256), progress, assigned.Add);

        Assert.Equal(new VodUploadResult("vod1234", "https://revu.lol/v/vod1234", 1_760_659_200), result);
        Assert.Equal(new[] { "vod1234" }, assigned);
        var init = proxy.Requests[0];
        Assert.Equal(("POST", "/vod-shares/uploads"), (init.Method, init.Path));
        Assert.Equal($"?size={size}&duration=2256&quality=720p", init.Query);
        Assert.Equal(("video/mp4", 0L, 0L), (init.MediaType, init.ContentLength, init.BodyLength));
        var parts = proxy.Requests.Where(r => r.Method == "PUT").ToList();
        Assert.Equal(new[] { "/vod-shares/vod1234/parts/1", "/vod-shares/vod1234/parts/2", "/vod-shares/vod1234/parts/3" },
            parts.Select(p => p.Path));
        Assert.Equal(new long[] { PartSize, PartSize, 8 * MiB }, parts.Select(p => p.BodyLength));
        var complete = proxy.Requests[^1];
        Assert.Equal(("POST", "/vod-shares/vod1234/complete"), (complete.Method, complete.Path));
        using var doc = JsonDocument.Parse(complete.Text!);
        Assert.Equal(3, doc.RootElement.GetProperty("parts").GetArrayLength());
        Assert.Equal("e2", doc.RootElement.GetProperty("parts")[1].GetProperty("etag").GetString());
        Assert.Equal(("finishing", size), (progress.Items[^1].Phase, progress.Items[^1].SentBytes));
        Assert.DoesNotContain(proxy.Requests, r => r.Path.StartsWith("/clips/") && r.Method != "DELETE");
    }

    [Fact]
    public async Task Upload_ClampsDuration_AndRefusesABadQuality()
    {
        var (service, proxy) = Create(HappyVodProxy());
        var file = MakeFile(MiB);
        await service.UploadVodAsync(file, "tok", new VodUploadRequest("1080p", 9000));
        Assert.Contains("duration=7200", proxy.Requests[0].Query);

        var ex = await Assert.ThrowsAsync<ClipUploadException>(() =>
            service.UploadVodAsync(file, "tok", new VodUploadRequest("4k", 60)));
        Assert.Equal("bad_request", ex.Code);
    }

    public static TheoryData<int, object, string, long?, bool> InitRefusals => new()
    {
        { 401, new { error = "unauthorized" }, "unauthorized", null, true },
        { 403, new { error = "not_allowed" }, "not_allowed", null, false },
        { 503, new { error = "vod_sharing_disabled", message = "Full-game sharing is paused right now." }, "vod_sharing_disabled", null, false },
        { 409, new { error = "pool_full", retry_at = 1_760_403_600, free_bytes = 100 }, "pool_full", 1_760_403_600, false },
        { 409, new { error = "pool_full", retry_at = (long?)null }, "pool_full", null, false },
        { 409, new { error = "live_limit" }, "live_limit", null, false },
        { 429, new { error = "daily_limit", retry_at = 1_760_486_400 }, "daily_limit", 1_760_486_400, false },
        { 429, new { error = "too_many_pending_uploads" }, "too_many_pending_uploads", null, false },
        { 429, new { error = "rate_limited" }, "rate_limited", null, false },
        { 413, new { error = "payload_too_large" }, "payload_too_large", null, false },
        { 400, new { error = "bad_request" }, "bad_request", null, false },
        { 418, new { }, "http_418", null, false },
        { 502, new { error = "clip_error" }, "network", null, false },
        { 500, new { }, "network", null, false },
    };

    [Theory]
    [MemberData(nameof(InitRefusals))]
    public async Task InitRefusals_MapToTheirCodes(int status, object body, string code, long? retryAt, bool unauthorized)
    {
        var (service, proxy) = Create(HappyVodProxy(seen =>
            seen.Path == "/vod-shares/uploads" ? Json((HttpStatusCode)status, body) : null));
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() =>
            service.UploadVodAsync(MakeFile(MiB), "tok", new VodUploadRequest("1080p", 600)));
        Assert.Equal((code, retryAt, unauthorized), (ex.Code, ex.RetryAt, ex.Unauthorized));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain('—', ex.Message);
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task InitNetworkFailure_IsNetwork()
    {
        var (service, proxy) = Create(HappyVodProxy());
        proxy.Throw = new HttpRequestException("offline");
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() =>
            service.UploadVodAsync(MakeFile(MiB), "tok", new VodUploadRequest("1080p", 600)));
        Assert.Equal("network", ex.Code);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task APartThatKeepsFailing_IsUploadFailed_AndTheRemoteIsDeleted()
    {
        var (service, proxy) = Create(HappyVodProxy(seen =>
            seen.Method == "PUT" && seen.Path.EndsWith("/parts/2")
                ? Json(HttpStatusCode.TooManyRequests, new { error = "too_many_part_uploads" })
                : null));
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() =>
            service.UploadVodAsync(MakeFile(20 * MiB), "tok", new VodUploadRequest("1080p", 600)));
        Assert.Equal("upload_failed", ex.Code);
        Assert.Equal(4, proxy.Requests.Count(r => r.Path.EndsWith("/parts/2")));
        Assert.Contains(proxy.Requests, r => r.Method == "DELETE" && r.Path == "/clips/vod1234");
    }

    [Fact]
    public async Task APartRidesOutAnOutage_ConnectionFailuresDontUseUpItsAttempts()
    {
        var failures = 0;
        var (service, proxy) = Create(HappyVodProxy(seen =>
        {
            if (seen.Method == "PUT" && seen.Path.EndsWith("/parts/2") && failures < 9)
            {
                failures++;
                throw new HttpRequestException(failures % 2 == 0 ? HttpRequestError.NameResolutionError : HttpRequestError.ConnectionError,
                    "No route to host");
            }
            return null;
        }));
        var done = await service.UploadVodAsync(MakeFile(20 * MiB), "tok", new VodUploadRequest("1080p", 600));
        Assert.Equal("vod1234", done.Id);
        Assert.Equal(10, proxy.Requests.Count(r => r.Path.EndsWith("/parts/2")));
        Assert.DoesNotContain(proxy.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task AnOutageLongerThanTheWindow_IsUploadFailed_AndTheRemoteIsDeleted()
    {
        var (service, proxy) = Create(HappyVodProxy(seen =>
            seen.Method == "PUT" ? throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known") : null));
        service.VodPartOutageWindow = TimeSpan.FromMilliseconds(150);
        service.VodPartOutageDelays = [TimeSpan.FromMilliseconds(10)];
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() =>
            service.UploadVodAsync(MakeFile(MiB), "tok", new VodUploadRequest("1080p", 600)));
        Assert.Equal("upload_failed", ex.Code);
        Assert.True(proxy.Requests.Count(r => r.Method == "PUT") > 4);
        Assert.Contains(proxy.Requests, r => r.Method == "DELETE" && r.Path == "/clips/vod1234");
    }

    [Fact]
    public async Task FailuresThatReachTheProxy_StillStopAtFourAttempts_EvenBetweenOutages()
    {
        var calls = 0;
        var (service, proxy) = Create(HappyVodProxy(seen =>
        {
            if (seen.Method != "PUT") return null;
            // Alternate: a dropped connection mid-request (may have reached the proxy), then no route.
            if (++calls % 2 == 1) throw new IOException("connection reset");
            throw new HttpRequestException(HttpRequestError.ConnectionError, "No route to host");
        }));
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() =>
            service.UploadVodAsync(MakeFile(MiB), "tok", new VodUploadRequest("1080p", 600)));
        Assert.Equal("upload_failed", ex.Code);
        Assert.Equal(7, proxy.Requests.Count(r => r.Method == "PUT")); // 4 that may have counted, 3 outages between
    }

    [Fact]
    public void OnlyFailuresBeforeAnyRequest_AreConnectFailures()
    {
        Assert.True(ClipUploadService.IsConnectFailure(new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        Assert.True(ClipUploadService.IsConnectFailure(new HttpRequestException(HttpRequestError.NameResolutionError, "dns")));
        Assert.False(ClipUploadService.IsConnectFailure(new HttpRequestException("reset")));
        Assert.False(ClipUploadService.IsConnectFailure(new IOException("reset")));
        Assert.False(ClipUploadService.IsConnectFailure(new TaskCanceledException("timeout")));
    }

    [Fact]
    public async Task ACompleteRejectedAsUnauthorized_KeepsItsSignInMeaning()
    {
        var (service, proxy) = Create(HappyVodProxy(seen =>
            seen.Path.EndsWith("/complete") ? Json(HttpStatusCode.Unauthorized, new { error = "unauthorized" }) : null));
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() =>
            service.UploadVodAsync(MakeFile(MiB), "tok", new VodUploadRequest("1080p", 600)));
        Assert.Equal(("unauthorized", true), (ex.Code, ex.Unauthorized));
        Assert.Contains(proxy.Requests, r => r.Method == "DELETE" && r.Path == "/clips/vod1234");
    }

    [Fact]
    public async Task Cancel_DeletesTheRemoteUpload()
    {
        using var cts = new CancellationTokenSource();
        var (service, proxy) = Create(HappyVodProxy(seen =>
        {
            if (seen.Method == "PUT" && seen.Path.EndsWith("/parts/1")) cts.Cancel();
            return null;
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UploadVodAsync(MakeFile(20 * MiB), "tok", new VodUploadRequest("1080p", 600), ct: cts.Token));
        Assert.Contains(proxy.Requests, r => r.Method == "DELETE" && r.Path == "/clips/vod1234");
    }

    [Fact]
    public async Task TooLargeOrEmptyFiles_AreRefusedBeforeTheProxy()
    {
        var (service, proxy) = Create(HappyVodProxy());
        var big = SparseFile.TryCreate(Path.Combine(_dir, "big.mp4"), 2147483649L);
        if (big is not null)
        {
            Assert.Equal("payload_too_large", (await Assert.ThrowsAsync<ClipUploadException>(() =>
                service.UploadVodAsync(big, "tok", new VodUploadRequest("720p", 600)))).Code);
        }
        Assert.Equal("upload_failed", (await Assert.ThrowsAsync<ClipUploadException>(() =>
            service.UploadVodAsync(MakeFile(0), "tok", new VodUploadRequest("720p", 600)))).Code);
        Assert.Equal("unauthorized", (await Assert.ThrowsAsync<ClipUploadException>(() =>
            service.UploadVodAsync(MakeFile(MiB), " ", new VodUploadRequest("720p", 600)))).Code);
        Assert.Empty(proxy.Requests);
    }

    // ── Markers ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Markers_ArePutAsJson()
    {
        var (service, proxy) = Create(HappyVodProxy());
        const string json = """{"version":1,"gameTimeAtVideoStart":0,"game":{},"markers":[],"notes":null}""";
        await service.PutVodMarkersAsync("vod1234", "tok", json);
        var put = Assert.Single(proxy.Requests);
        Assert.Equal(("PUT", "/vod-shares/vod1234/markers", "application/json", json), (put.Method, put.Path, put.MediaType, put.Text));
    }

    [Theory]
    [InlineData(404, "not_found", "not_found")]
    [InlineData(413, "markers_too_large", "markers_too_large")]
    [InlineData(400, "bad_markers", "bad_markers")]
    [InlineData(401, "unauthorized", "unauthorized")]
    [InlineData(502, "clip_error", "network")]
    public async Task MarkerRefusals_CarryTheirCode(int status, string error, string code)
    {
        var (service, _) = Create(_ => Json((HttpStatusCode)status, new { error }));
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.PutVodMarkersAsync("vod1234", "tok", "{}"));
        Assert.Equal(code, ex.Code);
    }
}
