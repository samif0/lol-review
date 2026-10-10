using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The background share queue (C6 6.2) with the upload client faked: SSE phases, the stored
/// link, the narration-changed guard, cancellation and error flags.
/// </summary>
public sealed class ClipShareWorkerTests
{
    private static async Task<(NarrationHarness H, EventTap Tap, long Bm, string Clip)> StartAsync(bool signedIn = true)
    {
        var h = new NarrationHarness();
        await h.InitAsync();
        if (signedIn) h.SignIn();
        var (bm, clip) = await h.ClipAsync();
        var tap = new EventTap(h.Hub);
        h.Share.Start();
        return (h, tap, bm, clip);
    }

    private static async Task<string> NarrateAsync(NarrationHarness h, long bm, string clip, string name = "render.mp4",
        string transcriptJson = "")
    {
        var narrated = Path.Combine(h.ClipsDir, "narrated", name);
        Directory.CreateDirectory(Path.GetDirectoryName(narrated)!);
        await File.WriteAllTextAsync(narrated, "narrated render");
        await h.Narrations.UpsertAsync(new ClipNarrationRecord(bm, NarrationHarness.GameId, Guid.NewGuid().ToString("D"),
            Path.Combine(h.NarrationDir, "v.webm"), narrated, clip, 0, 30_000, 0.8, 1, true, TranscriptStatuses.Pending,
            0, "", "", "", "", 0, 0));
        if (transcriptJson.Length > 0)
        {
            var gen = (await h.Narrations.GetAsync(bm))!.TranscriptGeneration;
            await h.Narrations.TryClaimTranscriptAsync(bm, gen);
            await h.Narrations.TrySetTranscriptAsync(bm, gen, TranscriptStatuses.Ready, "en", transcriptJson, "");
        }
        return narrated;
    }

