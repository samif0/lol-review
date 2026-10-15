using System.Text.Json;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The full-game share worker (contract 7.5) with the proxy and ffmpeg faked: the start
/// pre-check order, phases and SSE payloads, the store, cancellation and cleanup.
/// </summary>
public sealed partial class VodShareWorkerTests
{
    private const long GameId = VodShareHarness.GameId;

    private static async Task<(VodShareHarness H, EventTap Tap)> StartAsync(bool signedIn = true, bool started = true,
        int gameDuration = 1834)
    {
        var h = new VodShareHarness();
        await h.InitAsync(signedIn, started, gameDuration);
        return (h, new EventTap(h.Hub));
    }

    private static JsonElement Body(VodShareStartReply reply) =>
        JsonSerializer.SerializeToElement(reply.Body, SidecarJson.CreateOptions());

    private static async Task<JsonElement> StartShareAsync(VodShareHarness h, string quality = "1080p", bool notes = false)
    {
        var reply = await h.Worker.StartAsync(GameId, quality, notes, CancellationToken.None);
        Assert.Equal(200, reply.Status);
        return Body(reply);
    }

    private static string? Code(JsonElement body) =>
        body.TryGetProperty("errorCode", out var c) ? c.GetString() : null;

    private static Task<JsonElement> WaitPhaseAsync(EventTap tap, string phase) =>
        tap.WaitForAsync("vodShareProgress", e => e.GetProperty("phase").GetString() == phase);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time");
            await Task.Delay(10);
        }
    }

    // ── Pre-checks ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PreChecks_RefuseInContractOrder()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            // 5 before 6: a game in progress wins over being signed out.
            h.Live.SetGameInProgress(true);
            h.SignOut();
            Assert.Equal("game_in_progress", Code(await StartShareAsync(h)));
            h.Live.SetGameInProgress(false);

            var needsLogin = await StartShareAsync(h);
            Assert.Equal(("needs_login", true), (Code(needsLogin), needsLogin.GetProperty("needsLogin").GetBoolean()));
            Assert.Equal(0, h.Client.StatusCalls);
            h.SignIn();

            // 4 before 5: no recording wins over a game in progress.
            h.Live.SetGameInProgress(true);
            var recording = h.Recording;
            h.Recording = null;
            Assert.Equal("no_recording", Code(await StartShareAsync(h)));
            h.Recording = recording;
            h.Live.SetGameInProgress(false);

            h.Client.StatusError = new ClipUploadException("offline", retryable: true) { Code = "network" };
            Assert.Equal("network", Code(await StartShareAsync(h)));
            h.Client.StatusError = new ClipUploadException("expired", unauthorized: true) { Code = "unauthorized" };
            Assert.Equal("needs_login", Code(await StartShareAsync(h)));
            h.Client.StatusError = null;

            h.Client.Status = () => FakeVodClient.Allowed() with { Supported = false, Allowed = false };
            Assert.Equal("not_allowed", Code(await StartShareAsync(h)));
            h.Client.Status = () => FakeVodClient.Allowed() with { Allowed = false, Limits = null, Usage = null, Pool = null };
            Assert.Equal("not_allowed", Code(await StartShareAsync(h)));
            h.Client.Status = () => FakeVodClient.Allowed(sharing: false);
            Assert.Equal("sharing_paused", Code(await StartShareAsync(h)));
            h.Client.Status = () => FakeVodClient.Allowed(pending: 1, live: 3);
            Assert.Equal("busy", Code(await StartShareAsync(h)));
            h.Client.Status = () => FakeVodClient.Allowed(live: 3, startsToday: 6);
            Assert.Equal("live_limit", Code(await StartShareAsync(h)));
            h.Client.Status = () => FakeVodClient.Allowed(startsToday: 6, freeBytes: 0);
            var daily = await StartShareAsync(h);
            Assert.Equal(("daily_limit", FakeVodClient.Now + 86_400), (Code(daily), daily.GetProperty("retryAt").GetInt64()));

            h.Client.Status = () => FakeVodClient.Allowed(freeBytes: 500_000_000,
                releases: [new VodSharePoolRelease(FakeVodClient.Now + 3600, 400_000_000, 400_000_000)]);
            var full = await StartShareAsync(h);
            Assert.Equal(("pool_full", FakeVodClient.Now + 3600), (Code(full), full.GetProperty("retryAt").GetInt64()));

            h.Client.Status = () => FakeVodClient.Allowed();
            h.FreeDisk = 1_000_000_000;
            Assert.Equal("disk_full", Code(await StartShareAsync(h)));

            // No refusal ever created a job.
            Assert.Empty(h.Encoder.Encodes);
            Assert.Null((await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Job);
        }
    }

    [Fact]
    public async Task PreChecks_DurationProbeFallsBackToTheGame_AndTooLongIsRefused()
    {
        var (h, tap) = await StartAsync(gameDuration: 0);
        using (h)
        using (tap)
        {
            h.Mixer.ProbeDuration = 0;
            Assert.Equal("no_recording", Code(await StartShareAsync(h)));

            var (h2, tap2) = await StartAsync(gameDuration: 7300);
            using (h2)
            using (tap2)
            {
                h2.Mixer.ProbeDuration = 0;
                Assert.Equal("too_long", Code(await StartShareAsync(h2)));
                Assert.True((await h2.Worker.GetStatusAsync(GameId, CancellationToken.None)).Recording.TooLong);
            }
        }
    }

    [Fact]
    public async Task NotStarted_RefusesStarting_AndBadInputIs400()
    {
        var (h, tap) = await StartAsync(started: false);
        using (h)
        using (tap)
        {
            // Not "busy": no other game is being shared, Revu just isn't ready yet.
            var refused = await StartShareAsync(h);
            Assert.Equal("starting", Code(refused));
            Assert.Equal(JsonValueKind.Null, refused.GetProperty("busyGameId").ValueKind);
            Assert.Equal(400, (await h.Worker.StartAsync(GameId, "4k", false, CancellationToken.None)).Status);
            Assert.Equal(400, (await h.Worker.StartAsync(0, "1080p", false, CancellationToken.None)).Status);
        }
    }

    [Fact]
    public async Task AlreadyShared_ReturnsTheLink_WithoutTheProxy()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Store.Put(h.Entry());
            h.SignOut();
            var body = await StartShareAsync(h);
            Assert.True(body.GetProperty("alreadyShared").GetBoolean());
            Assert.Equal("https://revu.lol/v/old1234", body.GetProperty("share").GetProperty("url").GetString());
            Assert.Equal(0, h.Client.StatusCalls);
        }
    }

    [Fact]
    public async Task ATightPool_StartsAt720p_WithReasonPool()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Client.Status = () => FakeVodClient.Allowed(freeBytes: 1_000_000_000);
            var body = await StartShareAsync(h);
            Assert.Equal((true, "720p", "pool"), (body.GetProperty("accepted").GetBoolean(),
                body.GetProperty("quality").GetString(), body.GetProperty("qualityReason").GetString()));
            await WaitPhaseAsync(tap, "done");
            Assert.Equal(VodShareQuality.P720, h.Encoder.Encodes.Single().Quality);
            Assert.Equal("720p", h.Client.Uploads.Single().Request.Quality);
        }
    }

    // ── The job ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Share_PublishesEveryPhase_StoresTheLink_AndCleansUp()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            var body = await StartShareAsync(h, notes: true);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("qualityReason").ValueKind);

            var queued = await WaitPhaseAsync(tap, "queued");
            Assert.Equal(new[] { "gameId", "state", "phase", "percent", "quality", "qualityReason", "url", "expiresAt",
                "markersAttached", "errorCode", "error", "retryAt", "needsLogin", "linkChanged", "format", "mediaSeconds", "durationSeconds",
                "watchable", "uplinkSlow", "paused", "truncated" }, queued.EnumerateObject().Select(p => p.Name));
            Assert.Equal(("mp4", JsonValueKind.Null, 2255.4), (queued.GetProperty("format").GetString(),
                queued.GetProperty("mediaSeconds").ValueKind, queued.GetProperty("durationSeconds").GetDouble()));
            Assert.Equal(("running", 0, "1080p"), (queued.GetProperty("state").GetString(), queued.GetProperty("percent").GetInt32(),
                queued.GetProperty("quality").GetString()));
            await WaitPhaseAsync(tap, "preparing");
            await tap.WaitForAsync("vodShareProgress", e => e.GetProperty("phase").GetString() == "uploading"
                && e.GetProperty("percent").GetInt32() == 0);
            await WaitPhaseAsync(tap, "finishing");
            var done = await WaitPhaseAsync(tap, "done");

            Assert.Equal(("done", "https://revu.lol/v/vod1234", true), (done.GetProperty("state").GetString(),
                done.GetProperty("url").GetString(), done.GetProperty("markersAttached").GetBoolean()));
            Assert.Equal(FakeVodClient.Now + 259_200, done.GetProperty("expiresAt").GetInt64());

            var upload = h.Client.Uploads.Single();
            Assert.True(upload.FileExisted);
            Assert.StartsWith(Path.Combine(h.TempDir, $"game-{GameId}-"), upload.File);
            Assert.Equal(new VodUploadRequest("1080p", 2256), upload.Request);
            Assert.Equal(h.Recording, h.Encoder.Encodes.Single().Source);
            Assert.Empty(Directory.EnumerateFiles(h.TempDir));
            // The upload ran with the encode: it reserved the estimate plus 3% and the copy was padded to it.
            var started = Assert.Single(h.Client.WhileWriting);
            Assert.False(started.EncodeDone);
            Assert.Equal(1_396_160_262, started.Declared);
            Assert.Equal((upload.File, 1_396_160_262L), Assert.Single(h.Pads));
            Assert.Equal(1_396_160_262, upload.Declared);

            var entry = h.Store.Get(GameId)!;
            Assert.Equal(new VodShareEntry(GameId, "vod1234", "https://revu.lol/v/vod1234", "1080p", true, 1_315_583_333,
                FakeVodClient.Now, FakeVodClient.Now + 259_200, true), entry);
            Assert.Empty(h.Cleanup.Snapshot());

            var markers = Assert.Single(h.Client.Markers);
            Assert.Equal("vod1234", markers.Id);
            using var doc = JsonDocument.Parse(markers.Json);
            Assert.Equal("DRAGON", doc.RootElement.GetProperty("markers")[0].GetProperty("kind").GetString());
            Assert.Equal("my note", doc.RootElement.GetProperty("notes")[0].GetProperty("text").GetString());

            h.Client.Status = () => FakeVodClient.Allowed(live: 1,
                shares: [new VodShareServerShare("vod1234", "ready", "1080p", 1_315_583_333, FakeVodClient.Now, FakeVodClient.Now + 259_200)]);
            var status = await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            Assert.Equal(("done", "done"), (status.Job!.State, status.Job.Phase));
            Assert.Equal("https://revu.lol/v/vod1234", status.Share!.Url);
        }
    }

    [Fact]
    public async Task A60FpsRecording_IsSharedAt60_AndTheUploadStartsWithTheEncode()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Mixer.ProbeFrameRate = 60;
            var status = await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            Assert.Equal(60, status.Recording.Fps);
            Assert.Equal(new VodShareEstimatesDto(1_502_096_400, 915_692_400), status.Estimates);

            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "done");

            Assert.Equal(60, h.Encoder.Encodes.Single().Fps);
            var started = Assert.Single(h.Client.WhileWriting);
            Assert.False(started.EncodeDone);
            Assert.Equal((new VodUploadRequest("1080p", 2256, 60), 1_547_159_292L), (started.Request, started.Declared));
            Assert.Equal(1_547_159_292, Assert.Single(h.Pads).Size);
            Assert.Equal(1_547_159_292, h.Client.Uploads.Single().Declared);
        }
    }

    [Fact]
    public async Task ACopyLargerThanItsReservation_GivesUpTheEarlyUpload_AndUploadsTheFinishedFile()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.SizeFor = _ => 1_396_160_263;   // one byte past the 1080p30 reservation
            await StartShareAsync(h);
            var done = await WaitPhaseAsync(tap, "done");

            Assert.Equal(new bool?[] { false }, h.Client.WhileWritingOutcomes);
            var upload = h.Client.Uploads.Single();
            Assert.Null(upload.Declared);
            Assert.Equal("1080p", upload.Request.Quality);
            Assert.Empty(h.Pads);
            Assert.Equal("https://revu.lol/v/vod1234", done.GetProperty("url").GetString());
        }
    }

    [Fact]
    public async Task AnEncodeThatStartsOver_GivesUpTheEarlyUpload_AndUploadsTheFinishedFile()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.Restarts = true;
            h.Client.InitStarted = FakeVodClient.Started("first12");
            await StartShareAsync(h);
            var done = await WaitPhaseAsync(tap, "done");

            Assert.Equal(new bool?[] { false }, h.Client.WhileWritingOutcomes);
            Assert.Null(h.Client.Uploads.Single().Declared);
            Assert.Empty(h.Pads);
            // The first upload's link went with it; the finished share has a new one.
            Assert.Equal(("https://revu.lol/v/vod1234", true),
                (done.GetProperty("url").GetString(), done.GetProperty("linkChanged").GetBoolean()));
        }
    }

    [Fact]
    public async Task TheLinkShowsAsSoonAsTheUploadStarts_LongBeforeItFinishes()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Encoder.DuringEncode = ct => release.Task.WaitAsync(ct);
            await StartShareAsync(h);

            // The copy is still being encoded and the link is out: its page shows the upload's progress.
            var early = await tap.WaitForAsync("vodShareProgress", e => e.GetProperty("state").GetString() == "running"
                && e.GetProperty("url").GetString() == "https://revu.lol/v/vod1234");
            Assert.Equal((FakeVodClient.Now + 259_200, false),
                (early.GetProperty("expiresAt").GetInt64(), early.GetProperty("linkChanged").GetBoolean()));
            Assert.Empty(h.Client.Uploads);
            var job = (await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Job!;
            Assert.Equal(("running", "https://revu.lol/v/vod1234", (long?)(FakeVodClient.Now + 259_200), false),
                (job.State, job.Url, job.ExpiresAt, job.LinkChanged));
            // Only a finished share is stored; until then a crash leaves the upload queued for deletion.
            Assert.Null(h.Store.Get(GameId));
            Assert.Contains("vod1234", h.Cleanup.Snapshot());

            // Later progress keeps the link, and the finished share has the same one.
            release.SetResult();
            var finishing = await WaitPhaseAsync(tap, "finishing");
            Assert.Equal("https://revu.lol/v/vod1234", finishing.GetProperty("url").GetString());
            var done = await WaitPhaseAsync(tap, "done");
            Assert.Equal(("https://revu.lol/v/vod1234", false),
                (done.GetProperty("url").GetString(), done.GetProperty("linkChanged").GetBoolean()));
            Assert.Equal("https://revu.lol/v/vod1234", h.Store.Get(GameId)!.Url);
        }
    }

    [Fact]
    public async Task AGivenUpUpload_TakesItsLinkDown_AndTheNewLinkSaysItChanged()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Client.InitStarted = FakeVodClient.Started("first12");
            h.Encoder.SizeFor = _ => 1_396_160_263;   // one byte past the 1080p30 reservation
            await StartShareAsync(h);

            await tap.WaitForAsync("vodShareProgress", e => e.GetProperty("url").GetString() == "https://revu.lol/v/first12");
            var dropped = await tap.WaitForAsync("vodShareProgress", e => e.GetProperty("state").GetString() == "running"
                && e.GetProperty("url").ValueKind == JsonValueKind.Null && e.GetProperty("linkChanged").GetBoolean());
            Assert.Equal(JsonValueKind.Null, dropped.GetProperty("expiresAt").ValueKind);
            var replaced = await tap.WaitForAsync("vodShareProgress",
                e => e.GetProperty("url").GetString() == "https://revu.lol/v/vod1234");
            Assert.Equal(("running", true), (replaced.GetProperty("state").GetString(), replaced.GetProperty("linkChanged").GetBoolean()));
            var done = await WaitPhaseAsync(tap, "done");
            Assert.Equal(("https://revu.lol/v/vod1234", true),
                (done.GetProperty("url").GetString(), done.GetProperty("linkChanged").GetBoolean()));
            Assert.Equal("vod1234", h.Store.Get(GameId)!.Id);
        }
    }

    [Fact]
    public async Task ACancelledShare_NoLongerCarriesItsLink()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.DuringEncode = ct => Task.Delay(Timeout.Infinite, ct);
            await StartShareAsync(h);
            await tap.WaitForAsync("vodShareProgress", e => e.GetProperty("url").GetString() == "https://revu.lol/v/vod1234");

            Assert.True(h.Worker.Cancel(GameId));
            var cancelled = await WaitPhaseAsync(tap, "cancelled");
            Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("url").ValueKind);
            var job = (await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Job!;
            Assert.Equal(("cancelled", (string?)null), (job.State, job.Url));
        }
    }

    [Fact]
    public async Task ARefusedUpload_StopsTheEncode_AndReportsTheRefusal()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.DuringEncode = ct => Task.Delay(Timeout.Infinite, ct);
            h.Client.InitError = new ClipUploadException("full") { Code = "pool_full", RetryAt = 1_760_403_600 };
            await StartShareAsync(h);
            var error = await WaitPhaseAsync(tap, "error");

            Assert.Equal(("pool_full", 1_760_403_600L),
                (error.GetProperty("errorCode").GetString(), error.GetProperty("retryAt").GetInt64()));
            Assert.Equal(0, Volatile.Read(ref h.Encoder.Running));
            Assert.Empty(h.Client.Uploads);
            Assert.Empty(Directory.EnumerateFiles(h.TempDir));
        }
    }

    [Fact]
    public async Task AMarkersFailure_KeepsTheShare()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Client.MarkerError = _ => new ClipUploadException("offline", retryable: true) { Code = "network" };
            await StartShareAsync(h);
            var done = await WaitPhaseAsync(tap, "done");

            Assert.False(done.GetProperty("markersAttached").GetBoolean());
            // Three attempts right after init, three more after complete.
            Assert.Equal(6, h.Client.Markers.Count);
            Assert.False(h.Store.Get(GameId)!.MarkersAttached);
            Assert.Empty(h.Upload.Deleted);
        }
    }

    [Fact]
    public async Task AnOversized1080pCopy_IsPreparedAgainAt720p()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.SizeFor = q => q == VodShareQuality.P1080 ? 2_200_000_000 : 1_200_000_000;
            await StartShareAsync(h);
            var done = await WaitPhaseAsync(tap, "done");

            Assert.Equal(new[] { VodShareQuality.P1080, VodShareQuality.P720 }, h.Encoder.Encodes.Select(e => e.Quality));
            Assert.Equal(("720p", "size"), (done.GetProperty("quality").GetString(), done.GetProperty("qualityReason").GetString()));
            Assert.Equal("720p", h.Client.Uploads.Single().Request.Quality);
            Assert.Equal("720p", h.Store.Get(GameId)!.Quality);
        }
    }

    [Fact]
    public async Task AnOversized720pCopy_IsTooLarge()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.SizeFor = _ => 2_200_000_000;
            await StartShareAsync(h, quality: "720p");
            var error = await WaitPhaseAsync(tap, "error");
            Assert.Equal("too_large", error.GetProperty("errorCode").GetString());
            Assert.Empty(h.Client.Uploads);
            Assert.Empty(Directory.EnumerateFiles(h.TempDir));
        }
    }

    [Fact]
    public async Task AnEncodeFailure_IsEncodeFailed()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.Error = new VodEncodeException("encode_failed", "nope");
            await StartShareAsync(h);
            var error = await WaitPhaseAsync(tap, "error");
            Assert.Equal(("error", "encode_failed"), (error.GetProperty("state").GetString(), error.GetProperty("errorCode").GetString()));
            Assert.Equal("Revu couldn't prepare the video. Try again.", error.GetProperty("error").GetString());
            Assert.Null(h.Store.Get(GameId));
            // A share that fails is not one whose link changed: no new link is coming.
            Assert.DoesNotContain(tap.Seen, e => e.Type == "vodShareProgress" && e.Payload.GetProperty("linkChanged").GetBoolean());
            Assert.Equal(JsonValueKind.Null, error.GetProperty("url").ValueKind);
        }
    }

    [Theory]
    [InlineData("pool_full", "pool_full", false)]
    [InlineData("unauthorized", "needs_login", true)]
    [InlineData("too_many_pending_uploads", "busy", false)]
    [InlineData("vod_sharing_disabled", "sharing_paused", false)]
    [InlineData("upload_failed", "upload_failed", false)]
    [InlineData("network", "network", false)]
    [InlineData("payload_too_large", "too_large", false)]
    public async Task UploadRefusals_MapToTheSharedCodes(string proxyCode, string code, bool needsLogin)
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Client.OnUpload = (_, _, _, _, _) => Task.FromException<VodUploadResult>(
                new ClipUploadException("refused", unauthorized: proxyCode == "unauthorized") { Code = proxyCode, RetryAt = 1_760_403_600 });
            await StartShareAsync(h);
            var error = await WaitPhaseAsync(tap, "error");
            Assert.Equal((code, needsLogin), (error.GetProperty("errorCode").GetString(), error.GetProperty("needsLogin").GetBoolean()));
            Assert.Equal(1_760_403_600, error.GetProperty("retryAt").GetInt64());
            Assert.Empty(Directory.EnumerateFiles(h.TempDir));
        }
    }

    [Fact]
    public async Task Cancel_MidUpload_PublishesCancelled_AndLeavesTheIdForCleanup()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Client.InitStarted = FakeVodClient.Started("mid1234");
            h.Client.OnUpload = async (_, _, progress, _, ct) =>
            {
                progress?.Report(new ClipUploadProgress("uploading", 10, 100));
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            };
            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "uploading");
            await WaitUntilAsync(() => h.Cleanup.Snapshot().Contains("mid1234"));

            Assert.True(h.Worker.Cancel(GameId));
            var cancelled = await WaitPhaseAsync(tap, "cancelled");
            Assert.Equal("cancelled", cancelled.GetProperty("state").GetString());
            Assert.Contains("mid1234", h.Cleanup.Snapshot());
            Assert.Null(h.Store.Get(GameId));
            Assert.Empty(Directory.EnumerateFiles(h.TempDir));
            Assert.False(h.Worker.Cancel(GameId));
        }
    }

    [Fact]
    public async Task CancelAfterComplete_DeletesTheRemoteCopy()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Client.InitStarted = FakeVodClient.Started("late123");
            h.Client.OnUpload = (_, _, _, _, _) =>
            {
                h.Worker.Cancel(GameId);
                return Task.FromResult(new VodUploadResult("late123", "https://revu.lol/v/late123", FakeVodClient.Now + 259_200));
            };
            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "cancelled");

            Assert.Contains("late123", h.Upload.Deleted);
            Assert.Empty(h.Cleanup.Snapshot());
            Assert.Null(h.Store.Get(GameId));
        }
    }

    [Fact]
    public async Task AGameStarting_StopsTheShare()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.DuringEncode = ct => Task.Delay(Timeout.Infinite, ct);
            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "preparing");

            h.Live.SetGameInProgress(true);
            var error = await WaitPhaseAsync(tap, "error");
            Assert.Equal("game_started", error.GetProperty("errorCode").GetString());
            Assert.Empty(h.Client.Uploads);
            Assert.Empty(Directory.EnumerateFiles(h.TempDir));
        }
    }

    [Fact]
    public async Task OneJobAtATime_AnotherGameIsBusy_TheSameGameIsAlreadyRunning()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.DuringEncode = ct => Task.Delay(Timeout.Infinite, ct);
            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "preparing");

            Assert.True((await StartShareAsync(h)).GetProperty("alreadyRunning").GetBoolean());
            var other = Body(await h.Worker.StartAsync(GameId + 1, "1080p", false, CancellationToken.None));
            Assert.Equal(("busy", GameId), (Code(other), other.GetProperty("busyGameId").GetInt64()));
            Assert.Equal(GameId, (await h.Worker.GetStatusAsync(GameId + 1, CancellationToken.None)).BusyGameId);
            VodShareStatusDto mine = null!;
            await WaitUntilAsync(() =>
            {
                mine = h.Worker.GetStatusAsync(GameId, CancellationToken.None).GetAwaiter().GetResult();
                return mine.Job?.Percent == 34;
            });
            Assert.Equal(("running", "preparing", 34), (mine.Job!.State, mine.Job.Phase, mine.Job.Percent));
            Assert.Null(mine.BusyGameId);
            h.Worker.Cancel(GameId);
            await WaitPhaseAsync(tap, "cancelled");
        }
    }

    /// <summary>Until the fake encode is inside DuringEncode (it reports 34% first).</summary>
    private static Task WaitEncodingAsync(VodShareHarness h) =>
        WaitUntilAsync(() => h.Worker.GetStatusAsync(GameId, CancellationToken.None).GetAwaiter().GetResult().Job?.Percent == 34);

    /// <summary>An encode that takes <paramref name="lingerMs"/> to stop after it is cancelled.</summary>
    private static Func<CancellationToken, Task> SlowToStop(int lingerMs) => async ct =>
    {
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException)
        {
            await Task.Delay(lingerMs, CancellationToken.None);
            throw;
        }
    };

    [Fact]
    public async Task ReShareRightAfterCancel_WaitsForTheOldJob_ThenStartsANewOne()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.DuringEncode = SlowToStop(300);
            await StartShareAsync(h);
            await WaitEncodingAsync(h);
            Assert.True(h.Worker.Cancel(GameId));

            // Switching to 720p while the cancelled job is still cleaning up.
            var again = await StartShareAsync(h, "720p");
            Assert.True(again.GetProperty("accepted").GetBoolean());
            Assert.False(again.TryGetProperty("alreadyRunning", out _));
            Assert.Equal("720p", again.GetProperty("quality").GetString());
            await WaitUntilAsync(() =>
                h.Worker.GetStatusAsync(GameId, CancellationToken.None).GetAwaiter().GetResult().Job?.Quality == "720p");
            h.Worker.Cancel(GameId);
            await WaitUntilAsync(() =>
                h.Worker.GetStatusAsync(GameId, CancellationToken.None).GetAwaiter().GetResult().Job?.State == "cancelled");
        }
    }

    [Fact]
    public async Task ReShareWhileACancelledJobIsSlowToStop_IsRefusedAsStopping()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Worker.CancelledJobWait = TimeSpan.FromMilliseconds(50);
            h.Encoder.DuringEncode = SlowToStop(2000);
            await StartShareAsync(h);
            await WaitEncodingAsync(h);
            Assert.True(h.Worker.Cancel(GameId));

            var refused = await StartShareAsync(h);
            Assert.Equal("stopping", Code(refused));
            Assert.False(refused.GetProperty("ok").GetBoolean());
            await WaitPhaseAsync(tap, "cancelled");
        }
    }
}
