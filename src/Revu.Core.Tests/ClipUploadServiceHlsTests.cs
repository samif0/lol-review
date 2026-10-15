using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The HLS side of the proxy client (plan 4.4, 4.7, 7.1) against a fake proxy that speaks the HLS contract: status
/// parsing, the init (and an old proxy's MP4 reply), one body on the wire in seq order with at most 8 waiting and never
/// 8 seqs past the lowest unacknowledged one, runs before their segments, retries and the outage window, every response
/// mapping, lost-response replays, and what disposing does.
/// </summary>
public sealed class ClipUploadServiceHlsTests : IDisposable
{
    private const string Id = "hls1234";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "hlsup-" + Guid.NewGuid().ToString("N"));

    public ClipUploadServiceHlsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed record Seen(string Method, string Path, Dictionary<string, string> Query, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);
        public string Route => Path.Replace($"/vod-shares/{Id}/", "");
    }

    private sealed class FakeProxy : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = new();
        public Func<Seen, Task<HttpResponseMessage>> Respond { get; set; } = seen => Task.FromResult(Happy(seen));
        public int Waiting;
        public int MaxWaiting;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? Array.Empty<byte>() : await req.Content.ReadAsByteArrayAsync(ct);
            var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            var seen = new Seen(req.Method.Method, req.RequestUri.AbsolutePath,
                query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => query[k]!), body);
            lock (Requests)
            {
                Requests.Add(seen);
                MaxWaiting = Math.Max(MaxWaiting, ++Waiting);
            }
            try
            {
                return await Respond(seen).WaitAsync(ct);
            }
            finally
            {
                lock (Requests) Waiting--;
            }
        }

        public List<string> Routes()
        {
            lock (Requests) return Requests.Select(r => r.Method + " " + r.Route).ToList();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Happy(Seen seen)
    {
        if (seen.Method == "PUT" && seen.Route.StartsWith("hls/runs/", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, new { k = int.Parse(seen.Route.Split('/')[^1]), from = int.Parse(seen.Query["from"]) });
        if (seen.Method == "PUT" && seen.Route.StartsWith("hls/seg/", StringComparison.Ordinal))
        {
            var seq = int.Parse(seen.Route.Split('/')[^1]);
            return Json(HttpStatusCode.OK, new { seq, committed = seq + 1, media_ms = (seq + 1) * 2000, playable = seq >= 3 });
        }
        if (seen.Method == "POST" && seen.Route == "hls/pause") return Json(HttpStatusCode.OK, new { paused = true, reason = "game" });
        if (seen.Method == "POST" && seen.Route == "hls/end")
        {
            using var doc = JsonDocument.Parse(seen.Text);
            var n = doc.RootElement.GetProperty("segments").GetInt32();
            return Json(HttpStatusCode.Created, new
            {
                id = Id, url = "https://revu.lol/v/" + Id, expires_at = 1_760_659_200, size_bytes = 1234, media_ms = n * 2000,
                segments = n, truncated = doc.RootElement.GetProperty("truncated").GetBoolean(),
            });
        }
        if (seen.Method == "DELETE") return Json(HttpStatusCode.OK, new { ok = true });
        return Json(HttpStatusCode.NotFound, new { error = "not_found" });
    }

    private static ClipUploadService Service(FakeProxy proxy) =>
        new(new HttpClient(proxy), NullLogger<ClipUploadService>.Instance, "https://proxy.test")
        {
            PartRetryDelays = [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)],
            VodPartOutageDelays = [TimeSpan.FromMilliseconds(5)],
            VodPartOutageWindow = TimeSpan.FromSeconds(30),
        };

    private static VodHlsUploadSession Session(FakeProxy proxy, TimeSpan? outageWindow = null)
    {
        var service = Service(proxy);
        if (outageWindow is { } window) service.VodPartOutageWindow = window;
        var info = new VodHlsSessionInfo(Id, "https://revu.lol/v/" + Id, 1_760_659_200, 1_760_421_600, 1153, 16_777_216, 16_384, 8000, 8);
        return new VodHlsUploadSession(new HttpClient(proxy), NullLogger.Instance, "https://proxy.test", "session-token", info,
            service.VodRetry, ct => service.DeleteAsync(Id, "session-token", ct));
    }

    private static readonly byte[] InitBytes = [0, 0, 0, 16, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 1, 2, 3, 4, 5, 6, 7, 8];

    private HlsSegment Segment(int seq, int run = 0)
    {
        var path = Path.Combine(_dir, $"seg-{seq:D5}.m4s");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("moof+mdat " + seq));
        return new HlsSegment(seq, 2000, new FileInfo(path).Length, path, run, seq * 2.0, seq * 2.0 + 2);
    }

    private void AddRun(VodHlsUploadSession session, int run, int from, int count)
    {
        session.Add(new HlsInit(run, from, InitBytes));
        for (var seq = from; seq < from + count; seq++) session.Add(Segment(seq, run));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time");
            await Task.Delay(5);
        }
    }

    // ── Status and init ──────────────────────────────────────────────────────

    [Fact]
    public async Task Status_ParsesTheHlsSettingsAndTheSharesFormat()
    {
        var proxy = new FakeProxy
        {
            Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new
            {
                allowed = true, sharing = true, serving = true, now = 1_760_400_000,
                hls = new { enabled = true, segment_seconds = 2, target_duration = 6, max_segment_bytes = 16_777_216, init_max_bytes = 16_384, playable_ms = 8000, max_runs = 8 },
                shares = new object[]
                {
                    new { id = "hls1234", status = "uploading", quality = "1080p60", size_bytes = 5, created_at = 1, expires_at = 2, format = "hls", segments = 13, paused = true },
                    new { id = "mp41234", status = "ready", quality = "720p30", size_bytes = 5, created_at = 1, expires_at = 2 },
                },
            })),
        };
        var status = await Service(proxy).GetVodShareStatusAsync("token");

        Assert.Equal(new VodShareHlsInfo(true, 2, 6, 16_777_216, 16_384, 8000, 8), status.Hls);
        Assert.Equal(("hls", 13, true), (status.Shares[0].Format, status.Shares[0].Segments, status.Shares[0].Paused));
        Assert.Equal(("mp4", (int?)null, false), (status.Shares[1].Format, status.Shares[1].Segments, status.Shares[1].Paused));
    }

    [Fact]
    public async Task Status_WithoutHls_OrWithSettingsThisDesktopCannotKeep_IsOff()
    {
        var old = new FakeProxy { Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new { allowed = true, sharing = true, serving = true, now = 1 })) };
        Assert.Null((await Service(old).GetVodShareStatusAsync("token")).Hls);

        var six = new FakeProxy
        {
            Respond = _ => Task.FromResult(Json(HttpStatusCode.OK, new
            {
                allowed = true, sharing = true, serving = true, now = 1,
                hls = new { enabled = true, segment_seconds = 6, target_duration = 7, max_segment_bytes = 1, init_max_bytes = 1, playable_ms = 1, max_runs = 1 },
            })),
        };
        Assert.False((await Service(six).GetVodShareStatusAsync("token")).Hls!.Enabled);
    }

    [Fact]
    public async Task Start_ReservesAnHlsShare_AndReturnsItsSession()
    {
        var proxy = new FakeProxy
        {
            Respond = _ => Task.FromResult(Json(HttpStatusCode.Created, new
            {
                id = Id, format = "hls", part_count = 1153, segment_seconds = 2, target_duration = 6, max_segment_bytes = 16_777_216,
                init_max_bytes = 16_384, playable_ms = 8000, max_runs = 8, expires_at = 1_760_421_600,
                share_expires_at = 1_760_659_200, url = "https://revu.lol/v/" + Id,
            })),
        };
        var start = await Service(proxy).StartVodHlsUploadAsync("token", new VodUploadRequest("1080p", 2256, 60), 1_500_000_000);

        var seen = Assert.Single(proxy.Requests);
        Assert.Equal(("POST", "/vod-shares/uploads"), (seen.Method, seen.Path));
        Assert.Equal(("1500000000", "2256", "1080p", "60", "hls"),
            (seen.Query["size"], seen.Query["duration"], seen.Query["quality"], seen.Query["fps"], seen.Query["format"]));
        Assert.Equal(new VodUploadStarted(Id, "https://revu.lol/v/" + Id, 1_760_659_200), start.Started);
        Assert.Null(start.Mp4);
        Assert.Equal(new VodHlsSessionInfo(Id, "https://revu.lol/v/" + Id, 1_760_659_200, 1_760_421_600, 1153, 16_777_216, 16_384, 8000, 8),
            start.Session!.Info);
        await start.Session.DisposeAsync();
    }

    [Fact]
    public async Task Start_OnAnOldProxy_KeepsItsMp4RowForTodaysPath()
    {
        var proxy = new FakeProxy
        {
            Respond = _ => Task.FromResult(Json(HttpStatusCode.Created, new
            {
                id = Id, part_size = 16_777_216, part_count = 90, expires_at = 1_760_421_600, share_expires_at = 1_760_659_200,
                url = "https://revu.lol/v/" + Id,
            })),
        };
        var start = await Service(proxy).StartVodHlsUploadAsync("token", new VodUploadRequest("1080p", 2256, 60), 1_500_000_000);
        Assert.Null(start.Session);
        Assert.Equal(new VodMp4Reservation(Id, 16_777_216, 90, 1_500_000_000, 1_760_659_200, "https://revu.lol/v/" + Id), start.Mp4);
        Assert.Single(proxy.Requests);
    }

    [Fact]
    public async Task Start_Refused_KeepsTheProxyCode_AndAMalformedReplyIsDeleted()
    {
        var off = new FakeProxy { Respond = _ => Task.FromResult(Json(HttpStatusCode.Conflict, new { error = "hls_unavailable" })) };
        var error = await Assert.ThrowsAsync<ClipUploadException>(() =>
            Service(off).StartVodHlsUploadAsync("token", new VodUploadRequest("1080p", 2256, 60), 1_500_000_000));
        Assert.Equal("hls_unavailable", error.Code);

        var odd = new FakeProxy
        {
            Respond = seen => Task.FromResult(seen.Method == "DELETE" ? Json(HttpStatusCode.OK, new { ok = true })
                : Json(HttpStatusCode.Created, new { id = Id, format = "hls", part_count = 10, segment_seconds = 6, max_segment_bytes = 1, init_max_bytes = 1, max_runs = 8 })),
        };
        var bad = await Assert.ThrowsAsync<ClipUploadException>(() =>
            Service(odd).StartVodHlsUploadAsync("token", new VodUploadRequest("1080p", 2256, 60), 1_500_000_000));
        Assert.Equal("upload_failed", bad.Code);
        Assert.Equal(new[] { "POST /vod-shares/uploads", "DELETE /clips/" + Id }, odd.Requests.Select(r => r.Method + " " + r.Path));
    }

    // ── The pump ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_SendsTheRunFirst_ThenEverySegmentInOrder_AndCompletes()
    {
        var proxy = new FakeProxy();
        await using var session = Session(proxy);
        var states = new List<VodHlsUploadState>();
        session.Progress = s => { lock (states) states.Add(s); };
        AddRun(session, 0, 0, 5);
        session.CompleteAdding();
        session.Start(CancellationToken.None);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "PUT hls/runs/0", "PUT hls/seg/0", "PUT hls/seg/1", "PUT hls/seg/2", "PUT hls/seg/3", "PUT hls/seg/4" },
            proxy.Routes());
        var run = proxy.Requests[0];
        Assert.Equal(("0", "1080p", "60"), (run.Query["from"], run.Query["quality"], run.Query["fps"]));
        Assert.Equal(InitBytes, run.Body);
        Assert.Equal(("2000", "0"), (proxy.Requests[1].Query["ms"], proxy.Requests[1].Query["run"]));
        var state = session.State;
        Assert.Equal((5, 10.0, true, 0, 1), (state.Acked, state.AckedSeconds, state.Playable, state.Backlog, state.RunsAccepted));
        Assert.Contains(states, s => s.Acked == 5);
    }

    [Fact]
    public async Task AtMostEightWait_AndNeverEightSeqsPastTheLowestUnacknowledged()
    {
        var held = new TaskCompletionSource();
        var proxy = new FakeProxy
        {
            Respond = async seen =>
            {
                if (seen.Route == "hls/seg/0") await held.Task;
                return Happy(seen);
            },
        };
        await using var session = Session(proxy);
        AddRun(session, 0, 0, 20);
        session.CompleteAdding();
        session.Start(CancellationToken.None);

        // Seq 0 is held: 1..7 go and are acknowledged, 8 must wait for 0.
        await WaitUntilAsync(() => proxy.Routes().Contains("PUT hls/seg/7") && session.State.Backlog == 13);
        await Task.Delay(100);
        Assert.DoesNotContain("PUT hls/seg/8", proxy.Routes());
        held.SetResult();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Enumerable.Range(0, 20).Select(s => "PUT hls/seg/" + s), proxy.Routes().Skip(1));
        Assert.True(proxy.MaxWaiting <= VodHlsUploadSession.MaxAwaiting, $"{proxy.MaxWaiting} requests waited at once");
    }

    [Fact]
    public async Task ARunsSegments_WaitForItsInitToBeAccepted()
    {
        var held = new TaskCompletionSource();
        var proxy = new FakeProxy
        {
            Respond = async seen =>
            {
                if (seen.Route == "hls/runs/1") await held.Task;
                return Happy(seen);
            },
        };
        await using var session = Session(proxy);
        AddRun(session, 0, 0, 3);
        AddRun(session, 1, 3, 3);
        session.CompleteAdding();
        session.Start(CancellationToken.None);

        await WaitUntilAsync(() => proxy.Routes().Contains("PUT hls/runs/1"));
        await Task.Delay(100);
        Assert.DoesNotContain("PUT hls/seg/3", proxy.Routes());
        held.SetResult();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "PUT hls/runs/0", "PUT hls/seg/0", "PUT hls/seg/1", "PUT hls/seg/2", "PUT hls/runs/1", "PUT hls/seg/3",
            "PUT hls/seg/4", "PUT hls/seg/5" }, proxy.Routes());
        Assert.Equal("3", proxy.Requests.Single(r => r.Route == "hls/runs/1").Query["from"]);
        Assert.Equal(2, session.State.RunsAccepted);
    }

    [Fact]
    public async Task ServerErrorsAndConnectionFailures_AreRetried()
    {
        var serverErrors = 0;
        var refused = 0;
        var proxy = new FakeProxy
        {
            Respond = seen =>
            {
                if (seen.Route == "hls/seg/2" && Interlocked.Increment(ref serverErrors) <= 2)
                    return Task.FromResult(Json(HttpStatusCode.BadGateway, new { error = "clip_error" }));
                if (seen.Route == "hls/seg/3" && Interlocked.Increment(ref refused) <= 2)
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
                return Task.FromResult(Happy(seen));
            },
        };
        await using var session = Session(proxy);
        AddRun(session, 0, 0, 5);
        session.CompleteAdding();
        session.Start(CancellationToken.None);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, proxy.Routes().Count(r => r == "PUT hls/seg/2"));
        Assert.Equal(3, proxy.Routes().Count(r => r == "PUT hls/seg/3"));
        Assert.Equal(5, session.State.Acked);
    }

    [Fact]
    public async Task AnOutageLongerThanTheWindow_FailsAsNetwork()
    {
        var proxy = new FakeProxy
        {
            Respond = seen => seen.Route == "hls/seg/1"
                ? throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")
                : Task.FromResult(Happy(seen)),
        };
        await using var session = Session(proxy, outageWindow: TimeSpan.FromMilliseconds(200));
        AddRun(session, 0, 0, 3);
        session.CompleteAdding();
        session.Start(CancellationToken.None);
        var error = await Assert.ThrowsAsync<VodHlsUploadException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(VodHlsFailure.Network, error.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found", VodHlsFailure.Stopped)]
    [InlineData(HttpStatusCode.Conflict, "run_mismatch", VodHlsFailure.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, "bad_duration", VodHlsFailure.Conflict)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "over_reservation", VodHlsFailure.TooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, "too_many_part_uploads", VodHlsFailure.Limited)]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized", VodHlsFailure.Unauthorized)]
    public async Task EveryTerminalAnswer_IsMapped(HttpStatusCode status, string code, VodHlsFailure kind)
    {
        var proxy = new FakeProxy
        {
            Respond = seen => Task.FromResult(seen.Route == "hls/seg/4" ? Json(status, new { error = code }) : Happy(seen)),
        };
        var session = Session(proxy);
        AddRun(session, 0, 0, 6);
        session.CompleteAdding();
        session.Start(CancellationToken.None);
        var error = await Assert.ThrowsAsync<VodHlsUploadException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((kind, code), (error.Kind, error.Code));
        Assert.Equal(1, proxy.Routes().Count(r => r == "PUT hls/seg/4"));

        await session.DisposeAsync();
        var settle = proxy.Routes().Where(r => r is "POST hls/end" || r.StartsWith("DELETE", StringComparison.Ordinal)).ToList();
        // A stopped share is left alone; anything else that was playable ends at the edge.
        Assert.Equal(kind == VodHlsFailure.Stopped ? Array.Empty<string>() : ["POST hls/end"], settle);
    }

    [Fact]
    public async Task ATooLargeAnswer_LetsWhatWasSentSettle_BeforeFailing()
    {
        var release = new TaskCompletionSource();
        var fifthSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = new FakeProxy
        {
            Respond = async seen =>
            {
                if (seen.Route == "hls/seg/5")
                {
                    fifthSent.TrySetResult();
                    await release.Task;
                }
                // The 413 comes back only once 5 is on its way, as when answers lag behind the bodies (on a slow machine
                // it could otherwise arrive first, and then 5 is rightly never sent).
                if (seen.Route == "hls/seg/4")
                {
                    await fifthSent.Task;
                    return Json(HttpStatusCode.RequestEntityTooLarge, new { error = "over_reservation" });
                }
                return Happy(seen);
            },
        };
        await using var session = Session(proxy);
        AddRun(session, 0, 0, 8);
        session.CompleteAdding();
        session.Start(CancellationToken.None);
        await WaitUntilAsync(() => proxy.Routes().Contains("PUT hls/seg/5"));
        await Task.Delay(100);
        Assert.False(session.Completion.IsCompleted);
        release.SetResult();
        var error = await Assert.ThrowsAsync<VodHlsUploadException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(VodHlsFailure.TooLarge, error.Kind);
        var state = session.State;
        Assert.Equal((4, true), (state.Acked, state.CommitsPastEdge));
    }

    [Fact]
    public async Task StopAndStart_SendWhatWasNotAcknowledged_Again()
    {
        var hold = new TaskCompletionSource();
        var proxy = new FakeProxy
        {
            Respond = async seen =>
            {
                if (seen.Route == "hls/seg/3") await hold.Task;
                return Happy(seen);
            },
        };
        await using var session = Session(proxy);
        AddRun(session, 0, 0, 5);
        session.Start(CancellationToken.None);
        await WaitUntilAsync(() => proxy.Routes().Contains("PUT hls/seg/4"));
        await session.StopAsync();
        Assert.Equal(3, session.State.Acked);

        hold.SetResult();
        session.CompleteAdding();
        session.Start(CancellationToken.None);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, proxy.Routes().Count(r => r == "PUT hls/seg/3"));
        Assert.Equal(5, session.State.Acked);
    }

    [Fact]
    public async Task Drain_FinishesWhatWasSent_StopsBeforeTheRest_AndDropFromForgetsIt()
    {
        var gate = new TaskCompletionSource();
        var failedOnce = 0;
        var proxy = new FakeProxy
        {
            Respond = async seen =>
            {
                if (seen.Route == "hls/seg/2")
                {
                    await gate.Task;
                    if (Interlocked.Exchange(ref failedOnce, 1) == 0) return Json(HttpStatusCode.ServiceUnavailable, new { error = "clip_error" });
                }
                return Happy(seen);
            },
        };
        await using var session = Session(proxy);
        AddRun(session, 0, 0, 4);
        session.Start(CancellationToken.None);
        await WaitUntilAsync(() => proxy.Routes().Contains("PUT hls/seg/3"));
        var drain = session.DrainAsync(CancellationToken.None);
        session.Add(Segment(4));    // announced while draining: it waits for the next start
        gate.SetResult();
        Assert.Equal(4, await drain.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain("PUT hls/seg/4", proxy.Routes());
        Assert.Equal(2, proxy.Routes().Count(r => r == "PUT hls/seg/2"));

        session.DropFrom(4);
        Assert.Equal(0, session.State.Backlog);
    }

    // ── Control routes and replays ───────────────────────────────────────────

    [Fact]
    public async Task ALostRunAnswer_IsReplayed_AndPauseAndEndRetry()
    {
        var runAttempts = 0;
        var pauseAttempts = 0;
        var endAttempts = 0;
        var proxy = new FakeProxy
        {
            Respond = seen =>
            {
                if (seen.Route == "hls/runs/0" && Interlocked.Increment(ref runAttempts) == 1) throw new IOException("connection reset");
                if (seen.Route == "hls/pause" && Interlocked.Increment(ref pauseAttempts) == 1)
                    return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, new { error = "clip_error" }));
                if (seen.Route == "hls/end" && Interlocked.Increment(ref endAttempts) == 1)
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
                return Task.FromResult(Happy(seen));
            },
        };
        await using var session = Session(proxy);
        AddRun(session, 0, 0, 4);
        session.CompleteAdding();
        session.Start(CancellationToken.None);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, proxy.Routes().Count(r => r == "PUT hls/runs/0"));

        await session.PauseAsync("game", CancellationToken.None);
        Assert.Equal("{\"reason\":\"game\"}", proxy.Requests.Last(r => r.Route == "hls/pause").Text);
        var end = await session.EndAsync(4, truncated: false, CancellationToken.None);
        Assert.Equal(new VodHlsEndResult(Id, "https://revu.lol/v/" + Id, 1_760_659_200, 1234, 8000, 4, false), end);
        Assert.Equal("{\"segments\":4,\"truncated\":false}", proxy.Requests.Last(r => r.Route == "hls/end").Text);
        Assert.Equal((2, 2), (pauseAttempts, endAttempts));
    }

    [Fact]
    public async Task EndRefused_ThrowsItsKind()
    {
        var proxy = new FakeProxy
        {
            Respond = seen => Task.FromResult(seen.Route == "hls/end"
                ? Json(HttpStatusCode.Conflict, new { error = "segments_incomplete", committed = 3 })
                : Happy(seen)),
        };
        await using var session = Session(proxy);
        var error = await Assert.ThrowsAsync<VodHlsUploadException>(() => session.EndAsync(5, false, CancellationToken.None));
        Assert.Equal((VodHlsFailure.Conflict, "segments_incomplete"), (error.Kind, error.Code));
    }

    [Fact]
    public async Task Dispose_DeletesAShareThatNeverPlayed_AndEndsOneThatDid()
    {
        var proxy = new FakeProxy();
        var early = Session(proxy);
        AddRun(early, 0, 0, 2);       // 2 segments: never playable (playable from the 4th)
        early.CompleteAdding();
        early.Start(CancellationToken.None);
        await early.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await early.DisposeAsync();
        Assert.Equal("DELETE /clips/" + Id, proxy.Requests.Last().Method + " " + proxy.Requests.Last().Path);

        var later = new FakeProxy();
        var watched = Session(later);
        AddRun(watched, 0, 0, 6);
        watched.Start(CancellationToken.None);
        await WaitUntilAsync(() => watched.State.Acked == 6);
        await watched.DisposeAsync();
        var end = later.Requests.Last();
        Assert.Equal("POST hls/end", end.Method + " " + end.Route);
        Assert.Equal("{\"segments\":6,\"truncated\":true}", end.Text);

        var ended = new FakeProxy();
        var done = Session(ended);
        await done.EndAsync(1, false, CancellationToken.None);
        await done.DisposeAsync();
        Assert.Single(ended.Requests);
    }
}
