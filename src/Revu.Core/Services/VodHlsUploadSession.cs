#nullable enable

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>Why an HLS upload stopped (plan 4.7): what the share worker does next depends on it.</summary>
public enum VodHlsFailure
{
    /// <summary>404: the share was stopped or is gone. The job ends without a delete.</summary>
    Stopped,
    /// <summary>409 run_conflict / run_mismatch / segments_incomplete, or a 4xx the desktop never expects: fail loudly.</summary>
    Conflict,
    /// <summary>413: over the reservation after it could not grow. One 720p run, then end at the edge.</summary>
    TooLarge,
    /// <summary>429: the attempt cap or the run cap. End at the edge.</summary>
    Limited,
    /// <summary>401: the session expired. End at the edge and ask for a sign-in.</summary>
    Unauthorized,
    /// <summary>The proxy stayed unreachable past the outage window, or kept failing.</summary>
    Network,
}

/// <summary>A terminal HLS upload response, with the proxy's error code.</summary>
public sealed class VodHlsUploadException(VodHlsFailure kind, string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public VodHlsFailure Kind { get; } = kind;
    public string Code { get; } = code;
}

/// <summary>What an HLS init reserved (4.4 contract): the share, its slots and the limits its uploads keep.</summary>
public sealed record VodHlsSessionInfo(string Id, string Url, long ShareExpiresAt, long UploadExpiresAt, int Slots,
    long MaxSegmentBytes, int InitMaxBytes, int PlayableMs, int MaxRuns);

/// <summary>
/// Where the upload stands (plan 4.7 reports): the contiguous acknowledged segments and media, whether the proxy says
/// the share is playable, the backlog, the runs the proxy accepted (and where the last one starts), the bytes
/// acknowledged, the ingest ratio (media seconds acknowledged per wall second over the last 30 s; null before 10 s of
/// history), how long acknowledgements have been coming, and whether any seq past the edge is already acknowledged.
/// </summary>
public sealed record VodHlsUploadState(int Acked, double AckedSeconds, bool Playable, int Backlog, int RunsAccepted,
    long AckedBytes, double? IngestRatio, double ActiveSeconds, bool CommitsPastEdge, int LastRunFrom = 0);

/// <summary>The proxy's answer to <c>POST /vod-shares/:id/hls/end</c>.</summary>
public sealed record VodHlsEndResult(string Id, string Url, long ExpiresAt, long SizeBytes, long MediaMs, int Segments,
    bool Truncated);

/// <summary>One HLS share's upload, as the share worker drives it (see <see cref="VodHlsUploadSession"/>).</summary>
public interface IVodHlsUpload : IAsyncDisposable
{
    VodHlsSessionInfo Info { get; }
    VodShareQuality Quality { get; set; }
    int Fps { get; set; }
    Action<VodHlsUploadState>? Progress { get; set; }
    Task Completion { get; }
    VodHlsUploadState State { get; }
    void Add(HlsItem item);
    void CompleteAdding();
    void DropFrom(int seq);
    void Start(CancellationToken ct);
    Task StopAsync();
    Task<int> DrainAsync(CancellationToken ct);
    void ResetIngest();
    Task PauseAsync(string reason, CancellationToken ct);
    Task<VodHlsEndResult> EndAsync(int segments, bool truncated, CancellationToken ct);
    Task<bool> DeleteAsync(CancellationToken ct);
    void Abandon();
}

