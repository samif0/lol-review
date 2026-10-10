using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Clip sharing against a fake proxy: legacy single POST at or below 95 MiB, R2 multipart
/// above it (init, exact parts, complete), retries, remote cleanup, error copy.
/// </summary>
public sealed class ClipUploadServiceMultipartTests : IDisposable
{
    private const long MiB = 1024 * 1024;
    private const long PartSize = 16 * MiB;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "upload-" + Guid.NewGuid().ToString("N"));

    public ClipUploadServiceMultipartTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    internal sealed record Seen(string Method, string Path, string Query, long? ContentLength, long BodyLength,
        string? Text, string? MediaType);

    private sealed class FakeProxy : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = new();
        public required Func<Seen, CancellationToken, HttpResponseMessage> Respond { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            byte[] body = req.Content is null ? Array.Empty<byte>() : await req.Content.ReadAsByteArrayAsync(ct);
            var media = req.Content?.Headers.ContentType?.MediaType;
            var text = media is "application/json" or "text/plain" ? Encoding.UTF8.GetString(body) : null;
            var seen = new Seen(req.Method.Method, req.RequestUri!.AbsolutePath, req.RequestUri.Query,
                req.Content?.Headers.ContentLength, body.Length, text, media);
            lock (Requests) Requests.Add(seen);
            ct.ThrowIfCancellationRequested();
            return Respond(seen, ct);
        }

        public IEnumerable<Seen> Parts => Requests.Where(r => r.Method == "PUT" && r.Path.Contains("/parts/"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    /// <summary>A proxy that accepts everything (multipart sizes from the declared size).</summary>
    private static Func<Seen, CancellationToken, HttpResponseMessage> HappyProxy(Func<Seen, HttpResponseMessage?>? overrideWith = null) =>
        (seen, _) =>
        {
            var custom = overrideWith?.Invoke(seen);
            if (custom is not null) return custom;
            if (seen.Method == "POST" && seen.Path == "/clips/uploads")
            {
                var size = long.Parse(System.Web.HttpUtility.ParseQueryString(seen.Query)["size"]!);
                return Json(HttpStatusCode.Created, new { id = "mp1", part_size = PartSize, part_count = (int)((size + PartSize - 1) / PartSize), expires_at = 1 });
            }
            if (seen.Method == "PUT" && seen.Path.StartsWith("/clips/mp1/parts/"))
                return Json(HttpStatusCode.OK, new { part_number = int.Parse(seen.Path.Split('/')[^1]), etag = "e" + seen.Path.Split('/')[^1] });
            if (seen.Method == "POST" && seen.Path == "/clips/mp1/complete")
                return Json(HttpStatusCode.Created, new { id = "mp1", url = "https://revu.lol/mp1", expires_at = 99 });
            if (seen.Method == "POST" && seen.Path == "/clips")
                return Json(HttpStatusCode.Created, new { id = "lg1", url = "https://revu.lol/lg1", expires_at = 99 });
            if (seen.Method == "DELETE") return Json(HttpStatusCode.OK, new { ok = true });
            if (seen.Method == "PUT" && seen.Path.EndsWith("/transcript")) return Json(HttpStatusCode.OK, new { ok = true, segment_count = 1 });
            return Json(HttpStatusCode.NotFound, new { error = "not_found" });
        };

    private static (ClipUploadService Service, FakeProxy Proxy) Create(Func<Seen, CancellationToken, HttpResponseMessage> respond)
    {
        var proxy = new FakeProxy { Respond = respond };
        var service = new ClipUploadService(new HttpClient(proxy) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<ClipUploadService>.Instance, "https://proxy.test")
        {
            PartRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            CompleteRetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        };
        return (service, proxy);
    }

    private string MakeFile(long bytes, string ext = ".mp4")
    {
        var path = Path.Combine(_dir, $"clip{bytes}{ext}");
        using var fs = new FileStream(path, FileMode.Create);
        fs.SetLength(bytes);
        return path;
    }

    private sealed class Collect : IProgress<ClipUploadProgress>
    {
        public List<ClipUploadProgress> Items { get; } = new();
        public void Report(ClipUploadProgress value) { lock (Items) Items.Add(value); }
    }

    [Fact]
    public async Task FiftyMegabytes_IsOneLegacyPostWithAContentLength_AndProgressReachesTheTotal()
    {
        var (service, proxy) = Create(HappyProxy());
        var file = MakeFile(50 * MiB);
        var progress = new Collect();
        string? assigned = null;

        var result = await service.UploadAsync(file, "tok", "title", "Ahri", 42, progress, narrated: true,
            onRemoteIdAssigned: id => assigned = id);

        Assert.Equal("https://revu.lol/lg1", result.Url);
        var post = Assert.Single(proxy.Requests);
        Assert.Equal("/clips", post.Path);
        Assert.Equal(50 * MiB, post.ContentLength);
        Assert.Equal(50 * MiB, post.BodyLength);
        Assert.Contains("narrated=1", post.Query);
        Assert.Contains("duration=42", post.Query);
        Assert.Null(assigned);
        Assert.Equal(50 * MiB, progress.Items[^1].SentBytes);
        Assert.Equal(50 * MiB, progress.Items[^1].TotalBytes);
        Assert.All(progress.Items, p => Assert.Equal("uploading", p.Phase));
    }

    [Fact]
    public async Task HundredMebibytes_IsInitThenSevenExactPartsThenComplete()
    {
        var (service, proxy) = Create(HappyProxy());
        var size = 100 * MiB;
        var file = MakeFile(size);
        var progress = new Collect();
        var assigned = new List<string>();

        var result = await service.UploadAsync(file, "tok", "t", "Ahri", 300, progress, narrated: false,
            onRemoteIdAssigned: assigned.Add);

        Assert.Equal("https://revu.lol/mp1", result.Url);
        Assert.Equal(new[] { "mp1" }, assigned);
        var init = proxy.Requests[0];
        Assert.Equal(("POST", "/clips/uploads"), (init.Method, init.Path));
        Assert.Equal(0, init.ContentLength);
        Assert.Equal("video/mp4", init.MediaType);
        Assert.Contains($"size={size}", init.Query);
        Assert.Contains("narrated=0", init.Query);
        Assert.Contains("duration=300", init.Query);

        var parts = proxy.Parts.ToList();
        Assert.Equal(7, parts.Count);
        Assert.Equal(Enumerable.Range(1, 7).Select(n => $"/clips/mp1/parts/{n}"), parts.Select(p => p.Path));
        Assert.All(parts.Take(6), p => Assert.Equal(PartSize, p.ContentLength));
        Assert.Equal(4 * MiB, parts[6].ContentLength);
        Assert.All(parts, p => Assert.Equal(p.ContentLength, p.BodyLength));
        Assert.All(parts, p => Assert.Equal("application/octet-stream", p.MediaType));

        var complete = proxy.Requests[^1];
        Assert.Equal("/clips/mp1/complete", complete.Path);
        using var body = JsonDocument.Parse(complete.Text!);
        Assert.Equal(7, body.RootElement.GetProperty("parts").GetArrayLength());
        Assert.Equal("e7", body.RootElement.GetProperty("parts")[6].GetProperty("etag").GetString());
        Assert.DoesNotContain(proxy.Requests, r => r.Method == "DELETE");

        Assert.Equal(new ClipUploadProgress("finishing", size, size), progress.Items[^1]);
        Assert.Contains(new ClipUploadProgress("uploading", size, size), progress.Items);
    }

    [Fact]
    public async Task A503Part_IsRetried()
    {
        var failures = 0;
        var (service, proxy) = Create(HappyProxy(seen =>
            seen.Path == "/clips/mp1/parts/3" && failures++ < 2 ? Json(HttpStatusCode.ServiceUnavailable, new { error = "clip_error" }) : null));

        var result = await service.UploadAsync(MakeFile(100 * MiB), "tok", durationSeconds: 300);

        Assert.Equal("https://revu.lol/mp1", result.Url);
        Assert.Equal(3, proxy.Parts.Count(p => p.Path == "/clips/mp1/parts/3"));
        Assert.DoesNotContain(proxy.Requests, r => r.Method == "DELETE");
    }

    [Fact]
    public async Task PersistentPartFailure_SendsDelete_AndIsNotRetryable()
    {
        var (service, proxy) = Create(HappyProxy(seen =>
            seen.Path == "/clips/mp1/parts/2" ? Json(HttpStatusCode.BadGateway, new { error = "clip_error" }) : null));

        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.UploadAsync(MakeFile(100 * MiB), "tok", durationSeconds: 300));

        Assert.Equal("Sharing is temporarily unavailable. Try again in a moment.", ex.Message);
        Assert.False(ex.Retryable);
        Assert.Equal(4, proxy.Parts.Count(p => p.Path == "/clips/mp1/parts/2"));
        Assert.Contains(proxy.Requests, r => r.Method == "DELETE" && r.Path == "/clips/mp1");
        Assert.DoesNotContain(proxy.Requests, r => r.Path.EndsWith("/complete"));
    }

    [Fact]
    public async Task Cancellation_SendsDelete_EvenThoughThePassedTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var (service, proxy) = Create(HappyProxy(seen =>
        {
            if (seen.Path == "/clips/mp1/parts/2") cts.Cancel();
            return null;
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UploadAsync(MakeFile(100 * MiB), "tok", durationSeconds: 300, ct: cts.Token));

        Assert.Contains(proxy.Requests, r => r.Method == "DELETE" && r.Path == "/clips/mp1");
        Assert.DoesNotContain(proxy.Requests, r => r.Path.EndsWith("/complete"));
    }

    [Fact]
    public async Task CompleteIsRetriedOn5xx_ThenDeletedWhenItKeepsFailing()
    {
        var (service, proxy) = Create(HappyProxy(seen =>
            seen.Path == "/clips/mp1/complete" ? Json(HttpStatusCode.BadGateway, new { error = "clip_error" }) : null));

        await Assert.ThrowsAsync<ClipUploadException>(() => service.UploadAsync(MakeFile(100 * MiB), "tok", durationSeconds: 300));

        Assert.Equal(3, proxy.Requests.Count(r => r.Path == "/clips/mp1/complete"));
        Assert.Contains(proxy.Requests, r => r.Method == "DELETE" && r.Path == "/clips/mp1");
    }

    [Theory]
    [InlineData(429, "too_many_pending_uploads", null, "Another long clip is still uploading. Try again when it finishes.", false)]
    [InlineData(429, "rate_limited", null, "Too many uploads. Wait a moment and try again.", false)]
    [InlineData(413, "payload_too_large", null, "Clip is too large to share (2 GB max).", false)]
    [InlineData(403, "quota_exceeded", null, "Clip-share limit reached (150 active clips). Delete old shared clips, or let them expire.", false)]
    [InlineData(403, "quota_exceeded", "Active clip quota reached. Delete old clips or let them expire.", "Active clip quota reached. Delete old clips or let them expire.", false)]
    [InlineData(503, null, null, "Sharing is temporarily unavailable. Try again in a moment.", true)]
    [InlineData(404, "not_found", null, "Sharing is temporarily unavailable. Try again in a moment.", false)]
    // A proxy older than 3.14: POST /clips/uploads falls through to its 405 guard.
    [InlineData(405, "method_not_allowed", null, "Sharing is temporarily unavailable. Try again in a moment.", false)]
    public async Task InitErrors_MapToUserCopy_AndRetryableOnlyFor5xx(int status, string? code, string? message, string expected, bool retryable)
    {
        var (service, proxy) = Create(HappyProxy(seen =>
            seen.Path == "/clips/uploads" ? Json((HttpStatusCode)status, new { error = code, message }) : null));

        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.UploadAsync(MakeFile(100 * MiB), "tok", durationSeconds: 300));

        Assert.Equal(expected, ex.Message);
        Assert.Equal(retryable, ex.Retryable);
        Assert.False(ex.Unauthorized);
        Assert.DoesNotContain(proxy.Requests, r => r.Method == "DELETE");
    }

    [Theory]
    [InlineData(503, true, false)]
    [InlineData(413, false, false)]
    [InlineData(401, false, true)]
    [InlineData(403, false, true)]
    public async Task LegacyErrors_FlagRetryableAndUnauthorized(int status, bool retryable, bool unauthorized)
    {
        var (service, _) = Create(HappyProxy(seen =>
            seen.Path == "/clips" ? Json((HttpStatusCode)status, new { error = status == 403 ? "login_required" : "x" }) : null));

        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.UploadAsync(MakeFile(MiB), "tok"));

        Assert.Equal(retryable, ex.Retryable);
        Assert.Equal(unauthorized, ex.Unauthorized);
    }

    [Fact]
    public async Task NetworkFailure_OnTheLegacyPath_IsRetryable()
    {
        var (service, _) = Create((_, _) => throw new HttpRequestException("connection reset"));
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.UploadAsync(MakeFile(MiB), "tok"));
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task UnsupportedContainer_IsRejectedBeforeAnyRequest()
    {
        var (service, proxy) = Create(HappyProxy());
        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.UploadAsync(MakeFile(MiB, ".mkv"), "tok"));
        Assert.Equal("Only MP4 and WebM clips can be shared.", ex.Message);
        Assert.Empty(proxy.Requests);
    }

    [Fact]
    public async Task PutTranscript_SendsTheC2Document()
    {
        var (service, proxy) = Create(HappyProxy());
        var doc = TranscriptDocument.Normalize(new[] { new TranscriptSegment(1.234, 3.5, "first gank") }, "en", 60);

        await service.PutTranscriptAsync("abc1234", "tok", doc);

        var put = Assert.Single(proxy.Requests);
        Assert.Equal(("PUT", "/clips/abc1234/transcript", "application/json"), (put.Method, put.Path, put.MediaType));
        Assert.Equal("{\"version\":1,\"language\":\"en\",\"segments\":[{\"start\":1.234,\"end\":3.5,\"text\":\"first gank\"}]}", put.Text);
    }

    [Fact]
    public async Task Delete_ReportsGoneFor200And404_AndFalseOtherwise()
    {
        var status = HttpStatusCode.OK;
        var (service, _) = Create((_, _) => Json(status, new { ok = true }));
        Assert.True(await service.DeleteAsync("a", "tok"));
        status = HttpStatusCode.NotFound;
        Assert.True(await service.DeleteAsync("a", "tok"));
        status = HttpStatusCode.InternalServerError;
        Assert.False(await service.DeleteAsync("a", "tok"));
        Assert.False(await service.DeleteAsync("", "tok"));
    }

    [Theory]
    [InlineData(405, "Sharing is temporarily unavailable. Try again in a moment.", false)]
    [InlineData(404, "The shared clip is no longer available.", true)]
    public async Task PutTranscript_OldProxyIsUnavailable_AndAMissingClipIsGone(int status, string expected, bool gone)
    {
        var (service, _) = Create((_, _) => Json((HttpStatusCode)status, new { error = status == 405 ? "method_not_allowed" : "not_found" }));
        var doc = TranscriptDocument.Normalize(new[] { new TranscriptSegment(1, 2, "x") }, "en", 60);

        var ex = await Assert.ThrowsAsync<ClipUploadException>(() => service.PutTranscriptAsync("abc1234", "tok", doc));

        Assert.Equal(expected, ex.Message);
        Assert.Equal(gone, ex.Gone);
    }

    [Fact]
    public async Task CancelledMultipart_AStalledAbortIsCutShort()
    {
        using var cts = new CancellationTokenSource();
        // The abort DELETE hangs until its token fires (a stalled connection).
        var stalled = Create((seen, ct) =>
        {
            if (seen.Method == "DELETE")
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                ct.ThrowIfCancellationRequested();
            }
            return HappyProxy(s =>
            {
                if (s.Path == "/clips/mp1/parts/2") cts.Cancel();
                return null;
            })(seen, ct);
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stalled.Service.UploadAsync(MakeFile(100 * MiB), "tok", durationSeconds: 300, ct: cts.Token));
        watch.Stop();

        Assert.Contains(stalled.Proxy.Requests, r => r.Method == "DELETE" && r.Path == "/clips/mp1");
        // Well under the 10 s delete timeout: the 2 s budget after cancellation applied.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"abort took {watch.Elapsed}");
    }

    [Fact]
    public async Task CancellationBudget_IsLiveUntilTheTriggerFires_ThenExpires()
    {
        using var trigger = new CancellationTokenSource();
        using var budget = new CancellationBudget(trigger.Token, TimeSpan.FromMilliseconds(50));
        await Task.Delay(100);
        Assert.False(budget.Token.IsCancellationRequested);

        trigger.Cancel();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!budget.Token.IsCancellationRequested && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(budget.Token.IsCancellationRequested);
    }
}
