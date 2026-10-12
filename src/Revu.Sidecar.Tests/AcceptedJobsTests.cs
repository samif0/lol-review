using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Auto-clip (up to 12 ffmpeg runs) and the update download used to run on the request,
/// under a 30 s client deadline: the client gave up, killed the clip in progress or left
/// the download holding the lock, and the UI said "failed". They are accepted background
/// work now: giving up waiting never stops them, asking again joins them, and progress
/// plus completion go out over SSE.
/// </summary>
public sealed class AcceptedJobsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static SidecarBackgroundWork Work() => new(NullLogger<SidecarBackgroundWork>.Instance);

    // ── SidecarJobs ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ACallerThatStopsWaiting_LeavesTheJobRunning_AndASecondCallerJoinsIt()
    {
        var jobs = new SidecarJobs(Work());
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        Func<CancellationToken, Task<int>> run = _ => { Interlocked.Increment(ref runs); return release.Task; };

        var first = jobs.Start("job", "test job", run)!;
        using var gaveUp = new CancellationTokenSource();
        var waiting = first.WaitAsync(gaveUp.Token);
        gaveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var second = jobs.Start("job", "test job", run)!;
        Assert.Same(first, second);
        Assert.True(jobs.IsRunning("job"));
        release.SetResult(42);
        Assert.Equal(42, await second.WaitAsync(Wait));
        Assert.Equal(1, runs);

        // Once finished, the key is free for a new run.
        await WaitUntilAsync(() => !jobs.IsRunning("job"));
        Assert.Equal(7, await jobs.Start("job", "test job", _ => Task.FromResult(7))!.WaitAsync(Wait));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task AFailedJob_FaultsItsTask_AndFreesTheKey()
    {
        var jobs = new SidecarJobs(Work());
        var job = jobs.Start<int>("job", "test job", _ => throw new InvalidOperationException("boom"))!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.WaitAsync(Wait));
        await WaitUntilAsync(() => !jobs.IsRunning("job"));
    }

    [Fact]
    public async Task Shutdown_CancelsTheRunningJob_AndAdmitsNoNewOne()
    {
        var work = Work();
        var jobs = new SidecarJobs(work);
        var job = jobs.Start("job", "test job", async stopping => { await Task.Delay(Timeout.Infinite, stopping); return 1; })!;

        await work.StopAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.WaitAsync(Wait));
        Assert.Null(jobs.Start("job", "test job", _ => Task.FromResult(1)));
        Assert.False(work.ShutdownIncomplete);
    }

    // ── POST /api/clip/auto-objectives ────────────────────────────────────────

    private sealed class SlowAutoClip : IAutoClipService
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs;
        public CancellationToken Token;

        public async Task<AutoClipResult> ClipObjectiveEventsAsync(long gameId, long? objectiveId, CancellationToken ct = default,
            IProgress<AutoClipProgress>? progress = null)
        {
            Interlocked.Increment(ref Runs);
            Token = ct;
            progress?.Report(new AutoClipProgress(0, 3, 0, 0));
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            progress?.Report(new AutoClipProgress(2, 3, 2, 0));
            return new AutoClipResult(3, 1, "");
        }
    }

    [Fact]
    public async Task AutoClip_ClientDeadline_DoesNotStopTheBatch_AndItsResultArrivesOverSse()
    {
        var hub = new SidecarEventHub();
        var (reader, subscription) = hub.Subscribe();
        using var _ = subscription;
        var jobs = new SidecarJobs(Work());
        var clipper = new SlowAutoClip();
        var options = SidecarJson.CreateOptions();
        var body = new AutoObjectiveClipsBody(4242, 7);

        using var deadline = new CancellationTokenSource();
        var request = SidecarEndpoints.AutoClipObjectivesAsync(body, clipper, () => Task.CompletedTask, jobs, hub, options,
            NullLogger.Instance, deadline.Token);
        await clipper.Started.Task.WaitAsync(Wait);
        deadline.Cancel(); // the client's 280 s deadline passed

        var (status, reply) = Read(await request.WaitAsync(Wait));
        Assert.Equal(499, status);
        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.False(clipper.Token.IsCancellationRequested); // the clip in progress was not killed

        // A repeat click while it runs joins the same batch instead of planning it twice.
        var again = SidecarEndpoints.AutoClipObjectivesAsync(body, clipper, () => Task.CompletedTask, jobs, hub, options,
            NullLogger.Instance, CancellationToken.None);
        clipper.Release.SetResult();
        var (againStatus, againReply) = Read(await again.WaitAsync(Wait));
        Assert.Equal(200, againStatus);
        Assert.Equal(3, againReply.GetProperty("created").GetInt32());
        Assert.Equal(1, clipper.Runs);

        var events = await DrainAsync(reader, "autoClipProgress", e => e.GetProperty("finished").GetBoolean());
        Assert.Equal(new[] { 0, 2 }, events.Where(e => !e.GetProperty("finished").GetBoolean()).Select(e => e.GetProperty("done").GetInt32()));
        var done = events[^1];
        Assert.True(done.GetProperty("ok").GetBoolean());
        Assert.Equal(4242, done.GetProperty("gameId").GetInt64());
        Assert.Equal(7, done.GetProperty("objectiveId").GetInt64());
        Assert.Equal(3, done.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task AutoClip_AFailedBatch_IsReportedToTheWaitingClientAndOverSse()
    {
        var hub = new SidecarEventHub();
        var (reader, subscription) = hub.Subscribe();
        using var _ = subscription;
        var failing = new ThrowingAutoClip();

        var (status, reply) = Read(await SidecarEndpoints.AutoClipObjectivesAsync(new AutoObjectiveClipsBody(5, null), failing,
            () => Task.CompletedTask, new SidecarJobs(Work()), hub, SidecarJson.CreateOptions(), NullLogger.Instance, CancellationToken.None));

        Assert.Equal(422, status);
        Assert.Equal("Auto-clip failed (is ffmpeg installed?).", reply.GetProperty("error").GetString());
        var done = (await DrainAsync(reader, "autoClipProgress", e => e.GetProperty("finished").GetBoolean()))[^1];
        Assert.False(done.GetProperty("ok").GetBoolean());
    }

    private sealed class ThrowingAutoClip : IAutoClipService
    {
        public Task<AutoClipResult> ClipObjectiveEventsAsync(long gameId, long? objectiveId, CancellationToken ct = default,
            IProgress<AutoClipProgress>? progress = null) => throw new IOException("disk full");
    }

    // ── POST /api/update/download ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateDownload_OutlivesTheClientDeadline_AndARetryJoinsIt()
    {
        var hub = new SidecarEventHub();
        var (reader, subscription) = hub.Subscribe();
        using var _ = subscription;
        var jobs = new SidecarJobs(Work());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloads = 0;
        CancellationToken downloadToken = default;
        async Task<UpdateDownloadResult> Download(Action<int>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref downloads);
            downloadToken = ct;
            progress?.Invoke(10); progress?.Invoke(10); progress?.Invoke(55);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new UpdateDownloadResult(true, null, "3.15.0", "Downloaded.");
        }
        var options = SidecarJson.CreateOptions();

        using var deadline = new CancellationTokenSource();
        var first = SidecarEndpoints.DownloadUpdateAsync(Download, jobs, hub, options, NullLogger.Instance, deadline.Token);
        await started.Task.WaitAsync(Wait);
        deadline.Cancel();
        Assert.Equal(499, Read(await first.WaitAsync(Wait)).Status);
        Assert.False(downloadToken.IsCancellationRequested);

        var retry = SidecarEndpoints.DownloadUpdateAsync(Download, jobs, hub, options, NullLogger.Instance, CancellationToken.None);
        release.SetResult();
        var (status, reply) = Read(await retry.WaitAsync(Wait));
        Assert.Equal(200, status);
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("3.15.0", reply.GetProperty("version").GetString());
        Assert.Equal(1, downloads);

        var events = await DrainAsync(reader, "updateDownloadProgress", e => e.GetProperty("finished").GetBoolean());
        Assert.Equal(new[] { 10, 55, 100 }, events.Select(e => e.GetProperty("percent").GetInt32()));
        Assert.True(events[^1].GetProperty("ok").GetBoolean());
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static (int Status, JsonElement Body) Read(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return (status, JsonSerializer.SerializeToElement(value, SidecarJson.CreateOptions()));
    }

    private static async Task<List<JsonElement>> DrainAsync(ChannelReader<SidecarEventHub.SidecarEvent> reader, string type,
        Func<JsonElement, bool> last)
    {
        var events = new List<JsonElement>();
        using var timeout = new CancellationTokenSource(Wait);
        while (true)
        {
            var evt = await reader.ReadAsync(timeout.Token);
            if (evt.Type != type) continue;
            var payload = JsonSerializer.SerializeToElement(evt.Payload, SidecarJson.CreateOptions());
            events.Add(payload);
            if (last(payload)) return events;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition never held");
            await Task.Delay(10);
        }
    }
}