/// <summary>
/// One HLS share's upload (plan 4.7): the encoder's items go in with <see cref="Add"/>, and a pump sends them with one
/// body on the wire at a time, in seq order. The next body starts once the previous one's last byte is written; up to
/// <see cref="MaxAwaiting"/> requests wait for an answer, never more than that many seqs past the lowest one not yet
/// acknowledged. A run's segments go only after the proxy accepted the run's init. Retries follow
/// <see cref="UploadRetry"/>; the per-PUT timeout is max(30 s, bytes at 50 KB/s). A terminal answer faults
/// <see cref="Completion"/> with a <see cref="VodHlsUploadException"/>. <see cref="StopAsync"/> pauses (in-flight
/// requests are aborted, everything unacknowledged is sent again later) and <see cref="DrainAsync"/> finishes what is
/// in flight first. Disposing a share that was neither ended nor deleted ends it at the edge when anything playable
/// was committed, and deletes it otherwise.
/// </summary>
public sealed class VodHlsUploadSession : IVodHlsUpload
{
    public const int MaxAwaiting = 8;
    private const long SlowestBytesPerSecond = 50 * 1024;
    private static readonly TimeSpan MinPutTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisposeBudget = TimeSpan.FromSeconds(10);
    private static readonly long IngestWindowTicks = Stopwatch.Frequency * 30;
    private static readonly long IngestMinimumTicks = Stopwatch.Frequency * 10;

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _base;
    private readonly string _token;
    private readonly Func<UploadRetry> _retry;
    private readonly Func<CancellationToken, Task<bool>> _delete;
    private readonly object _gate = new();
    private readonly SortedDictionary<int, Segment> _segments = new();
    private readonly SortedDictionary<int, Run> _runs = new();
    private readonly List<(long Ticks, double Seconds)> _samples = new();
    private TaskCompletionSource _done = NewDone();
    private TaskCompletionSource _wake = NewDone();
    private Task? _pump;
    private CancellationTokenSource? _pumpStop;
    private bool _addingDone;
    private bool _stopSending;
    private int _awaiting;
    private int _acked;
    private double _ackedSeconds;
    private long _ackedBytes;
    private bool _playable;
    private int _runsAccepted;
    private int _lastRunFrom;
    private long _firstAckTicks;
    private VodHlsUploadException? _terminal;
    private bool _ended;
    private bool _deleted;
    private bool _gone;

    internal VodHlsUploadSession(HttpClient http, ILogger logger, string baseUrl, string sessionToken, VodHlsSessionInfo info,
        Func<UploadRetry> retry, Func<CancellationToken, Task<bool>> delete)
    {
        _http = http;
        _logger = logger;
        _base = $"{baseUrl.TrimEnd('/')}/{ClipUploadService.VodRouteBase}/{Uri.EscapeDataString(info.Id)}";
        _token = sessionToken;
        _retry = retry;
        _delete = delete;
        Info = info;
    }

    public VodHlsSessionInfo Info { get; }

    /// <summary>The quality and frame rate the next run's init is announced with.</summary>
    public VodShareQuality Quality { get; set; } = VodShareQuality.P1080;
    public int Fps { get; set; } = 60;

    /// <summary>Called after every acknowledgement (outside any lock).</summary>
    public Action<VodHlsUploadState>? Progress { get; set; }

    /// <summary>Completes once everything added is acknowledged after <see cref="CompleteAdding"/>; faults on a terminal answer.</summary>
    public Task Completion
    {
        get { lock (_gate) return _done.Task; }
    }

    public VodHlsUploadState State
    {
        get { lock (_gate) return Snapshot(Stopwatch.GetTimestamp()); }
    }

    private static TaskCompletionSource NewDone() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ── Items ────────────────────────────────────────────────────────────────

    private enum Phase { Queued, Sending, Awaiting, Done }

    private sealed class Segment(HlsSegment item, UploadRetry retry)
    {
        public HlsSegment Item { get; } = item;
        public UploadRetry Retry { get; } = retry;
        public Phase Phase { get; set; }
        public long DueTicks { get; set; }
        public long SentTicks { get; set; }
    }

    private sealed class Run(HlsInit item, UploadRetry retry)
    {
        public HlsInit Item { get; } = item;
        public UploadRetry Retry { get; } = retry;
        public Phase Phase { get; set; }
        public long DueTicks { get; set; }
    }

    /// <summary>Queue an item the encoder announced. A segment replaces an unsent one of the same seq.</summary>
    public void Add(HlsItem item)
    {
        lock (_gate)
        {
            switch (item)
            {
                case HlsInit init when !_runs.ContainsKey(init.Run):
                    _runs[init.Run] = new Run(init, _retry());
                    break;
                case HlsSegment segment when !_segments.TryGetValue(segment.Seq, out var old) || old.Phase == Phase.Queued:
                    _segments[segment.Seq] = new Segment(segment, _retry());
                    break;
            }
            Wake();
        }
    }

