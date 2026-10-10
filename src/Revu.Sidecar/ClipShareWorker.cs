#nullable enable

using System.Diagnostics;
using System.Threading.Channels;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>C6 6.2b: the last known share state of one bookmark.</summary>
public sealed record ClipShareStatus(
    string State,
    string? Phase,
    long SentBytes,
    long TotalBytes,
    string? Url,
    string? Error,
    bool Retryable,
    bool NeedsLogin,
    bool Narrated = false,
    bool TranscriptAttached = false)
{
    public static readonly ClipShareStatus Idle = new("idle", null, 0, 0, null, null, false, false);
}

/// <summary>
/// Background clip sharing (C6 6.2). One global queue, one upload at a time. Each job runs
/// through <see cref="SidecarBackgroundWork.TryRun"/> so shutdown drains or cancels it; the
/// dispatcher loop itself is a plain task on <see cref="SidecarBackgroundWork.Stopping"/>.
/// Progress goes out as <c>clipShareProgress</c> SSE events (bytes throttled to 4/s per
/// bookmark; phase changes, done and error always sent) and is kept for the status poll.
/// </summary>
public sealed partial class ClipShareWorker
{
    private const int MaxServerClipSeconds = 610;
    private static readonly long ThrottleTicks = Stopwatch.Frequency / 4;

    private readonly IVodRepository _vod;
    private readonly IClipNarrationRepository _narrations;
    private readonly IConfigService _config;
    private readonly IClipUploadService _clips;
    private readonly IClipRetentionGuard _retention;
    private readonly INarrationMixer _mixer;
    private readonly Func<Task> _ensureBackedUp;
    private readonly SidecarEventHub _hub;
    private readonly SidecarBackgroundWork _work;
    private readonly RemoteClipCleanupStore _cleanup;
    private readonly ILogger<ClipShareWorker> _logger;

    private readonly Channel<ShareJob> _queue = Channel.CreateUnbounded<ShareJob>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _gate = new();
    private readonly Dictionary<long, ShareJob> _active = new();
    private readonly Dictionary<long, ClipShareStatus> _status = new();
    private int _started;

    public ClipShareWorker(
        IVodRepository vod,
        IClipNarrationRepository narrations,
        IConfigService config,
        IClipUploadService clips,
        IClipRetentionGuard retention,
        INarrationMixer mixer,
        Func<Task> ensureBackedUp,
        SidecarEventHub hub,
        SidecarBackgroundWork work,
        RemoteClipCleanupStore cleanup,
        ILogger<ClipShareWorker> logger)
    {
        _vod = vod;
        _narrations = narrations;
        _config = config;
        _clips = clips;
        _retention = retention;
        _mixer = mixer;
        _ensureBackedUp = ensureBackedUp;
        _hub = hub;
        _work = work;
        _cleanup = cleanup;
        _logger = logger;
    }

    /// <summary>One queued or running share.</summary>
    internal sealed class ShareJob
    {
        public required long GameId { get; init; }
        public required long BookmarkId { get; init; }
        public required string Champion { get; init; }
        public required string Title { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public bool Started { get; set; }
        public bool Narrated { get; set; }
        /// <summary>Why the job was cancelled; published as its error. Guarded by <c>_gate</c>.</summary>
        public string? CancelReason { get; set; }
        public string LastPhase { get; set; } = "";
        public long LastBytesPublish { get; set; }
    }

    /// <summary>Start the dispatcher (idempotent). Only the non-isolated host calls this.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(() => DispatchAsync(_work.Stopping));
    }

    /// <summary>
    /// Queue a share for <paramref name="bookmarkId"/>. Idempotent: a bookmark whose job is
    /// already queued or running keeps that job. Returns the job id (the bookmark id).
    /// </summary>
    public string Enqueue(long gameId, long bookmarkId, string champion, string title, bool narrated)
    {
        ShareJob job;
        lock (_gate)
        {
            if (_active.TryGetValue(bookmarkId, out var existing) && !existing.Cts.IsCancellationRequested)
                return bookmarkId.ToString();
            job = new ShareJob
            {
                GameId = gameId,
                BookmarkId = bookmarkId,
                Champion = champion ?? "",
                Title = title ?? "",
                Cts = CancellationTokenSource.CreateLinkedTokenSource(_work.Stopping),
                Narrated = narrated,
            };
            _active[bookmarkId] = job;
        }
        Publish(job, "queued");
        _queue.Writer.TryWrite(job);
        return bookmarkId.ToString();
    }

