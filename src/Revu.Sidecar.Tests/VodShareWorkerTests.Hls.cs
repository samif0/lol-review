using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// 4.4 HLS shares in the worker (plan 4.2, 7.1): when HLS is used, the link and markers at init, an upload that ends in
/// full, a game start that pauses a watchable share (and cancels one nobody could watch yet), Revu closing, Cancel,
/// the 720p switch (over the reservation and for a slow uplink, once), an older proxy's MP4 row, and the new event fields.
/// </summary>
public sealed partial class VodShareWorkerTests
{
    private static readonly VodShareHlsInfo HlsOn = new(true, 2, 6, 16_777_216, 16_384, 8000, 8);
    /// <summary>The harness game (2,255.4 s) is 1,128 segments of 2 s.</summary>
    private const int AllSegments = 1128;

    private static async Task<(VodShareHarness H, EventTap Tap)> StartHlsAsync()
    {
        var (h, tap) = await StartAsync();
        h.Client.Status = () => FakeVodClient.Allowed() with { Hls = HlsOn };
        return (h, tap);
    }

    /// <summary>Call <paramref name="index"/> of the encode announces <paramref name="segments"/> segments then waits to be stopped.</summary>
    private static Func<int, VodHlsEncodeRequest, System.Threading.Channels.ChannelWriter<HlsItem>, CancellationToken, Task<VodHlsEncodeResult>>
        HangAfter(params int[] segments) => async (index, request, writer, ct) =>
        {
            if (index < segments.Length)
            {
                await FakeVodEncoder.WriteSegmentsAsync(request, writer, segments[index], ct);
                await Task.Delay(Timeout.Infinite, ct);
            }
            var rest = (int)Math.Ceiling((request.DurationSeconds - request.ResumeAtSeconds) / 2);
            await FakeVodEncoder.WriteSegmentsAsync(request, writer, rest, ct);
            return new VodHlsEncodeResult(true, request.NextRun + 1, request.NextSeq + rest, request.DurationSeconds, "h264_nvenc", null);
        };

    private static Task<JsonElement> WaitWatchableAsync(EventTap tap) =>
        tap.WaitForAsync("vodShareProgress", e => e.GetProperty("watchable").GetBoolean());

    [Fact]
    public async Task Hls_IsUsedOnlyWhenTheProxyOffersIt_TheLengthWasProbed_AndThisPcAllowsIt()
    {
        async Task<bool> SharedAsHlsAsync(Action<VodShareHarness> setup)
        {
            var (h, tap) = await StartHlsAsync();
            using (h)
            using (tap)
            {
                setup(h);
                await StartShareAsync(h);
                await WaitPhaseAsync(tap, "done");
                return h.Client.HlsStarts.Count == 1 && h.Client.WhileWriting.Count == 0;
            }
        }

        Assert.True(await SharedAsHlsAsync(_ => { }));
        Assert.False(await SharedAsHlsAsync(h => h.Client.Status = () => FakeVodClient.Allowed()));
        Assert.False(await SharedAsHlsAsync(h => h.Client.Status = () => FakeVodClient.Allowed() with { Hls = HlsOn with { Enabled = false } }));
        Assert.False(await SharedAsHlsAsync(h => h.Worker.EnvironmentVariable = name => name == "REVU_VOD_SHARE_HLS" ? "0" : null));
        // The game's own length (no probe) is not exact enough to reserve segment slots.
        Assert.False(await SharedAsHlsAsync(h => h.Mixer.ProbeDuration = 0));
    }

    [Fact]
    public async Task AnHlsShare_ShowsItsLinkAtInit_UploadsWhileItEncodes_AndEndsInFull()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            await StartShareAsync(h);
            var early = await tap.WaitForAsync("vodShareProgress", e => e.GetProperty("url").GetString() == "https://revu.lol/v/hls1234");
            Assert.Equal(("hls", "uploading"), (early.GetProperty("format").GetString(), early.GetProperty("phase").GetString()));
            var watchable = await WaitWatchableAsync(tap);
            Assert.True(watchable.GetProperty("mediaSeconds").GetDouble() >= 8);
            Assert.Equal(2255.4, watchable.GetProperty("durationSeconds").GetDouble());
            var done = await WaitPhaseAsync(tap, "done");

