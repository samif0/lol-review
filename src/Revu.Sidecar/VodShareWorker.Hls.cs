#nullable enable

using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// 4.4: a full game shared as HLS segments that play while they upload (plan 4.2). The encode and the init start
/// together and every finished segment goes up at once, in order. A League game start pauses a share that is already
/// watchable (the files stay, the proxy is told) and it continues once no game has run for 30 s; a 1080p upload slower
/// than the video plays switches to 720p once, at the edge; Revu closing ends a watchable share where its upload
/// stopped. Only Stop or Cancel deletes a watchable share; a share nobody could watch yet is deleted as before.
/// </summary>
public sealed partial class VodShareWorker
{
    /// <summary>How long no game must be in progress before a paused share continues.</summary>
    internal TimeSpan ResumeAfterNoGame { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>"0" in REVU_VOD_SHARE_HLS keeps every share of this PC on the MP4 path (tests replace the lookup).</summary>
    internal Func<string, string?> EnvironmentVariable { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>The 720p switch: a 1080p upload acknowledging fewer media seconds than this per second.</summary>
    internal const double UplinkSwitchRatio = 1.05;
    /// <summary>Below this the sheet says people watching may see pauses.</summary>
    internal const double UplinkSlowRatio = 1.0;
    internal const int UplinkSwitchMinSegments = 4;
    internal const double UplinkSwitchMinSeconds = 10;
    /// <summary>A switch this close to the end of the game would only cost a re-encode.</summary>
    internal const double UplinkSwitchMinRemainingSeconds = 60;
    /// <summary>720p is preselected when the last measured upload was below this times the 1080p rate (plan 4.7).</summary>
    internal const double PreselectRatio = 1.1;
    /// <summary>How long ending a watchable share may take once the host is stopping (inside the 10 s drain).</summary>
    internal static readonly TimeSpan EndBudgetWhenStopping = TimeSpan.FromSeconds(4);

    /// <summary>A job folder under ShareTemp: <c>game-&lt;gameId&gt;-&lt;guid32&gt;</c>.</summary>
    internal static readonly Regex JobFolderName = new(@"^game-\d+-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    /// <summary>How an HLS share ended: its link and expiry, its exact size, and whether it stops before the game does.</summary>
    internal sealed record HlsOutcome(VodUploadResult Uploaded, long SizeBytes, bool Truncated);

    /// <summary>The share was stopped elsewhere (the proxy answered 404): the job ends without a delete.</summary>
    internal sealed class HlsStoppedException() : Exception("The share was stopped elsewhere.");

    /// <summary>HLS is used when the proxy offers it for this account, the length came from a real probe, and this PC allows it.</summary>
    internal bool HlsAllowed(VodShareServerStatus status, SourceInfo source) =>
        status.Hls is { Enabled: true } && source.Probed && EnvironmentVariable("REVU_VOD_SHARE_HLS") != "0";

    /// <summary>
    /// Run the job as an HLS share. Null when it already ended with a reported outcome (an error, or a share stopped
    /// elsewhere). An init refused with <c>hls_unavailable</c>, or answered by an older proxy, continues on today's
    /// MP4 path (the latter on the row it already reserved).
    /// </summary>
    private async Task<HlsOutcome?> RunHlsAsync(VodShareJob job, string token, Action<VodUploadStarted> assigned,
        string mp4File, CancellationToken ct)
    {
        var run = new HlsRun(this, job, token);
        try
        {
            return await run.RunAsync(assigned, mp4File, ct).ConfigureAwait(false);
        }
        finally
        {
            await run.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The 1080p rate an upload must sustain, in bytes per second (video with its overshoot, and audio).</summary>
    internal static double Rate1080BytesPerSecond(int fps, double durationSeconds) =>
        (VodShareEncoderArgs.VideoKbps(VodShareQuality.P1080, fps, durationSeconds) * 1.04 + 128) * 125;

    /// <summary>One HLS job's state machine (plan 4.2).</summary>
    private sealed class HlsRun(VodShareWorker worker, VodShareJob job, string token) : IAsyncDisposable
    {
        // Not SingleReader: that channel cannot count what it holds, and the loop asks.
        private readonly Channel<HlsItem> _items = Channel.CreateUnbounded<HlsItem>();
        private readonly string _folder = Path.Combine(worker.TempDirectory, $"game-{job.GameId}-{Guid.NewGuid():N}");
        private IVodHlsUpload? _upload;
        private Task<VodHlsEncodeResult>? _encode;
        private CancellationTokenSource? _encodeStop;
        private VodHlsEncodeResult? _final;
        private bool _addingDone;
        private int _nextRun;
        private int _nextSeq;
        private double _diskEnd;
        private int _storedOnce;
        private bool _ended;
        private double? _lastIngest;
        private double _bytesPerMediaSecond;

        private ILogger Log => worker._logger;

        public async Task<HlsOutcome?> RunAsync(Action<VodUploadStarted> assigned, string mp4File, CancellationToken ct)
        {
            Directory.CreateDirectory(_folder);
            var declared = VodShareEncoderArgs.ReserveBytes(job.Quality, job.Fps, job.DurationSeconds);
            var began = Stopwatch.GetTimestamp();
            StartEncode(ct);

            VodHlsStart start;
            try
            {
                start = await worker._client.StartVodHlsUploadAsync(token, Request(job), declared, ct).ConfigureAwait(false);
            }
            catch (ClipUploadException ex) when (ex.Code == "hls_unavailable")
            {
                // Turned off since the status read: no daily start was spent, so today's MP4 path starts afresh.
                Log.LogInformation("Full-game share for game {GameId}: HLS is off now; sharing the finished copy instead", job.GameId);
                await StopEncodeAsync().ConfigureAwait(false);
                ClearFolder();
                job.Format = "mp4";
                var mp4 = await worker.EncodeAndUploadAsync(job, mp4File, token, assigned, ct).ConfigureAwait(false);
                return mp4 is { } done ? new HlsOutcome(done.Uploaded, done.Encoded.SizeBytes, false) : null;
            }
            catch
            {
                await StopEncodeAsync().ConfigureAwait(false);
                throw;
            }

            if (start.Mp4 is { } reserved)
            {
                // An older proxy reserved an MP4 upload: today's pipelined path on that row.
                await StopEncodeAsync().ConfigureAwait(false);
                ClearFolder();
                job.Format = "mp4";
                assigned(start.Started);
                var mp4 = await worker.EncodeAndUploadAsync(job, mp4File, token, assigned, ct, reserved).ConfigureAwait(false);
                return mp4 is { } done ? new HlsOutcome(done.Uploaded, done.Encoded.SizeBytes, false) : null;
            }

            _upload = start.Session!;
            _upload.Quality = job.Quality;
            _upload.Fps = job.Fps;
            _upload.Progress = OnProgress;
            assigned(start.Started);
            Log.LogInformation("HLS share {Id} for game {GameId} reserved {Ms:F0} ms after Share", start.Started.Id, job.GameId,
                Stopwatch.GetElapsedTime(began).TotalMilliseconds);
            _upload.Start(ct);
            try
            {
                return await LoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await SettleCancelledAsync().ConfigureAwait(false);
                throw;
            }
            catch (HlsStoppedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "HLS share {Id} for game {GameId} failed", _upload.Info.Id, job.GameId);
                await SettleFailedAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        // ── The loop ─────────────────────────────────────────────────────────

        private async Task<HlsOutcome?> LoopAsync(CancellationToken ct)
        {
            var upload = _upload!;
            Task? itemsReady = null;
            Task? tick = null;
            while (true)
            {
                Feed();
                if (_encode is { IsCompleted: true } encode) await SettleEncodeAsync(encode).ConfigureAwait(false);
                if (_final is not null && !_addingDone && _encode is null && _items.Reader.Count == 0)
                {
                    upload.CompleteAdding();
                    _addingDone = true;
                }

                var completion = upload.Completion;
                if (completion.IsCompleted)
                {
                    if (completion.IsCompletedSuccessfully) return await EndAsync(!_final!.Complete, ct).ConfigureAwait(false);
                    var failure = completion.Exception?.InnerException as VodHlsUploadException
                        ?? new VodHlsUploadException(VodHlsFailure.Network, "network", "The upload stopped.");
                    if (await RecoverAsync(failure, ct).ConfigureAwait(false) is { } outcome) return outcome;
                    continue;
                }

                if (worker._live.IsGameInProgress)
                {
                    if (!upload.State.Playable)
                    {
                        // Nothing anyone could watch yet: today's cancel.
                        Log.LogInformation("Full-game share for game {GameId} stopped: a game started", job.GameId);
                        worker.Cancel(job.GameId, "game_started");
                        ct.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        await PauseForGameAsync(ct).ConfigureAwait(false);
                        itemsReady = null;
                        tick = null;
                    }
                    continue;
                }

                if (ShouldSwitch())
                {
                    await SwitchTo720Async(fromFailure: false, ct).ConfigureAwait(false);
                    itemsReady = null;
                    tick = null;
                    continue;
                }

                itemsReady ??= _items.Reader.WaitToReadAsync(ct).AsTask();
                tick ??= Task.Delay(worker.GameCheckInterval, ct);
                var waits = new List<Task> { itemsReady, tick, completion };
                if (_encode is { } running) waits.Add(running);
                await Task.WhenAny(waits).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (itemsReady.IsCompleted) itemsReady = null;
                if (tick.IsCompleted) tick = null;
            }
        }

        /// <summary>Move what the encoder announced to the upload, and follow the edge on disk.</summary>
        private void Feed()
        {
            while (_items.Reader.TryRead(out var item))
            {
                switch (item)
                {
                    case HlsInit init:
                        _nextRun = Math.Max(_nextRun, init.Run + 1);
                        break;
                    case HlsSegment segment:
                        _nextSeq = Math.Max(_nextSeq, segment.Seq + 1);
                        _diskEnd = Math.Max(_diskEnd, segment.EndSeconds);
                        break;
                }
                _upload?.Add(item);
            }
        }

        private void StartEncode(CancellationToken ct)
        {
            _encodeStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var request = new VodHlsEncodeRequest(job.SourcePath, _folder, job.Quality, job.Fps, job.DurationSeconds, _nextRun,
                _nextSeq, _diskEnd, _upload?.Info.MaxRuns ?? 8);
            _final = null;
            _addingDone = false;
            _encode = worker._encoder.EncodeHlsAsync(request, _items.Writer, null, _encodeStop.Token);
        }

        private async Task StopEncodeAsync()
        {
            if (_encode is null) return;
            try { _encodeStop?.Cancel(); } catch (ObjectDisposedException) { }
            try { await _encode.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or VodEncodeException) { }
            catch (Exception ex) { Log.LogDebug(ex, "HLS encode ended with an error while stopping"); }
            _encode = null;
            _encodeStop?.Dispose();
            _encodeStop = null;
            Feed();
        }

        /// <summary>The encode ended by itself: complete, stopped at the edge, or with no working encoder at all.</summary>
        private async Task SettleEncodeAsync(Task<VodHlsEncodeResult> encode)
        {
            _encode = null;
            _encodeStop?.Dispose();
            _encodeStop = null;
            Feed();
            try
            {
                _final = await encode.ConfigureAwait(false);
            }
            catch (VodEncodeException) when (_upload!.State.Playable)
            {
                // Viewers have part of the game: it ends where the upload gets to.
                _final = new VodHlsEncodeResult(false, _nextRun, _nextSeq, _diskEnd, null, "no_encoder");
            }
            if (!_final.Complete)
                Log.LogWarning("HLS share encode for game {GameId} stopped at {At:F1} s ({Reason})", job.GameId, _final.EndSeconds,
                    _final.StopReason);
        }

        // ── Progress ─────────────────────────────────────────────────────────

        private void OnProgress(VodHlsUploadState state)
        {
            var percent = (int)Math.Clamp(Math.Floor(100 * state.AckedSeconds / Math.Max(1, job.DurationSeconds)), 0, 99);
            lock (worker._gate)
            {
                job.MediaSeconds = state.AckedSeconds;
                job.Watchable = state.Playable;
                job.UplinkSlow = state.IngestRatio is { } r && r < UplinkSlowRatio && state.Backlog > 0 && !job.Paused;
                if (state.IngestRatio is { } ingest && !job.Paused)
                {
                    _lastIngest = ingest;
                    if (state.AckedSeconds > 0) _bytesPerMediaSecond = state.AckedBytes / state.AckedSeconds;
                }
            }
            // Stored before the sheet hears it is watchable, so Stop sharing finds the link from then on.
            if (state.Playable && Interlocked.Exchange(ref _storedOnce, 1) == 0) StoreLink();
            worker.Report(job, "running", "uploading", percent);
        }

        /// <summary>
        /// The share is watchable: from now on a crash or a quit keeps it (the proxy ends it at the edge), so it is no
        /// longer queued for deletion and its link is stored, so Stop sharing works later.
        /// </summary>
        private void StoreLink()
        {
            var info = _upload!.Info;
            worker._cleanup.Remove(info.Id);
            lock (worker._gate) worker._storedAt[info.Id] = Stopwatch.GetTimestamp();
            worker._store.Put(new VodShareEntry(job.GameId, info.Id, info.Url, VodShareEncoderArgs.Wire(job.Quality), job.IncludeNotes,
                _upload.State.AckedBytes, worker.Now(), info.ShareExpiresAt, false));
            Log.LogInformation("HLS share {Id} for game {GameId} is watchable", info.Id, job.GameId);
        }

        // ── Pause for a game (plan 4.2) ──────────────────────────────────────

        private async Task PauseForGameAsync(CancellationToken ct)
        {
            var upload = _upload!;
            var unfinished = _final is null;
            Log.LogInformation("HLS share {Id} paused: a game started", upload.Info.Id);
            lock (worker._gate)
            {
                job.Paused = true;
                job.UplinkSlow = false;
            }
            worker.Report(job, "running", phase: null, percent: null);
            await StopEncodeAsync().ConfigureAwait(false);
            await upload.StopAsync().ConfigureAwait(false);
            try
            {
                await upload.PauseAsync("game", ct).ConfigureAwait(false);
            }
            catch (VodHlsUploadException ex) when (ex.Kind == VodHlsFailure.Stopped)
            {
                upload.Abandon();
                throw new HlsStoppedException();
            }
            catch (VodHlsUploadException ex)
            {
                // The page then reads the upload as stalled; the share still continues after the game.
                Log.LogInformation(ex, "HLS share {Id}: the pause could not be sent", upload.Info.Id);
            }

            long? clear = null;
            while (true)
            {
                await Task.Delay(worker.GameCheckInterval, ct).ConfigureAwait(false);
                if (worker._live.IsGameInProgress) clear = null;
                else if ((clear ??= Stopwatch.GetTimestamp()) is var since && Stopwatch.GetElapsedTime(since) >= worker.ResumeAfterNoGame) break;
            }

            Log.LogInformation("HLS share {Id} continues after the game ({Unfinished})", upload.Info.Id,
                unfinished ? "encoding the rest" : "uploading what is ready");
            lock (worker._gate) job.Paused = false;
            worker.Report(job, "running", phase: null, percent: null);
            upload.ResetIngest();
            upload.Start(ct);
            if (unfinished) StartEncode(ct);
        }

        // ── The 720p switch (plan 4.2) ───────────────────────────────────────

        private bool ShouldSwitch()
        {
            if (job.Quality != VodShareQuality.P1080 || job.Switched || job.Paused) return false;
            var s = _upload!.State;
            return s.Acked >= UplinkSwitchMinSegments && s.ActiveSeconds >= UplinkSwitchMinSeconds
                && s.IngestRatio is { } ratio && ratio < UplinkSwitchRatio
                && s.Acked > s.LastRunFrom
                && job.DurationSeconds - s.AckedSeconds > UplinkSwitchMinRemainingSeconds;
        }

        private async Task SwitchTo720Async(bool fromFailure, CancellationToken ct)
        {
            var upload = _upload!;
            job.Switched = true;
            await StopEncodeAsync().ConfigureAwait(false);
            var edge = fromFailure ? upload.State.Acked : await upload.DrainAsync(ct).ConfigureAwait(false);
            if (!fromFailure) await upload.StopAsync().ConfigureAwait(false);
            upload.DropFrom(edge);
            for (var seq = edge; seq < _nextSeq; seq++) TryDeleteFile(Fmp4Splitter.SegmentPath(_folder, seq));
            var state = upload.State;
            _nextSeq = edge;
            _diskEnd = state.AckedSeconds;
            _nextRun = state.RunsAccepted;
            Log.LogInformation("HLS share {Id} switches to 720p at {At:F1} s (segment {Edge}, run {Run}): {Why}", upload.Info.Id,
                _diskEnd, edge, _nextRun, fromFailure ? "over the reservation" : $"ingest {state.IngestRatio:F2}x");
            lock (worker._gate)
            {
                job.Quality = VodShareQuality.P720;
                job.QualityReason = "uplink";
                job.UplinkSlow = false;
            }
            upload.Quality = VodShareQuality.P720;
            upload.ResetIngest();
            upload.Start(ct);
            StartEncode(ct);
            worker.Report(job, "running", phase: null, percent: null);
        }

        // ── Endings ──────────────────────────────────────────────────────────

        /// <summary>A terminal answer (plan 4.7): switch once to 720p, or end at the edge, or fail. Null to continue.</summary>
        private async Task<HlsOutcome?> RecoverAsync(VodHlsUploadException failure, CancellationToken ct)
        {
            var upload = _upload!;
            var state = upload.State;
            Log.LogWarning("HLS share {Id} upload stopped: {Kind} ({Code}) at segment {Acked}", upload.Info.Id, failure.Kind,
                failure.Code, state.Acked);
            switch (failure.Kind)
            {
                case VodHlsFailure.Stopped:
                    upload.Abandon();
                    throw new HlsStoppedException();
                case VodHlsFailure.TooLarge when job.Quality == VodShareQuality.P1080 && !job.Switched && !state.CommitsPastEdge
                                                 && state.Acked > state.LastRunFrom && state.Playable:
                    await SwitchTo720Async(fromFailure: true, ct).ConfigureAwait(false);
                    return null;
                case VodHlsFailure.TooLarge or VodHlsFailure.Limited when state.Playable:
                    await StopEncodeAsync().ConfigureAwait(false);
                    return await EndAsync(truncated: true, ct).ConfigureAwait(false);
                case VodHlsFailure.Conflict:
                    await LogServerStateAsync(ct).ConfigureAwait(false);
                    throw failure;
                default:
                    throw failure;
            }
        }

        private async Task<HlsOutcome> EndAsync(bool truncated, CancellationToken ct)
        {
            var upload = _upload!;
            var acked = upload.State.Acked;
            if (acked == 0) throw new VodHlsUploadException(VodHlsFailure.Conflict, "nothing_uploaded", "No segment was uploaded.");
            var end = await upload.EndAsync(acked, truncated, ct).ConfigureAwait(false);
            _ended = true;
            return new HlsOutcome(new VodUploadResult(end.Id, end.Url, end.ExpiresAt), end.SizeBytes, end.Truncated);
        }

        /// <summary>
        /// Cancelled: by the player (Cancel, or a game before anything was watchable) the share is deleted; by Revu
        /// closing, a watchable share ends where its upload stopped (the proxy ends it later if that fails).
        /// </summary>
        private async Task SettleCancelledAsync()
        {
            await StopEncodeAsync().ConfigureAwait(false);
            var upload = _upload!;
            await upload.StopAsync().ConfigureAwait(false);
            var shutdown = worker._work.Stopping.IsCancellationRequested;
            var state = upload.State;
            if (shutdown && state.Playable && state.Acked > 0)
            {
                using var budget = new CancellationTokenSource(EndBudgetWhenStopping);
                try
                {
                    await upload.EndAsync(state.Acked, truncated: true, budget.Token).ConfigureAwait(false);
                    _ended = true;
                    Log.LogInformation("HLS share {Id} ended at segment {Acked} as Revu closes", upload.Info.Id, state.Acked);
                }
                catch (Exception ex) when (ex is VodHlsUploadException or OperationCanceledException or HttpRequestException)
                {
                    // Still watchable: the proxy ends it at the edge once it has been idle for 30 minutes.
                    Log.LogInformation(ex, "HLS share {Id} could not be ended as Revu closes", upload.Info.Id);
                    upload.Abandon();
                }
                return;
            }
            await DeleteAsync(shutdown).ConfigureAwait(false);
        }

        /// <summary>A failure: a watchable share ends at the edge (best effort), anything else is deleted.</summary>
        private async Task SettleFailedAsync(CancellationToken ct)
        {
            await StopEncodeAsync().ConfigureAwait(false);
            var upload = _upload!;
            await upload.StopAsync().ConfigureAwait(false);
            var state = upload.State;
            if (_ended) return;
            if (state.Playable && state.Acked > 0)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await upload.EndAsync(state.Acked, truncated: true, budget.Token).ConfigureAwait(false);
                    _ended = true;
                }
                catch (Exception ex) when (ex is VodHlsUploadException or OperationCanceledException or HttpRequestException)
                {
                    Log.LogInformation(ex, "HLS share {Id} could not be ended after the failure", upload.Info.Id);
                    upload.Abandon();
                }
                return;
            }
            await DeleteAsync(worker._work.Stopping.IsCancellationRequested).ConfigureAwait(false);
        }

        private async Task DeleteAsync(bool shutdown)
        {
            var upload = _upload!;
            var id = upload.Info.Id;
            var gone = false;
            using var budget = new CancellationBudget(worker._work.Stopping, DropBudgetWhenStopping);
            try { gone = await upload.DeleteAsync(budget.Token).ConfigureAwait(false); }
            catch (Exception ex) { Log.LogDebug(ex, "HLS share {Id}: remote delete failed", id); }
            if (!gone) upload.Abandon();
            worker.RemoveStored(job.GameId, id);
            if (gone) worker._cleanup.Remove(id);
            else worker._cleanup.Add(id);
            Log.LogInformation("HLS share {Id} for game {GameId}: remote deleted {Deleted}{Shutdown}", id, job.GameId, gone,
                shutdown ? " as Revu closes" : "");
        }

        /// <summary>A protocol conflict: log what the proxy holds for this share, for the bug report.</summary>
        private async Task LogServerStateAsync(CancellationToken ct)
        {
            try
            {
                var status = await worker._client.GetVodShareStatusAsync(token, ct).ConfigureAwait(false);
                var share = status.Shares.FirstOrDefault(s => s.Id == _upload!.Info.Id);
                Log.LogWarning("HLS share {Id} conflict: the proxy has {Status}, {Segments} segments, paused {Paused}; "
                    + "this PC acknowledged {Acked}", _upload!.Info.Id, share?.Status ?? "nothing", share?.Segments, share?.Paused,
                    _upload.State.Acked);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogDebug(ex, "HLS share status read after a conflict failed");
            }
        }

        private void ClearFolder()
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.LogDebug(ex, "Share folder cleanup failed"); }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await StopEncodeAsync().ConfigureAwait(false);
            if (_upload is { } upload)
            {
                // Whatever this job did not settle itself is settled here: ended at the edge if watchable, else deleted.
                await upload.DisposeAsync().ConfigureAwait(false);
                await RememberUplinkAsync().ConfigureAwait(false);
            }
            ClearFolder();
        }

        /// <summary>
        /// Remember how fast this account's upload went (the last ingest ratio, as bytes per second of the media it
        /// carried), so a slow uplink gets 720p preselected next time. The ratio is the encoder's speed when the
        /// encoder was the slower one, which never preselects anything.
        /// </summary>
        private async Task RememberUplinkAsync()
        {
            double? ratio;
            double bytesPerMediaSecond;
            lock (worker._gate)
            {
                ratio = _lastIngest;
                bytesPerMediaSecond = _bytesPerMediaSecond;
            }
            if (worker._memory is not { } memory || ratio is not { } r || bytesPerMediaSecond <= 0) return;
            try
            {
                memory.SetUplink(await worker.AccountKeyAsync().ConfigureAwait(false), r * bytesPerMediaSecond);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.LogDebug(ex, "Upload speed not remembered");
            }
        }
    }
}