    /// <summary>
    /// Cancel the bookmark's queued or running share (no-op when none). The job's error event
    /// carries <paramref name="reason"/> (e.g. <see cref="NarrationChangedMessage"/>); without one
    /// the error is empty, which the UI treats as a silent stop.
    /// </summary>
    public void Cancel(long bookmarkId, string? reason = null)
    {
        ShareJob? job;
        bool started;
        lock (_gate)
        {
            if (!_active.TryGetValue(bookmarkId, out job)) return;
            started = job.Started;
            if (!started) _active.Remove(bookmarkId);
            if (!string.IsNullOrEmpty(reason)) job.CancelReason = reason;
        }
        try { job.Cts.Cancel(); }
        catch (ObjectDisposedException) { }
        // A running job reports its own cancellation; a queued one never will.
        if (!started) PublishError(job, CancelMessage(job), retryable: false, needsLogin: false);
    }

    /// <summary>The error a cancelled job publishes: its cancel reason, else empty.</summary>
    private string CancelMessage(ShareJob job)
    {
        lock (_gate) return job.CancelReason ?? "";
    }

    public bool IsActive(long bookmarkId)
    {
        lock (_gate) return _active.ContainsKey(bookmarkId);
    }

    public ClipShareStatus GetStatus(long bookmarkId)
    {
        lock (_gate) return _status.TryGetValue(bookmarkId, out var s) ? s : ClipShareStatus.Idle;
    }

    private async Task DispatchAsync(CancellationToken stopping)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stopping).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var job))
                {
                    if (job.Cts.IsCancellationRequested)
                    {
                        Retire(job);
                        continue;
                    }
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_work.TryRun("clip-share", async () =>
                        {
                            try { await RunJobAsync(job).ConfigureAwait(false); }
                            finally { completion.TrySetResult(); }
                        }))
                    {
                        PublishError(job, "", retryable: false, needsLogin: false);
                        Retire(job);
                        return;
                    }
                    await completion.Task.ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clip share dispatcher stopped unexpectedly");
        }
    }

    private void Retire(ShareJob job)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(job.BookmarkId, out var current) && ReferenceEquals(current, job))
                _active.Remove(job.BookmarkId);
        }
        job.Cts.Dispose();
    }

    // ── Publishing ───────────────────────────────────────────────────────────

    private void Publish(ShareJob job, string phase, long? sent = null, long? total = null,
        string? url = null, bool? transcriptAttached = null)
    {
        var state = phase switch
        {
            "queued" => "queued",
            "done" => "done",
            "error" => "error",
            _ => "running",
        };
        var status = new ClipShareStatus(state, phase, sent ?? 0, total ?? 0, url, null, false, false,
            job.Narrated, transcriptAttached ?? false);
        lock (_gate)
        {
            // A cancelled job that finishes after the same clip was shared again must not
            // overwrite the new job's state (or confuse a waiting UI with its events).
            if (IsSuperseded(job)) return;
            _status[job.BookmarkId] = status;
        }

        // Byte progress within one phase is throttled; a phase change always goes out.
        var now = Stopwatch.GetTimestamp();
        if (phase == job.LastPhase && phase == "uploading" && now - job.LastBytesPublish < ThrottleTicks) return;
        job.LastPhase = phase;
        job.LastBytesPublish = now;
        _hub.Publish("clipShareProgress", new
        {
            gameId = job.GameId,
            bookmarkId = job.BookmarkId,
            phase,
            sentBytes = sent,
            totalBytes = total,
            url,
            error = (string?)null,
            retryable = (bool?)null,
            needsLogin = (bool?)null,
            narrated = (bool?)job.Narrated,
            transcriptAttached,
        });
    }

    private void PublishError(ShareJob job, string error, bool retryable, bool needsLogin)
    {
        lock (_gate)
        {
            if (IsSuperseded(job)) return;
            _status[job.BookmarkId] = new ClipShareStatus("error", "error", 0, 0, null, error, retryable, needsLogin,
                job.Narrated);
        }
        job.LastPhase = "error";
        _hub.Publish("clipShareProgress", new
        {
            gameId = job.GameId,
            bookmarkId = job.BookmarkId,
            phase = "error",
            sentBytes = (long?)null,
            totalBytes = (long?)null,
            url = (string?)null,
            error,
            retryable,
            needsLogin,
            narrated = (bool?)job.Narrated,
            transcriptAttached = (bool?)null,
        });
    }

    /// <summary>True when a newer job owns this bookmark. Call under <c>_gate</c>.</summary>
    private bool IsSuperseded(ShareJob job) =>
        _active.TryGetValue(job.BookmarkId, out var current) && !ReferenceEquals(current, job);

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; posts asynchronously and can reorder).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static bool SamePath(string? a, string? b) =>
        string.Equals(ClipRetentionGuard.Normalize(a), ClipRetentionGuard.Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static int ClampDuration(double seconds) =>
        (int)Math.Clamp(Math.Ceiling(double.IsFinite(seconds) ? seconds : 1), 1, MaxServerClipSeconds);
}