            Assert.DoesNotContain(tap.Seen, e => e.Type == "vodShareProgress" && e.Payload.GetProperty("phase").GetString() == "preparing");
            Assert.Equal(("https://revu.lol/v/hls1234", false, "hls"), (done.GetProperty("url").GetString(),
                done.GetProperty("truncated").GetBoolean(), done.GetProperty("format").GetString()));
            var (request, declared) = Assert.Single(h.Client.HlsStarts);
            Assert.Equal(new VodUploadRequest("1080p", 2256, 30), request);
            Assert.Equal(VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 30, 2255.4), declared);
            var encode = Assert.Single(h.Encoder.HlsEncodes);
            Assert.Equal((0, 0, 0.0, VodShareQuality.P1080, 8), (encode.NextRun, encode.NextSeq, encode.ResumeAtSeconds, encode.Quality, encode.MaxRuns));
            Assert.Contains($"end:{AllSegments}:false", h.Client.Hls.Calls);
            Assert.DoesNotContain("delete", h.Client.Hls.Calls);

            var entry = h.Store.Get(GameId)!;
            Assert.Equal(("hls1234", AllSegments * 1_250_000L, true), (entry.Id, entry.SizeBytes, entry.MarkersAttached));
            Assert.Empty(h.Cleanup.Snapshot());
            Assert.Empty(Directory.EnumerateFileSystemEntries(h.TempDir));
            Assert.Contains(h.Client.Markers, m => m.Id == "hls1234");
        }
    }

    [Fact]
    public async Task AWatchableShare_PausesForAGame_AndContinuesAtTheEdgeAsANewRun()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Encoder.OnHlsEncode = HangAfter(10);
            await StartShareAsync(h);
            await WaitWatchableAsync(tap);
            await WaitUntilAsync(() => h.Client.Hls.State.Acked == 10);

            h.Live.SetGameInProgress(true);
            await tap.WaitForAsync("vodShareProgress", e => e.GetProperty("paused").GetBoolean());
            await WaitUntilAsync(() => h.Client.Hls.Saw("pause:game"));
            Assert.Contains("stop", h.Client.Hls.Calls);
            Assert.Single(h.Encoder.HlsEncodes);

            h.Live.SetGameInProgress(false);
            var done = await WaitPhaseAsync(tap, "done");
            Assert.False(done.GetProperty("paused").GetBoolean());
            Assert.Equal(2, h.Encoder.HlsEncodes.Count);
            var resumed = h.Encoder.HlsEncodes[1];
            Assert.Equal((1, 10, 20.0), (resumed.NextRun, resumed.NextSeq, resumed.ResumeAtSeconds));
            Assert.Contains("reset", h.Client.Hls.Calls);
            Assert.Contains($"end:{AllSegments}:false", h.Client.Hls.Calls);
        }
    }

    [Fact]
    public async Task AGameBeforeAnythingIsWatchable_CancelsTheShareAndDeletesIt()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Client.Hls.PlayableAfter = 1000;
            h.Encoder.OnHlsEncode = HangAfter(2);
            await StartShareAsync(h);
            await tap.WaitForAsync("vodShareProgress", e => e.GetProperty("url").GetString() == "https://revu.lol/v/hls1234");
            await WaitUntilAsync(() => h.Client.Hls.State.Acked == 2);

            h.Live.SetGameInProgress(true);
            var error = await WaitPhaseAsync(tap, "error");
            Assert.Equal("game_started", error.GetProperty("errorCode").GetString());
            Assert.Contains("delete", h.Client.Hls.Calls);
            Assert.DoesNotContain("pause:game", h.Client.Hls.Calls);
            Assert.Null(h.Store.Get(GameId));
        }
    }

    [Fact]
    public async Task ClosingRevu_EndsAWatchableShareWhereItsUploadStopped_AndKeepsTheLink()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Encoder.OnHlsEncode = HangAfter(10);
            await StartShareAsync(h);
            await WaitWatchableAsync(tap);
            await WaitUntilAsync(() => h.Client.Hls.State.Acked == 10);

            await h.Work.StopAsync(CancellationToken.None);
            await WaitUntilAsync(() => h.Client.Hls.Saw("end:10:true"));
            Assert.DoesNotContain("delete", h.Client.Hls.Calls);
            Assert.Equal("hls1234", h.Store.Get(GameId)!.Id);
            Assert.DoesNotContain("hls1234", h.Cleanup.Snapshot());
        }
    }

    [Fact]
    public async Task ClosingRevu_DeletesAShareNobodyCouldWatchYet()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Client.Hls.PlayableAfter = 1000;
            h.Encoder.OnHlsEncode = HangAfter(2);
            await StartShareAsync(h);
            await WaitUntilAsync(() => h.Client.Hls.State.Acked == 2);
            await h.Work.StopAsync(CancellationToken.None);
            await WaitUntilAsync(() => h.Client.Hls.Saw("delete"));
            Assert.DoesNotContain(h.Client.Hls.Calls, c => c.StartsWith("end:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Cancel_DeletesEvenAWatchableShare()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Encoder.OnHlsEncode = HangAfter(10);
            await StartShareAsync(h);
            await WaitWatchableAsync(tap);
            Assert.NotNull(h.Store.Get(GameId));    // watchable: stored, so Stop sharing works after a crash too

            Assert.True(h.Worker.Cancel(GameId));
            await WaitPhaseAsync(tap, "cancelled");
            Assert.Contains("delete", h.Client.Hls.Calls);
            Assert.Null(h.Store.Get(GameId));
        }
    }

    [Fact]
    public async Task OverTheReservation_SwitchesTo720pOnceAtTheEdge_ThenEndsThere()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            var refusals = 0;
            h.Client.Hls.FailAt = seq =>
            {
                if (!(seq == 12 && refusals == 0) && !(seq == 16 && refusals == 1)) return null;
                refusals++;
                return new VodHlsUploadException(VodHlsFailure.TooLarge, "over_reservation", "413");
            };
            h.Encoder.OnHlsEncode = HangAfter(15, 5);
            await StartShareAsync(h);
            var done = await WaitPhaseAsync(tap, "done");

            Assert.Contains("drop:12", h.Client.Hls.Calls);
            var switched = h.Encoder.HlsEncodes[1];
            Assert.Equal((VodShareQuality.P720, 12, 1, 24.0), (switched.Quality, switched.NextSeq, switched.NextRun, switched.ResumeAtSeconds));
            Assert.Equal(VodShareQuality.P720, h.Client.Hls.Quality);
            Assert.Contains("end:16:true", h.Client.Hls.Calls);
            Assert.Equal(("720p", "uplink", true), (done.GetProperty("quality").GetString(),
                done.GetProperty("qualityReason").GetString(), done.GetProperty("truncated").GetBoolean()));
            Assert.Equal(2, h.Encoder.HlsEncodes.Count);
        }
    }

    [Fact]
    public async Task ASlowUplink_SwitchesTo720pOnce()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Client.Hls.Ingest = 0.8;
            h.Encoder.OnHlsEncode = HangAfter(10);
            await StartShareAsync(h);
            var slow = await tap.WaitForAsync("vodShareProgress", e => e.GetProperty("qualityReason").GetString() == "uplink");
            Assert.Equal("720p", slow.GetProperty("quality").GetString());
            var done = await WaitPhaseAsync(tap, "done");

            Assert.Single(h.Client.Hls.Calls, "drain");
            Assert.Equal(2, h.Encoder.HlsEncodes.Count);
            // The switch comes once 4 segments are in (the encode may have announced a few more by then).
            var switched = h.Encoder.HlsEncodes[1];
            Assert.Equal(VodShareQuality.P720, switched.Quality);
            Assert.InRange(switched.NextSeq, 4, 10);
            Assert.Equal(switched.NextSeq * 2.0, switched.ResumeAtSeconds);
            Assert.Contains($"drop:{switched.NextSeq}", h.Client.Hls.Calls);
            Assert.False(done.GetProperty("truncated").GetBoolean());
            // The slow upload is remembered: the next share of this account starts at 720p.
            Assert.Equal("720p", (await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).PreferredQuality);
        }
    }

    [Fact]
    public async Task AFastUplink_IsRemembered_AndPreselectsNothing()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Client.Hls.Ingest = 9;
            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "done");
            Assert.Null((await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).PreferredQuality);
            Assert.True(h.Memory.UplinkBytesPerSecond(await h.Worker.AccountKeyAsync()) > 1_000_000);
        }
    }

    [Fact]
    public async Task AShareStoppedElsewhere_EndsTheJobWithoutADelete()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Client.Hls.FailAt = seq => seq == 6 ? new VodHlsUploadException(VodHlsFailure.Stopped, "not_found", "404") : null;
            h.Encoder.OnHlsEncode = HangAfter(10);
            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "cancelled");
            Assert.Contains("abandon", h.Client.Hls.Calls);
            Assert.DoesNotContain("delete", h.Client.Hls.Calls);
            Assert.Null(h.Store.Get(GameId));
        }
    }

    [Fact]
    public async Task ARunConflict_FailsLoudly_ButAWatchableShareEndsAtTheEdge()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Client.Hls.FailAt = seq => seq == 8 ? new VodHlsUploadException(VodHlsFailure.Conflict, "run_mismatch", "409") : null;
            h.Encoder.OnHlsEncode = HangAfter(10);
            await StartShareAsync(h);
            var error = await WaitPhaseAsync(tap, "error");
            Assert.Equal("upload_failed", error.GetProperty("errorCode").GetString());
            Assert.True(error.GetProperty("watchable").GetBoolean());
            Assert.Contains("end:8:true", h.Client.Hls.Calls);
            Assert.True(h.Client.StatusCalls >= 2);   // the status was read again for the log
        }
    }

    [Fact]
    public async Task Markers_MoveByTheSourceStart_ForAnHlsShareOnly()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Mixer.ProbeStart = 4.956;
            h.Snapshot = VodShareMarkersTests.Vod([VodShareMarkersTests.Event(1, "DRAGON", 312, "Dragon", "gold")], videoStart: 12.5);
            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "done");
            using var doc = JsonDocument.Parse(h.Client.Markers.First(m => m.Id == "hls1234").Json);
            Assert.Equal(17.46, doc.RootElement.GetProperty("gameTimeAtVideoStart").GetDouble());
        }
    }

    [Fact]
    public async Task AnOlderProxysMp4Row_IsUploadedAsToday_WithoutASecondInit()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Client.OnHlsStart = (request, declared) => new VodHlsStart(FakeVodClient.Started("old1234"), null,
                new VodMp4Reservation("old1234", 16_777_216, (int)((declared + 16_777_215) / 16_777_216), declared,
                    FakeVodClient.Now + 259_200, "https://revu.lol/v/old1234"));
            await StartShareAsync(h);
            var done = await WaitPhaseAsync(tap, "done");

            Assert.Equal("https://revu.lol/v/old1234", done.GetProperty("url").GetString());
            Assert.Equal("mp4", done.GetProperty("format").GetString());
            Assert.Single(h.Client.Reserved);
            Assert.Empty(h.Client.WhileWriting);
            Assert.Single(h.Encoder.Encodes);
            Assert.Equal(VodShareEncoderArgs.ReserveBytes(VodShareQuality.P1080, 30, 2255.4), Assert.Single(h.Pads).Size);
            Assert.Equal("old1234", h.Store.Get(GameId)!.Id);
        }
    }

    [Fact]
    public async Task HlsTurnedOffBeforeTheInit_SharesTheFinishedCopyInstead()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Client.HlsStartError = new ClipUploadException("off") { Code = "hls_unavailable" };
            await StartShareAsync(h);
            var done = await WaitPhaseAsync(tap, "done");
            Assert.Equal("https://revu.lol/v/vod1234", done.GetProperty("url").GetString());
            Assert.Single(h.Client.WhileWriting);
        }
    }

    [Fact]
    public async Task OpeningTheSheet_PrewarmsTheHlsEncoder_UnlessAGameRuns()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            h.Live.SetGameInProgress(true);
            await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            await Task.Delay(100);
            Assert.Equal(0, Volatile.Read(ref h.Encoder.Prewarms));

            h.Live.SetGameInProgress(false);
            await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            await WaitUntilAsync(() => Volatile.Read(ref h.Encoder.Prewarms) == 1);
        }
    }

    [Fact]
    public async Task AStartRightAfterTheSheet_ReusesItsStatusRead()
    {
        var (h, tap) = await StartHlsAsync();
        using (h)
        using (tap)
        {
            await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            var reads = h.Client.StatusCalls;
            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "done");
            Assert.Equal(reads, h.Client.StatusCalls);
        }
    }

    [Fact]
    public void Startup_RemovesHlsJobFolders_ButNeverFollowsAJunction()
    {
        var root = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "sharetemp-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "outside-" + Guid.NewGuid().ToString("N"));
        try
        {
            var job = Path.Combine(root, "game-4321-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            File.WriteAllBytes(Path.Combine(job, "seg-00000.m4s"), [1]);
            File.WriteAllBytes(Path.Combine(job, "seg-00001.m4s"), [1]);
            var other = Path.Combine(root, "game-4321-notaguid");
            Directory.CreateDirectory(other);
            File.WriteAllBytes(Path.Combine(other, "keep.m4s"), [1]);
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "precious.txt"), [1]);
            var junction = Path.Combine(root, "game-1-" + Guid.NewGuid().ToString("N"));
            using (var mklink = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                       $"/c mklink /J \"{junction}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!)
                mklink.WaitForExit();
            Assert.True(Directory.Exists(junction));

            Assert.Equal(1, NarrationStartup.ClearVodShareTemp(root, NullLogger.Instance));
            Assert.False(Directory.Exists(job));
            Assert.True(File.Exists(Path.Combine(other, "keep.m4s")));
            Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(outside, recursive: true); } catch { }
        }
    }
}
