using Revu.Core.Services;

namespace Revu.Sidecar.Tests;

/// <summary>
/// A scripted HLS upload session for the share worker tests: while started it acknowledges every segment as it is
/// added (in order), reports progress the way the real session does, and records every call. A test can hold
/// acknowledgements, set the ingest ratio, or end the upload with a terminal answer.
/// </summary>
internal sealed class FakeHlsUpload : IVodHlsUpload
{
    private readonly object _gate = new();
    private readonly List<HlsSegment> _queued = new();
    private readonly List<HlsSegment> _acked = new();
    private readonly List<HlsInit> _runs = new();
    private TaskCompletionSource _done = New();
    private bool _started;
    private bool _addingDone;

    public VodHlsSessionInfo Info { get; } = new("hls1234", "https://revu.lol/v/hls1234", FakeVodClient.Now + 259_200,
        FakeVodClient.Now + 3_600, 1153, 16_777_216, 16_384, 8000, 8);

    public VodShareQuality Quality { get; set; } = VodShareQuality.P1080;
    public int Fps { get; set; } = 60;
    public Action<VodHlsUploadState>? Progress { get; set; }

    /// <summary>Every call, in order ("start", "stop", "drain", "pause:game", "end:12:true", "delete", "drop:6", ...).</summary>
    public List<string> Calls { get; } = new();
    /// <summary>Acknowledge segments as they come (false holds them until <see cref="AckAll"/>).</summary>
    public bool AutoAck { get; set; } = true;
    /// <summary>The proxy says playable once this many segments are acknowledged.</summary>
    public int PlayableAfter { get; set; } = 4;
    public double? Ingest { get; set; }
    public double ActiveSeconds { get; set; } = 100;
    public bool CommitsPastEdge { get; set; }
    /// <summary>Fail when this seq would be acknowledged (it stays unacknowledged).</summary>
    public Func<int, VodHlsUploadException?>? FailAt { get; set; }
    public Exception? EndError { get; set; }
    public bool Ended { get; private set; }
    public bool Deleted { get; private set; }
    public bool Abandoned { get; private set; }
    public List<VodShareQuality> RunQualities { get; } = new();

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Record(string call)
    {
        lock (Calls) Calls.Add(call);
    }

    public bool Saw(string call)
    {
        lock (Calls) return Calls.Contains(call);
    }

    public Task Completion
    {
        get { lock (_gate) return _done.Task; }
    }

    public VodHlsUploadState State
    {
        get { lock (_gate) return Snapshot(); }
    }

    private VodHlsUploadState Snapshot()
    {
        var acked = _acked.Count;
        var lastRun = _runs.Where(r => r.FromSeq < acked).Select(r => r.FromSeq).DefaultIfEmpty(0).Max();
        return new VodHlsUploadState(acked, acked == 0 ? 0 : _acked[^1].EndSeconds, acked >= PlayableAfter, _queued.Count,
            _runs.Count(r => r.FromSeq < acked || acked == 0 && r.FromSeq == 0), acked * 1_250_000L, Ingest, ActiveSeconds,
            CommitsPastEdge, lastRun);
    }

    public void Add(HlsItem item)
    {
        lock (_gate)
        {
            switch (item)
            {
                case HlsInit init:
                    _runs.Add(init);
                    RunQualities.Add(Quality);
                    break;
                case HlsSegment segment:
                    _queued.Add(segment);
                    break;
            }
        }
        if (AutoAck) AckAll();
    }

    /// <summary>Acknowledge what is queued (when started), reporting progress after each.</summary>
    public void AckAll()
    {
        while (true)
        {
            VodHlsUploadState state;
            lock (_gate)
            {
                if (!_started || _queued.Count == 0)
                {
                    if (_started && _addingDone && _queued.Count == 0) _done.TrySetResult();
                    return;
                }
                var next = _queued[0];
                if (FailAt?.Invoke(next.Seq) is { } failure)
                {
                    _started = false;
                    _done.TrySetException(failure);
                    return;
                }
                _queued.RemoveAt(0);
                _acked.Add(next);
                state = Snapshot();
            }
            Progress?.Invoke(state);
        }
    }

    public void CompleteAdding()
    {
        Record("complete");
        lock (_gate) _addingDone = true;
        AckAll();
    }

    public void DropFrom(int seq)
    {
        Record($"drop:{seq}");
        lock (_gate)
        {
            _queued.RemoveAll(s => s.Seq >= seq);
            _runs.RemoveAll(r => r.FromSeq >= seq && r.FromSeq >= _acked.Count);
        }
    }

    public void Start(CancellationToken ct)
    {
        Record("start");
        lock (_gate)
        {
            _started = true;
            if (_done.Task.IsCompleted) _done = New();
        }
        if (AutoAck) AckAll();
    }

    public Task StopAsync()
    {
        Record("stop");
        lock (_gate) _started = false;
        return Task.CompletedTask;
    }

    public Task<int> DrainAsync(CancellationToken ct)
    {
        Record("drain");
        lock (_gate)
        {
            _started = false;
            return Task.FromResult(_acked.Count);
        }
    }

    public void ResetIngest() => Record("reset");

    public Task PauseAsync(string reason, CancellationToken ct)
    {
        Record($"pause:{reason}");
        return Task.CompletedTask;
    }

    public Task<VodHlsEndResult> EndAsync(int segments, bool truncated, CancellationToken ct)
    {
        Record($"end:{segments}:{(truncated ? "true" : "false")}");
        if (EndError is { } error) return Task.FromException<VodHlsEndResult>(error);
        Ended = true;
        double media;
        lock (_gate) media = _acked.Take(segments).Select(s => s.EndSeconds).DefaultIfEmpty(0).Max();
        return Task.FromResult(new VodHlsEndResult(Info.Id, Info.Url, Info.ShareExpiresAt, segments * 1_250_000L,
            (long)(media * 1000), segments, truncated));
    }

    public Task<bool> DeleteAsync(CancellationToken ct)
    {
        Record("delete");
        Deleted = true;
        return Task.FromResult(true);
    }

    public void Abandon()
    {
        Record("abandon");
        Abandoned = true;
    }

    public ValueTask DisposeAsync()
    {
        Record("dispose");
        return ValueTask.CompletedTask;
    }
}
