using System.Text.Json;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The markers document of a full-game share: it goes up right after the upload's init, so the
/// watch page names the game while it uploads, with the attach after complete as the fallback.
/// </summary>
public sealed partial class VodShareWorkerTests
{
    private static (string Id, bool Ready)[] MarkerPuts(VodShareHarness h)
    {
        lock (h.Client.Markers) return h.Client.Markers.Select(m => (m.Id, m.Ready)).ToArray();
    }

    [Fact]
    public async Task TheMarkersGoUpRightAfterInit_WhileTheGameIsStillEncoding()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Encoder.DuringEncode = ct => release.Task.WaitAsync(ct);
            await StartShareAsync(h);

            await WaitUntilAsync(() => MarkerPuts(h).Length == 1);
            Assert.Equal(("vod1234", false), MarkerPuts(h)[0]);
            Assert.Empty(h.Client.Uploads);
            using (var doc = JsonDocument.Parse(h.Client.Markers[0].Json))
            {
                Assert.Equal("Jinx", doc.RootElement.GetProperty("game").GetProperty("champion").GetString());
                Assert.Equal("DRAGON", doc.RootElement.GetProperty("markers")[0].GetProperty("kind").GetString());
            }

            release.SetResult();
            var done = await WaitPhaseAsync(tap, "done");
            // Nothing more goes up after complete.
            Assert.True(done.GetProperty("markersAttached").GetBoolean());
            Assert.Single(MarkerPuts(h));
            Assert.True(h.Store.Get(GameId)!.MarkersAttached);
        }
    }

    [Fact]
    public async Task AProxyThatTakesMarkersOnlyWhenReady_GetsThemAfterComplete()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Client.MarkersOnlyWhenReady = true;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Encoder.DuringEncode = ct => release.Task.WaitAsync(ct);
            await StartShareAsync(h);
            await WaitUntilAsync(() => MarkerPuts(h).Length == 1);
            release.SetResult();
            var done = await WaitPhaseAsync(tap, "done");

            // A 404 is not retried: the attach after complete is the one that works.
            Assert.Equal(new[] { ("vod1234", false), ("vod1234", true) }, MarkerPuts(h));
            Assert.True(done.GetProperty("markersAttached").GetBoolean());
            Assert.True(h.Store.Get(GameId)!.MarkersAttached);
        }
    }

    [Fact]
    public async Task AnUploadThatStartsOver_GetsTheMarkersAgainUnderItsNewId()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            var builds = 0;
            h.Worker.LoadSnapshotAsync = (_, _) =>
            {
                Interlocked.Increment(ref builds);
                return Task.FromResult(h.Snapshot);
            };
            h.Client.InitStarted = FakeVodClient.Started("first12");
            h.Encoder.SizeFor = _ => 1_396_160_263;   // one byte past the 1080p30 reservation
            // The given-up upload's attach never answers; it must not hold up the finished share.
            h.Client.DuringMarkers = (id, ct) => id == "first12" ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;
            h.Encoder.DuringEncode = _ => WaitUntilAsync(() => MarkerPuts(h).Length == 1);
            await StartShareAsync(h);
            var done = await WaitPhaseAsync(tap, "done");

            Assert.Equal(new[] { "first12", "vod1234" }, MarkerPuts(h).Select(p => p.Id));
            Assert.Equal(("https://revu.lol/v/vod1234", true, true), (done.GetProperty("url").GetString(),
                done.GetProperty("linkChanged").GetBoolean(), done.GetProperty("markersAttached").GetBoolean()));
            var entry = h.Store.Get(GameId)!;
            Assert.Equal(("vod1234", true), (entry.Id, entry.MarkersAttached));
            Assert.Equal(1, Volatile.Read(ref builds));
            // The job is over: another game can be shared.
            await WaitUntilAsync(() =>
                h.Worker.GetStatusAsync(GameId + 1, CancellationToken.None).GetAwaiter().GetResult().BusyGameId is null);
        }
    }

    [Fact]
    public async Task AnAttachStillRunningAtComplete_IsWaitedFor_AndRecordedOnlyOnceItLands()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Client.DuringMarkers = (_, ct) => hold.Task.WaitAsync(ct);
            await StartShareAsync(h);

            // The share is stored once complete; its markers are not confirmed yet, so it isn't done.
            await WaitUntilAsync(() => h.Store.Get(GameId) is not null && MarkerPuts(h).Length == 1);
            Assert.False(h.Store.Get(GameId)!.MarkersAttached);
            var job = (await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Job!;
            Assert.Equal(("running", "finishing"), (job.State, job.Phase));

            hold.SetResult();
            var done = await WaitPhaseAsync(tap, "done");
            Assert.True(done.GetProperty("markersAttached").GetBoolean());
            Assert.True(h.Store.Get(GameId)!.MarkersAttached);
            Assert.Single(MarkerPuts(h));
        }
    }
}