    /// <summary>No more items will come: the pump completes once the queue is acknowledged.</summary>
    public void CompleteAdding()
    {
        lock (_gate)
        {
            _addingDone = true;
            Wake();
        }
    }

    /// <summary>
    /// Forget unacknowledged segments at or after <paramref name="seq"/> and runs that start there (a quality switch
    /// starts over at the edge). Only while the pump is stopped.
    /// </summary>
    public void DropFrom(int seq)
    {
        lock (_gate)
        {
            if (_pump is { IsCompleted: false }) throw new InvalidOperationException("Stop the upload before dropping segments.");
            foreach (var key in _segments.Where(kv => kv.Key >= seq && kv.Value.Phase != Phase.Done).Select(kv => kv.Key).ToList())
                _segments.Remove(key);
            foreach (var key in _runs.Where(kv => kv.Value.Item.FromSeq >= seq && kv.Value.Phase != Phase.Done).Select(kv => kv.Key).ToList())
                _runs.Remove(key);
        }
    }

    // ── The pump ─────────────────────────────────────────────────────────────

    /// <summary>Start (or restart after a stop or a failure) sending what is queued.</summary>
    public void Start(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_pump is { IsCompleted: false }) return;
            if (_ended || _deleted || _gone) throw new InvalidOperationException("This share upload is over.");
            if (_terminal is not null || _done.Task.IsCompleted)
            {
                _terminal = null;
                _done = NewDone();
            }
            _stopSending = false;
            foreach (var s in _segments.Values) if (s.Phase is Phase.Sending or Phase.Awaiting) s.Phase = Phase.Queued;
            foreach (var r in _runs.Values) if (r.Phase is Phase.Sending or Phase.Awaiting) r.Phase = Phase.Queued;
            _awaiting = 0;
            _pumpStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stop = _pumpStop.Token;
            _pump = Task.Run(() => PumpAsync(stop), CancellationToken.None);
        }
    }

    /// <summary>Pause: abort in-flight requests and stop. Everything unacknowledged is sent again after <see cref="Start"/>.</summary>
    public async Task StopAsync()
    {
        Task? pump;
        lock (_gate)
        {
            pump = _pump;
            try { _pumpStop?.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (pump is not null)
        {
            try { await pump.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or VodHlsUploadException) { }
        }
    }

    /// <summary>
    /// Stop sending new seqs, finish every request already sent (with retries), then stop. Returns the contiguous
    /// acknowledged segments: with nothing in flight any more, every sent seq is acknowledged.
    /// </summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        Task? pump;
        lock (_gate)
        {
            _stopSending = true;
            pump = _pump;
            Wake();
        }
        if (pump is not null)
        {
            using var reg = ct.Register(() => { try { _pumpStop?.Cancel(); } catch (ObjectDisposedException) { } });
            try { await pump.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or VodHlsUploadException) { }
            ct.ThrowIfCancellationRequested();
        }
        lock (_gate)
        {
            if (_terminal is { } failure) throw failure;
            return _acked;
        }
    }

    /// <summary>Start the ingest measurement over (after a pause the old window would read as a slow upload).</summary>
    public void ResetIngest()
    {
        lock (_gate)
        {
            _samples.Clear();
            _firstAckTicks = 0;
        }
    }

    private void Wake()
    {
        var wake = _wake;
        _wake = NewDone();
        wake.TrySetResult();
    }

    private abstract record Work;
    private sealed record SegmentWork(Segment Segment) : Work;
    private sealed record RunWork(Run Run) : Work;

    private async Task PumpAsync(CancellationToken ct)
    {
        var inFlight = new List<Task>();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Work? next;
                Task wake;
                TimeSpan? wait;
                lock (_gate)
                {
                    inFlight.RemoveAll(t => t.IsCompleted);
                    if (_terminal is not null)
                    {
                        // Nothing new goes out, but what was sent settles first: the edge decides where the share ends
                        // or a 720p run starts. A share stopped elsewhere has no edge to keep.
                        if (_terminal.Kind == VodHlsFailure.Stopped || _awaiting == 0) break;
                        next = null;
                        wait = null;
                    }
                    else if (_stopSending && _awaiting == 0 && !_segments.Values.Any(s => s.Phase == Phase.Queued && s.SentTicks > 0)
                             && !_runs.Values.Any(r => r.Phase == Phase.Queued && r.DueTicks > 0))
                    {
                        break;
                    }
                    else if (_addingDone && _awaiting == 0 && _segments.Values.All(s => s.Phase == Phase.Done)
                             && _runs.Values.All(r => r.Phase == Phase.Done))
                    {
                        _done.TrySetResult();
                        break;
                    }
                    else
                    {
                        next = Pick(Stopwatch.GetTimestamp(), out wait);
                    }
                    wake = _wake.Task;
                }

                if (next is null)
                {
                    // A stop (the token) always ends the wait, even with nothing in flight.
                    await Task.WhenAny(wake, Task.Delay(wait ?? Timeout.InfiniteTimeSpan, ct)).ConfigureAwait(false);
                    continue;
                }

                var (send, sent) = next switch
                {
                    SegmentWork s => SendSegment(s.Segment, ct),
                    RunWork r => SendRun(r.Run, ct),
                    _ => throw new InvalidOperationException(),
                };
                inFlight.Add(send);
                // One body on the wire: the next starts once this one's last byte is written (or it ended early).
                await Task.WhenAny(sent, send).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ct.IsCancellationRequested || _terminal is not null)
            {
                try { _pumpStop?.Cancel(); } catch (ObjectDisposedException) { }
            }
            try { await Task.WhenAll(inFlight).ConfigureAwait(false); }
            catch { /* each request reports its own outcome */ }
            lock (_gate)
            {
                if (_terminal is { } failure) _done.TrySetException(failure);
            }
        }
    }

    /// <summary>The next thing to send, in seq order (a run's init sits right before its first segment). Caller holds the gate.</summary>
    private Work? Pick(long now, out TimeSpan? wait)
    {
        wait = null;
        if (_awaiting >= MaxAwaiting) return null;
        var lowest = _segments.Values.FirstOrDefault(s => s.Phase != Phase.Done)?.Item.Seq;
        foreach (var segment in _segments.Values)
        {
            if (segment.Phase == Phase.Done) continue;
            var seq = segment.Item.Seq;
            if (lowest is { } low && seq >= low + MaxAwaiting) break;
            if (!_runs.TryGetValue(segment.Item.Run, out var run)) break;   // its init has not been announced
            if (run.Phase != Phase.Done)
            {
                if (run.Phase != Phase.Queued || run.Item.Run != _runsAccepted) break;
                if (_stopSending && run.DueTicks == 0) break;
                if (run.DueTicks > now)
                {
                    wait = Until(run.DueTicks, now);
                    break;
                }
                return new RunWork(run);
            }
            if (segment.Phase != Phase.Queued) continue;
            // Draining: only what was sent before goes again.
            if (_stopSending && segment.SentTicks == 0) break;
            if (segment.DueTicks > now)
            {
                var until = Until(segment.DueTicks, now);
                wait = wait is { } w && w < until ? w : until;
                continue;
            }
            return new SegmentWork(segment);
        }
        return null;
    }

    private static TimeSpan Until(long dueTicks, long now) =>
        TimeSpan.FromSeconds(Math.Max(0.001, (dueTicks - now) / (double)Stopwatch.Frequency));

    // ── Requests ─────────────────────────────────────────────────────────────

    /// <summary>Request content that says when its last byte was handed to the connection.</summary>
    private sealed class SentContent(byte[] body, string mediaType) : HttpContent
    {
        private readonly TaskCompletionSource _sent = NewDone();

        public Task Sent => _sent.Task;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            _sent.TrySetResult();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return true;
        }

        public void Prepare() => Headers.ContentType = new MediaTypeHeaderValue(mediaType);
    }

    private (Task Send, Task Sent) SendSegment(Segment segment, CancellationToken ct)
    {
        var item = segment.Item;
        byte[] body;
        try
        {
            body = File.ReadAllBytes(item.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (_gate) Fail(new VodHlsUploadException(VodHlsFailure.Network, "segment_missing",
                $"Segment {item.Seq} could not be read.", ex));
            return (Task.CompletedTask, Task.CompletedTask);
        }
        var content = new SentContent(body, "video/mp4");
        content.Prepare();
        lock (_gate)
        {
            segment.Phase = Phase.Sending;
            segment.SentTicks = Stopwatch.GetTimestamp();
            _awaiting++;
        }
        var url = $"{_base}/hls/seg/{item.Seq.ToString(CultureInfo.InvariantCulture)}"
            + $"?ms={item.DurationMs.ToString(CultureInfo.InvariantCulture)}&run={item.Run.ToString(CultureInfo.InvariantCulture)}";
        var timeout = TimeSpan.FromSeconds(Math.Max(MinPutTimeout.TotalSeconds, body.Length / (double)SlowestBytesPerSecond));
        var send = Task.Run(async () =>
        {
            var began = Stopwatch.GetTimestamp();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                using var timer = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
                using var res = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
                lock (_gate) if (segment.Phase == Phase.Sending) segment.Phase = Phase.Awaiting;
                if (res.IsSuccessStatusCode)
                {
                    var answer = await res.Content.ReadFromJsonAsync<SegmentAnswer>(linked.Token).ConfigureAwait(false);
                    Acknowledge(segment, answer, began, res);
                    return;
                }
                await RefuseAsync(segment.Retry, res, linked.Token, delay => Requeue(segment, delay), $"segment {item.Seq}")
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_gate) Settle(segment);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or JsonException)
            {
                var delay = segment.Retry.After(ex);
                if (delay is null)
                {
                    lock (_gate)
                    {
                        Settle(segment);
                        Fail(new VodHlsUploadException(VodHlsFailure.Network, "network", "The upload kept failing.", ex));
                    }
                    return;
                }
                _logger.LogInformation("HLS segment {Seq} of {Id} failed ({Error}); retrying in {Delay:F0} s", item.Seq, Info.Id,
                    ex.GetType().Name, delay.Value.TotalSeconds);
                Requeue(segment, delay.Value);
            }
        }, CancellationToken.None);
        return (send, content.Sent);
    }

    private (Task Send, Task Sent) SendRun(Run run, CancellationToken ct)
    {
        var item = run.Item;
        var content = new SentContent(item.Bytes, "video/mp4");
        content.Prepare();
        lock (_gate)
        {
            run.Phase = Phase.Sending;
            run.DueTicks = Math.Max(1, run.DueTicks);
            _awaiting++;
        }
        var url = $"{_base}/hls/runs/{item.Run.ToString(CultureInfo.InvariantCulture)}"
            + $"?from={item.FromSeq.ToString(CultureInfo.InvariantCulture)}&quality={VodShareEncoderArgs.Wire(Quality)}"
            + $"&fps={VodShareEncoderArgs.Fps(Fps).ToString(CultureInfo.InvariantCulture)}";
        var send = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = content };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                using var timer = new CancellationTokenSource(ControlTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
                using var res = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    lock (_gate)
                    {
                        run.Phase = Phase.Done;
                        _awaiting = Math.Max(0, _awaiting - 1);
                        if (item.Run + 1 > _runsAccepted)
                        {
                            _runsAccepted = item.Run + 1;
                            _lastRunFrom = item.FromSeq;
                        }
                        Wake();
                    }
                    _logger.LogInformation("HLS run {Run} of {Id} accepted from segment {From} ({Bytes} byte init)", item.Run,
                        Info.Id, item.FromSeq, item.Bytes.Length);
                    return;
                }
                await RefuseAsync(run.Retry, res, linked.Token, delay => Requeue(run, delay), $"run {item.Run}").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (run.Phase != Phase.Done) run.Phase = Phase.Queued;
                    _awaiting = Math.Max(0, _awaiting - 1);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                var delay = run.Retry.After(ex);
                if (delay is null)
                {
                    lock (_gate)
                    {
                        run.Phase = Phase.Queued;
                        _awaiting = Math.Max(0, _awaiting - 1);
                        Fail(new VodHlsUploadException(VodHlsFailure.Network, "network", "The upload kept failing.", ex));
                    }
                    return;
                }
                Requeue(run, delay.Value);
            }
        }, CancellationToken.None);
        return (send, content.Sent);
    }

    /// <summary>A segment or run request that will not get an answer any more (aborted): back to the queue.</summary>
    private void Settle(Segment segment)
    {
        if (segment.Phase != Phase.Done) segment.Phase = Phase.Queued;
        _awaiting = Math.Max(0, _awaiting - 1);
        Wake();
    }

    private void Requeue(Segment segment, TimeSpan delay)
    {
        lock (_gate)
        {
            segment.DueTicks = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
            Settle(segment);
        }
    }

    private void Requeue(Run run, TimeSpan delay)
    {
        lock (_gate)
        {
            run.DueTicks = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
            if (run.Phase != Phase.Done) run.Phase = Phase.Queued;
            _awaiting = Math.Max(0, _awaiting - 1);
            Wake();
        }
    }

    private void Acknowledge(Segment segment, SegmentAnswer? answer, long began, HttpResponseMessage res)
    {
        var item = segment.Item;
        VodHlsUploadState state;
        var now = Stopwatch.GetTimestamp();
        var counted = false;
        lock (_gate)
        {
            if (segment.Phase != Phase.Done)
            {
                segment.Phase = Phase.Done;
                counted = true;
            }
            if (answer?.playable == true) _playable = true;
            var moved = false;
            while (_segments.TryGetValue(_acked, out var next) && next.Phase == Phase.Done)
            {
                _ackedSeconds = next.Item.EndSeconds;
                _ackedBytes += next.Item.Bytes;
                _acked++;
                moved = true;
            }
            if (moved)
            {
                if (_firstAckTicks == 0) _firstAckTicks = now;
                _samples.Add((now, _ackedSeconds));
                _samples.RemoveAll(s => now - s.Ticks > IngestWindowTicks * 2);
            }
            state = Snapshot(now);
        }
        // The first segments and every 30th go to the log at Information (the canary reads them), the rest at Debug.
        var level = item.Seq < 3 || item.Seq % 30 == 0 ? LogLevel.Information : LogLevel.Debug;
        if (_logger.IsEnabled(level))
        {
            var colo = res.Headers.TryGetValues("cf-ray", out var rays) ? rays.FirstOrDefault()?.Split('-').LastOrDefault() : null;
            var timing = res.Headers.TryGetValues("Server-Timing", out var timings) ? string.Join(", ", timings) : "";
            _logger.Log(level, "HLS segment {Seq} of {Id} acknowledged in {Ms:F0} ms ({Bytes} bytes{Already}; colo {Colo}; {Timing}); "
                + "{Acked} contiguous, {Media:F1} s", item.Seq, Info.Id, Stopwatch.GetElapsedTime(began).TotalMilliseconds,
                item.Bytes, answer?.already == true ? ", already there" : "", colo ?? "?", timing, state.Acked, state.AckedSeconds);
        }
        // The request counts as waiting until its progress went out, so the upload never completes before it.
        try
        {
            Progress?.Invoke(state);
        }
        finally
        {
            lock (_gate)
            {
                if (counted) _awaiting = Math.Max(0, _awaiting - 1);
                Wake();
            }
        }
    }

    /// <summary>A non-2xx answer: retried (5xx) or terminal (the 4.7 table).</summary>
    private async Task RefuseAsync(UploadRetry retry, HttpResponseMessage res, CancellationToken ct, Action<TimeSpan> requeue,
        string what)
    {
        var status = (int)res.StatusCode;
        var code = await ErrorCodeAsync(res, ct).ConfigureAwait(false) ?? $"http_{status}";
        if (status >= 500)
        {
            if (retry.Server() is { } delay)
            {
                _logger.LogWarning("HLS {What} of {Id} returned {Status} ({Code}); retrying", what, Info.Id, status, code);
                requeue(delay);
                return;
            }
        }
        var kind = res.StatusCode switch
        {
            HttpStatusCode.NotFound => VodHlsFailure.Stopped,
            HttpStatusCode.RequestEntityTooLarge => VodHlsFailure.TooLarge,
            HttpStatusCode.TooManyRequests => VodHlsFailure.Limited,
            HttpStatusCode.Unauthorized => VodHlsFailure.Unauthorized,
            _ when status >= 500 => VodHlsFailure.Network,
            _ => VodHlsFailure.Conflict,
        };
        _logger.LogWarning("HLS {What} of {Id} refused: {Status} {Code}", what, Info.Id, status, code);
        lock (_gate)
        {
            _awaiting = Math.Max(0, _awaiting - 1);
            Fail(new VodHlsUploadException(kind, code, $"The proxy refused {what} ({status} {code})."));
        }
    }

    /// <summary>Record a terminal failure (the first one wins) and wake the pump so it stops. Caller holds the gate.</summary>
    private void Fail(VodHlsUploadException failure)
    {
        if (_terminal is null)
        {
            _terminal = failure;
            if (failure.Kind == VodHlsFailure.Stopped)
            {
                _gone = true;
                try { _pumpStop?.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
        Wake();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var body = await res.Content.ReadFromJsonAsync<ErrorAnswer>(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body?.error) ? null : body!.error!.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private VodHlsUploadState Snapshot(long now)
    {
        double? ingest = null;
        if (_samples.Count > 0 && _firstAckTicks > 0)
        {
            var since = now - IngestWindowTicks;
            var start = _samples.LastOrDefault(s => s.Ticks <= since);
            if (start == default) start = (_firstAckTicks, _samples[0].Seconds);
            var span = (now - start.Ticks) / (double)Stopwatch.Frequency;
            if (now - _firstAckTicks >= IngestMinimumTicks && span > 0)
                ingest = Math.Max(0, _ackedSeconds - start.Seconds) / span;
        }
        var backlog = _segments.Values.Count(s => s.Phase != Phase.Done);
        var past = _segments.Values.Any(s => s.Phase == Phase.Done && s.Item.Seq > _acked);
        var active = _firstAckTicks > 0 ? (now - _firstAckTicks) / (double)Stopwatch.Frequency : 0;
        return new VodHlsUploadState(_acked, _ackedSeconds, _playable, backlog, _runsAccepted, _ackedBytes, ingest, active, past,
            _lastRunFrom);
    }

    // ── Control routes ───────────────────────────────────────────────────────

    /// <summary>POST /hls/pause: the share waits for the sharer's game. Idempotent; the next upload clears it.</summary>
    public async Task PauseAsync(string reason, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { reason });
        using var res = await ControlAsync(HttpMethod.Post, "hls/pause", json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// POST /hls/end: the share is ready with <paramref name="segments"/> segments (the proxy keeps any more it already
    /// committed, so the playlist never shrinks). A replay answers the same.
    /// </summary>
    public async Task<VodHlsEndResult> EndAsync(int segments, bool truncated, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { segments, truncated });
        using var res = await ControlAsync(HttpMethod.Post, "hls/end", json, ct).ConfigureAwait(false);
        var body = await res.Content.ReadFromJsonAsync<EndAnswer>(ct).ConfigureAwait(false)
            ?? throw new VodHlsUploadException(VodHlsFailure.Conflict, "bad_response", "The proxy's answer to end was empty.");
        lock (_gate) _ended = true;
        _logger.LogInformation("HLS share {Id} ended: {Segments} segments, {Seconds:F1} s, {Bytes} bytes{Truncated}", Info.Id,
            body.segments, body.media_ms / 1000.0, body.size_bytes, body.truncated ? " (cut short)" : "");
        return new VodHlsEndResult(body.id ?? Info.Id, string.IsNullOrWhiteSpace(body.url) ? Info.Url : body.url!,
            body.expires_at > 0 ? body.expires_at : Info.ShareExpiresAt, body.size_bytes, body.media_ms,
            body.segments, body.truncated);
    }

    /// <summary>DELETE the share (Stop sharing, Cancel, or nothing playable when the job ends). True when it is gone.</summary>
    public async Task<bool> DeleteAsync(CancellationToken ct)
    {
        var gone = await _delete(ct).ConfigureAwait(false);
        if (gone) lock (_gate) _deleted = true;
        return gone;
    }

    /// <summary>Forget the share without ending or deleting it (it was stopped elsewhere).</summary>
    public void Abandon()
    {
        lock (_gate) _gone = true;
    }

    /// <summary>A pause or end call: 3 attempts on a 5xx or a failed send; a terminal answer throws.</summary>
    private async Task<HttpResponseMessage> ControlAsync(HttpMethod method, string path, string json, CancellationToken ct)
    {
        var retry = _retry();
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan? delay;
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var req = new HttpRequestMessage(method, $"{_base}/{path}") { Content = content };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                using var timer = new CancellationTokenSource(ControlTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
                var res = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);
                if (res.IsSuccessStatusCode) return res;
                using (res)
                {
                    var status = (int)res.StatusCode;
                    var code = await ErrorCodeAsync(res, linked.Token).ConfigureAwait(false) ?? $"http_{status}";
                    delay = status >= 500 && attempt < 3 ? retry.Server() : null;
                    if (delay is null)
                    {
                        var kind = res.StatusCode switch
                        {
                            HttpStatusCode.NotFound => VodHlsFailure.Stopped,
                            HttpStatusCode.Unauthorized => VodHlsFailure.Unauthorized,
                            HttpStatusCode.TooManyRequests => VodHlsFailure.Limited,
                            _ when status >= 500 => VodHlsFailure.Network,
                            _ => VodHlsFailure.Conflict,
                        };
                        if (kind == VodHlsFailure.Stopped) lock (_gate) _gone = true;
                        throw new VodHlsUploadException(kind, code, $"The proxy refused {path} ({status} {code}).");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                delay = attempt < 3 ? retry.After(ex) : null;
                if (delay is null)
                    throw new VodHlsUploadException(VodHlsFailure.Network, "network", $"Couldn't reach Revu for {path}.", ex);
            }
            await Task.Delay(delay.Value, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the pump. A share neither ended nor deleted (nor stopped elsewhere) ends at the edge when the proxy says it
    /// is playable, and is deleted otherwise. Best effort, with a short budget.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        bool settle, playable;
        int acked;
        lock (_gate)
        {
            settle = !_ended && !_deleted && !_gone;
            playable = _playable;
            acked = _acked;
        }
        if (settle)
        {
            using var budget = new CancellationTokenSource(DisposeBudget);
            try
            {
                if (playable && acked > 0) await EndAsync(acked, truncated: true, budget.Token).ConfigureAwait(false);
                else await DeleteAsync(budget.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is VodHlsUploadException or OperationCanceledException or HttpRequestException
                                           or IOException or JsonException)
            {
                // The proxy's cron settles the share (ended at the edge if playable, else discarded).
                _logger.LogInformation(ex, "HLS share {Id} could not be settled on dispose", Info.Id);
            }
        }
        _pumpStop?.Dispose();
    }

    // ── Wire DTOs (snake_case) ───────────────────────────────────────────────

    private sealed class SegmentAnswer
    {
        public int seq { get; set; }
        public int committed { get; set; }
        public long media_ms { get; set; }
        public bool playable { get; set; }
        public bool already { get; set; }
    }

    private sealed class ErrorAnswer
    {
        public string? error { get; set; }
    }

    private sealed class EndAnswer
    {
        public string? id { get; set; }
        public string? url { get; set; }
        public long expires_at { get; set; }
        public long size_bytes { get; set; }
        public long media_ms { get; set; }
        public int segments { get; set; }
        public bool truncated { get; set; }
    }
}