    [Fact]
    public async Task Share_PublishesQueuedPreparingUploadingAndDone_AndStoresTheLink()
    {
        var (h, tap, bm, clip) = await StartAsync();
        using (h)
        using (tap)
        {
            var pinnedDuringUpload = false;
            h.Upload.OnUpload = async (file, progress, assign, _) =>
            {
                pinnedDuringUpload = (await h.Retention.GetProtectedPathsAsync()).Contains(file);
                assign?.Invoke("abc1234");
                progress?.Report(new ClipUploadProgress("uploading", 5, 10));
                progress?.Report(new ClipUploadProgress("finishing", 10, 10));
                return new ClipUploadResult("abc1234", "https://revu.lol/abc1234", 99);
            };

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "my clip", narrated: false);

            await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "queued");
            await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "preparing");
            await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "uploading");
            await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "finishing");
            var done = await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "done");

            Assert.Equal("https://revu.lol/abc1234", done.GetProperty("url").GetString());
            Assert.Equal(bm, done.GetProperty("bookmarkId").GetInt64());
            Assert.False(done.GetProperty("narrated").GetBoolean());
            Assert.False(done.GetProperty("transcriptAttached").GetBoolean());
            Assert.True(pinnedDuringUpload);
            Assert.Equal(clip, h.Upload.Uploads.Single().File);
            Assert.Equal(30, h.Upload.Uploads.Single().Duration);
            Assert.Equal("https://revu.lol/abc1234", (await h.Scope.Vod.GetBookmarksAsync(NarrationHarness.GameId)).Single().ShareUrl);
            Assert.Empty(h.Cleanup.Snapshot());
            var status = h.Share.GetStatus(bm);
            Assert.Equal(("done", "https://revu.lol/abc1234"), (status.State, status.Url));
            Assert.False(h.Share.IsActive(bm));
        }
    }

    [Fact]
    public async Task NarratedShare_UploadsTheRender_AndAttachesAReadyTranscript()
    {
        var (h, tap, bm, clip) = await StartAsync();
        using (h)
        using (tap)
        {
            var doc = TranscriptDocument.Normalize(new[] { new TranscriptSegment(1, 2, "push now") }, "en", 30);
            var narrated = await NarrateAsync(h, bm, clip, transcriptJson: doc.ToJson());

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: true);
            await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "transcript");
            var done = await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "done");

            Assert.True(done.GetProperty("narrated").GetBoolean());
            Assert.True(done.GetProperty("transcriptAttached").GetBoolean());
            Assert.Equal((narrated, true), (h.Upload.Uploads.Single().File, h.Upload.Uploads.Single().Narrated));
            Assert.Equal("abc1234", h.Upload.Transcripts.Single().Slug);
            Assert.Equal("push now", h.Upload.Transcripts.Single().Doc.Segments.Single().Text);
            Assert.Equal("abc1234", (await h.Narrations.GetAsync(bm))!.TranscriptPushedSlug);
        }
    }

    [Fact]
    public async Task NarrationChangedMidUpload_DeletesTheRemoteCopy_AndFails()
    {
        var (h, tap, bm, clip) = await StartAsync();
        using (h)
        using (tap)
        {
            await NarrateAsync(h, bm, clip, "first.mp4");
            h.Upload.OnUpload = async (_, _, assign, _) =>
            {
                assign?.Invoke("stale99");
                await NarrateAsync(h, bm, clip, "second.mp4");   // the user re-recorded
                return new ClipUploadResult("stale99", "https://revu.lol/stale99", 99);
            };

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: true);
            var error = await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "error");

            Assert.Equal("Narration changed. Share again to update the link.", error.GetProperty("error").GetString());
            Assert.False(error.GetProperty("retryable").GetBoolean());
            Assert.Equal(new[] { "stale99" }, h.Upload.Deleted);
            Assert.Empty(h.Cleanup.Snapshot());
            Assert.Equal("", (await h.Scope.Vod.GetBookmarksAsync(NarrationHarness.GameId)).Single().ShareUrl);
        }
    }

    [Fact]
    public async Task Cancel_StopsARunningJob_WithAnEmptyNonRetryableError()
    {
        var (h, tap, bm, _) = await StartAsync();
        using (h)
        using (tap)
        {
            var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Upload.OnUpload = async (_, _, _, ct) =>
            {
                uploading.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            };

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            await uploading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Share.Cancel(bm);
            var error = await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "error");

            Assert.Equal("", error.GetProperty("error").GetString());
            Assert.False(error.GetProperty("retryable").GetBoolean());
            Assert.Equal("error", h.Share.GetStatus(bm).State);
            Assert.Equal("", (await h.Scope.Vod.GetBookmarksAsync(NarrationHarness.GameId)).Single().ShareUrl);

            // The queue moves on: a fresh share of the same clip runs.
            h.Upload.OnUpload = (_, _, _, _) => Task.FromResult(new ClipUploadResult("new1", "https://revu.lol/new1", 1));
            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "done");
        }
    }

    [Fact]
    public async Task CancelThenShareAgain_TheOldJobsLateCancellationNeverOverridesTheNewShare()
    {
        var (h, tap, bm, _) = await StartAsync();
        using (h)
        using (tap)
        {
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            h.Upload.OnUpload = async (_, _, _, ct) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    // A slow upload that only notices the cancellation once released.
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                    ct.ThrowIfCancellationRequested();
                }
                return new ClipUploadResult("new2", "https://revu.lol/new2", 1);
            };

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Share.Cancel(bm);
            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            Assert.Equal("queued", h.Share.GetStatus(bm).State);

            releaseFirst.SetResult();
            var done = await tap.WaitForAsync("clipShareProgress",
                e => e.GetProperty("phase").GetString() == "done" && e.GetProperty("bookmarkId").GetInt64() == bm);

            Assert.Equal("https://revu.lol/new2", done.GetProperty("url").GetString());
            Assert.DoesNotContain(tap.Seen, s => s.Type == "clipShareProgress"
                && s.Payload.GetProperty("phase").GetString() == "error");
            var status = h.Share.GetStatus(bm);
            Assert.Equal(("done", "https://revu.lol/new2"), (status.State, status.Url));
            Assert.Equal(2, calls);
        }
    }

    [Fact]
    public async Task CancelWhileQueued_ReportsTheErrorAtOnce_AndTheJobNeverRuns()
    {
        var (h, tap, bm, _) = await StartAsync();
        using (h)
        using (tap)
        {
            var (other, _) = await h.ClipAsync(200, 230);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Upload.OnUpload = async (file, _, _, _) =>
            {
                await release.Task;
                return new ClipUploadResult("x" + Path.GetFileNameWithoutExtension(file).Length, "https://revu.lol/x", 1);
            };

            h.Share.Enqueue(NarrationHarness.GameId, other, "Ahri", "", narrated: false);   // occupies the queue
            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            h.Share.Cancel(bm);
            var error = await tap.WaitForAsync("clipShareProgress",
                e => e.GetProperty("phase").GetString() == "error" && e.GetProperty("bookmarkId").GetInt64() == bm);
            release.SetResult();
            await tap.WaitForAsync("clipShareProgress",
                e => e.GetProperty("phase").GetString() == "done" && e.GetProperty("bookmarkId").GetInt64() == other);

            Assert.Equal("", error.GetProperty("error").GetString());
            Assert.Single(h.Upload.Uploads);
        }
    }

    [Fact]
    public async Task CancelWithTheNarrationReason_FailsARunningJob_WithTheNarrationChangedMessage()
    {
        var (h, tap, bm, _) = await StartAsync();
        using (h)
        using (tap)
        {
            var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Upload.OnUpload = async (_, _, _, ct) =>
            {
                uploading.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            };

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            await uploading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            h.Share.Cancel(bm, ClipShareWorker.NarrationChangedMessage);
            var error = await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "error");

            Assert.Equal("Narration changed. Share again to update the link.", error.GetProperty("error").GetString());
            Assert.False(error.GetProperty("retryable").GetBoolean());
            Assert.False(error.GetProperty("needsLogin").GetBoolean());
            var status = h.Share.GetStatus(bm);
            Assert.Equal(("error", "Narration changed. Share again to update the link."), (status.State, status.Error));
            Assert.Equal("", (await h.Scope.Vod.GetBookmarksAsync(NarrationHarness.GameId)).Single().ShareUrl);
        }
    }

    [Fact]
    public async Task CancelWhileQueued_WithTheNarrationReason_ReportsTheNarrationChangedMessage()
    {
        var (h, tap, bm, _) = await StartAsync();
        using (h)
        using (tap)
        {
            var (other, _) = await h.ClipAsync(200, 230);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Upload.OnUpload = async (_, _, _, _) =>
            {
                await release.Task;
                return new ClipUploadResult("q1", "https://revu.lol/q1", 1);
            };

            h.Share.Enqueue(NarrationHarness.GameId, other, "Ahri", "", narrated: false);   // occupies the queue
            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            h.Share.Cancel(bm, ClipShareWorker.NarrationChangedMessage);
            var error = await tap.WaitForAsync("clipShareProgress",
                e => e.GetProperty("phase").GetString() == "error" && e.GetProperty("bookmarkId").GetInt64() == bm);
            release.SetResult();
            await tap.WaitForAsync("clipShareProgress",
                e => e.GetProperty("phase").GetString() == "done" && e.GetProperty("bookmarkId").GetInt64() == other);

            Assert.Equal("Narration changed. Share again to update the link.", error.GetProperty("error").GetString());
            Assert.Single(h.Upload.Uploads);
        }
    }

    [Fact]
    public async Task NarrationChangeDuringARunningShare_FailsItWithTheNarrationChangedMessage()
    {
        var (h, tap, bm, _) = await StartAsync();
        using (h)
        using (tap)
        {
            var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Upload.OnUpload = async (_, _, assign, ct) =>
            {
                assign?.Invoke("mid42");
                uploading.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            };

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            await uploading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var bookmark = (await h.Scope.Vod.GetBookmarksAsync(NarrationHarness.GameId)).Single();
            Assert.False(await h.Commands().ClearShareForNarrationChangeAsync(bookmark));   // no link stored yet
            var error = await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "error");

            Assert.Equal("Narration changed. Share again to update the link.", error.GetProperty("error").GetString());
            Assert.False(error.GetProperty("retryable").GetBoolean());
            Assert.Equal("", (await h.Scope.Vod.GetBookmarksAsync(NarrationHarness.GameId)).Single().ShareUrl);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ErrorEvents_CarryRetryableAndNeedsLogin(bool retryable, bool unauthorized)
    {
        var (h, tap, bm, _) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Upload.OnUpload = (_, _, _, _) =>
                throw new ClipUploadException("Sharing is temporarily unavailable. Try again in a moment.", unauthorized, retryable);

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            var error = await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "error");

            Assert.Equal(retryable, error.GetProperty("retryable").GetBoolean());
            Assert.Equal(unauthorized, error.GetProperty("needsLogin").GetBoolean());
            var status = h.Share.GetStatus(bm);
            Assert.Equal((retryable, unauthorized), (status.Retryable, status.NeedsLogin));
        }
    }

    [Fact]
    public async Task SignedOut_FailsWithNeedsLogin()
    {
        var (h, tap, bm, _) = await StartAsync(signedIn: false);
        using (h)
        using (tap)
        {
            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            var error = await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "error");
            Assert.True(error.GetProperty("needsLogin").GetBoolean());
            Assert.Empty(h.Upload.Uploads);
        }
    }

    [Fact]
    public async Task ClipDeletedBeforeTheLinkIsStored_DropsTheRemoteCopy_AndStoresNothing()
    {
        var (h, tap, bm, _) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Upload.OnUpload = async (_, _, assign, _) =>
            {
                assign?.Invoke("late123");
                // /api/clip/delete ran while the upload was finishing: it read an empty link.
                await h.Scope.Vod.DeleteClipFullAsync(bm);
                return new ClipUploadResult("late123", "https://revu.lol/late123", 99);
            };

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            var error = await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "error");

            Assert.False(error.GetProperty("retryable").GetBoolean());
            Assert.Equal(new[] { "late123" }, h.Upload.Deleted);
            Assert.Empty(h.Cleanup.Snapshot());
            Assert.Empty(await h.Scope.Vod.GetBookmarksAsync(NarrationHarness.GameId));
        }
    }

    [Fact]
    public async Task ClipDeletedBeforeTheLinkIsStored_AndTheRemoteDeleteFails_KeepsTheIdQueued()
    {
        var (h, tap, bm, _) = await StartAsync();
        using (h)
        using (tap)
        {
            h.Upload.DeleteResult = false;
            h.Upload.OnUpload = async (_, _, assign, _) =>
            {
                assign?.Invoke("late456");
                await h.Scope.Vod.DeleteClipFullAsync(bm);
                return new ClipUploadResult("late456", "https://revu.lol/late456", 99);
            };

            h.Share.Enqueue(NarrationHarness.GameId, bm, "Ahri", "", narrated: false);
            await tap.WaitForAsync("clipShareProgress", e => e.GetProperty("phase").GetString() == "error");

            Assert.Equal(new[] { "late456" }, h.Cleanup.Snapshot());
        }
    }
}
