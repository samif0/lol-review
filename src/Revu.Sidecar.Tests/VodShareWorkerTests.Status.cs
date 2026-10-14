using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>Stop and status of the full-game share worker (contract 7.5 to 7.7).</summary>
public sealed partial class VodShareWorkerTests
{
    // ── Stop ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_DeletesTheRemoteAndForgetsTheLink()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Store.Put(h.Entry("stop123"));
            var reply = await h.Worker.StopAsync(GameId, CancellationToken.None);
            Assert.Equal(new VodShareStopReply(true, true, false), reply);
            Assert.Contains("stop123", h.Upload.Deleted);
            Assert.Null(h.Store.Get(GameId));
            Assert.Equal(new VodShareStopReply(true, false, false), await h.Worker.StopAsync(GameId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Stop_SignedOut_QueuesTheDelete_AndKeepsTheLinkUntilItIsReallyGone()
    {
        var (h, tap) = await StartAsync(signedIn: false);
        using (h)
        using (tap)
        {
            h.Store.Put(h.Entry("queue12"));
            Assert.Equal(new VodShareStopReply(true, false, true), await h.Worker.StopAsync(GameId, CancellationToken.None));
            Assert.Contains("queue12", h.Cleanup.Snapshot());
            // The link still works for anyone who has it, so the page keeps showing it and Stop sharing.
            Assert.Equal("queue12", h.Store.Get(GameId)?.Id);
            Assert.Equal("queue12", (await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Share?.Id);

            // Signed back in, Stop sharing works and the queued delete is no longer needed.
            h.SignIn();
            Assert.Equal(new VodShareStopReply(true, true, false), await h.Worker.StopAsync(GameId, CancellationToken.None));
            Assert.Null(h.Store.Get(GameId));
            Assert.DoesNotContain("queue12", h.Cleanup.Snapshot());
        }
    }

    [Fact]
    public async Task Stop_ForAGameBeingDeleted_ForgetsTheLinkEvenWhenTheDeleteIsQueued()
    {
        var (h, tap) = await StartAsync(signedIn: false);
        using (h)
        using (tap)
        {
            h.Store.Put(h.Entry("gone123"));
            Assert.Equal(new VodShareStopReply(true, false, true),
                await h.Worker.StopAsync(GameId, CancellationToken.None, forgetIfQueued: true));
            Assert.Contains("gone123", h.Cleanup.Snapshot());
            Assert.Null(h.Store.Get(GameId));
        }
    }

    [Fact]
    public async Task Stop_CancelsARunningJobFirst()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Encoder.DuringEncode = ct => Task.Delay(Timeout.Infinite, ct);
            await StartShareAsync(h);
            await WaitPhaseAsync(tap, "preparing");
            var reply = await h.Worker.StopAsync(GameId, CancellationToken.None);
            Assert.Equal(new VodShareStopReply(true, false, false), reply);
            Assert.Null((await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Job);
        }
    }

    // ── Status ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_SignedOut_MakesNoProxyCall()
    {
        var (h, tap) = await StartAsync(signedIn: false);
        using (h)
        using (tap)
        {
            var status = await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            Assert.Equal((false, false, true), (status.SignedIn, status.Allowed, status.Reachable));
            Assert.Null(status.Pool);
            Assert.Null(status.Limits);
            Assert.Equal(new VodShareRecordingDto(true, true, 2256, false, 30), status.Recording);
            Assert.Equal(new VodShareEstimatesDto(1_355_495_400, 769_091_400), status.Estimates);
            Assert.Equal(259_200, status.TtlSeconds);
            Assert.Equal(0, h.Client.StatusCalls);
        }
    }

    [Fact]
    public async Task Status_Allowed_ReportsPoolLimits_AndReconcilesTheStore()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Store.Put(h.Entry("gone123"));
            h.Client.Status = () => FakeVodClient.Allowed(live: 1, startsToday: 2, freeBytes: 1_000_000_000,
                releases: [new VodSharePoolRelease(FakeVodClient.Now + 3600, 500_000_000, 500_000_000)]);

            var status = await h.Worker.GetStatusAsync(GameId, CancellationToken.None);

            Assert.True(status.SignedIn && status.Allowed && status.SharingOn && status.Reachable);
            Assert.Equal(new VodSharePoolDto(1_000_000_000, false, true, FakeVodClient.Now + 3600, null), status.Pool);
            Assert.Equal(new VodShareLimitsDto(1, 3, 0, 2, 6, FakeVodClient.Now + 86_400), status.Limits);
            Assert.Null(status.Share);
            Assert.Null(h.Store.Get(GameId));
        }
    }

    [Fact]
    public async Task Status_PoolFlags_PlanForTheSameMarginAsStart()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            // Room for the bare 1080p30 estimate (1,355,495,400) but not for it plus 3%.
            h.Client.Status = () => FakeVodClient.Allowed(freeBytes: 1_380_000_000);

            var status = await h.Worker.GetStatusAsync(GameId, CancellationToken.None);

            Assert.Equal(new VodShareEstimatesDto(1_355_495_400, 769_091_400), status.Estimates);
            Assert.Equal(new VodSharePoolDto(1_380_000_000, false, true, null, null), status.Pool);
        }
    }

    [Fact]
    public async Task Status_Unreachable_UsesTheCachedAllowedFlag()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            Assert.True((await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Allowed);
            h.Client.StatusError = new ClipUploadException("offline", retryable: true) { Code = "network" };
            var offline = await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            Assert.Equal((false, true, true), (offline.Reachable, offline.Allowed, offline.SignedIn));
            Assert.Null(offline.Pool);

            h.Client.StatusError = new ClipUploadException("expired", unauthorized: true) { Code = "unauthorized" };
            var expired = await h.Worker.GetStatusAsync(GameId, CancellationToken.None);
            Assert.Equal((false, false), (expired.SignedIn, expired.Allowed));
        }
    }

    [Fact]
    public async Task Status_AnImportedRecording_IsNotBuiltIn()
    {
        var (h, tap) = await StartAsync();
        using (h)
        using (tap)
        {
            var imported = Path.Combine(h.Root, "Videos", "game.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(imported)!);
            File.WriteAllBytes(imported, [1]);
            h.Recording = imported;
            Assert.False((await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Recording.BuiltIn);
            h.Recording = null;
            Assert.Equal(new VodShareRecordingDto(false, false, null, false),
                (await h.Worker.GetStatusAsync(GameId, CancellationToken.None)).Recording);
        }
    }

    [Fact]
    public void Messages_ArePlainCopy()
    {
        Assert.All(VodShareWorker.ErrorCodes, code =>
        {
            var text = VodShareWorker.Message(code);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain('—', text);
            Assert.DoesNotContain('–', text);
            Assert.EndsWith(".", text);
            Assert.True(char.IsUpper(text[0]));
        });
    }

    [Fact]
    public void Startup_ClearsLeftoverShareCopies_OnlyAtTheTopLevel()
    {
        var root = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "sharetemp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keep = Path.Combine(root, "nested", "keep.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.WriteAllBytes(keep, [1]);
            File.WriteAllBytes(Path.Combine(root, "game-1-abc.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(root, "game-1-abc.mp4.tmp"), [1]);

            Assert.Equal(2, NarrationStartup.ClearVodShareTemp(root, NullLogger.Instance));
            Assert.Empty(Directory.EnumerateFiles(root));
            Assert.True(File.Exists(keep));
            Assert.Equal(0, NarrationStartup.ClearVodShareTemp(Path.Combine(root, "missing"), NullLogger.Instance));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
