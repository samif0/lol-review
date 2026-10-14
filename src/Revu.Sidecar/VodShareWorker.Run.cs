#nullable enable

using System.Diagnostics;
using Revu.Core.Constants;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>One full-game share job, start to finish (contract 7.5 job steps).</summary>
public sealed partial class VodShareWorker
{
    /// <summary>How long a remote drop may take once the host is stopping (the id stays queued).</summary>
    internal static readonly TimeSpan DropBudgetWhenStopping = TimeSpan.FromSeconds(2);

    internal async Task RunJobAsync(VodShareJob job)
    {
        var ct = job.Cts.Token;
        string? file = null;
        VodUploadResult? uploaded = null;
        var stored = false;
        var token = "";
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = WatchForGameAsync(job, watch.Token);
        try
        {
            ct.ThrowIfCancellationRequested();
            Report(job, "running", "preparing", 0);
            Directory.CreateDirectory(TempDirectory);
            file = Path.Combine(TempDirectory, $"game-{job.GameId}-{Guid.NewGuid():N}.mp4");

            // The upload starts with the encode, so the session is needed first.
            var session = await ClipShareLinks.SessionAsync(_config).ConfigureAwait(false);
            if (!session.SignedIn)
            {
                Fail(job, "needs_login", needsLogin: true);
                return;
            }
            token = session.Token;

            long createdAt = 0;
            void Assigned(string id)
            {
                createdAt = Now();
                _cleanup.Add(id);
            }
            if (await EncodeAndUploadAsync(job, file, token, Assigned, ct).ConfigureAwait(false) is not { } done) return;
            var encoded = done.Encoded;
            uploaded = done.Uploaded;
            // From here a game starting no longer matters: the upload is done.
            watch.Cancel();
            ct.ThrowIfCancellationRequested();

            var entry = new VodShareEntry(job.GameId, uploaded.Id, uploaded.Url, VodShareEncoderArgs.Wire(job.Quality),
                job.IncludeNotes, encoded.SizeBytes, createdAt > 0 ? createdAt : Now(), uploaded.ExpiresAt, false);
            lock (_gate) _storedAt[uploaded.Id] = Stopwatch.GetTimestamp();
            _store.Put(entry);
            stored = true;
            _cleanup.Remove(uploaded.Id);
            _logger.LogInformation("Full-game share stored: game {GameId} -> {Url}", job.GameId, uploaded.Url);

            Report(job, "running", "finishing", 100);
            var attached = await AttachMarkersAsync(job, uploaded.Id, token, ct).ConfigureAwait(false);
            if (attached) _store.Put(entry with { MarkersAttached = true });
            ct.ThrowIfCancellationRequested();
            Report(job, "done", "done", 100, uploaded.Url, uploaded.ExpiresAt, attached);
        }
        catch (VodEncodeException ex)
        {
            Fail(job, ex.Code == "too_long" ? "too_long" : "encode_failed");
        }
        catch (ClipUploadException ex)
        {
            var (code, needsLogin) = MapUploadError(ex);
            if (needsLogin)
                _logger.LogInformation("Full-game share: proxy returned unauthorized; prompting re-login without clearing the session.");
            else
                _logger.LogWarning(ex, "Full-game share failed for game {GameId} ({Code})", job.GameId, ex.Code);
            if (uploaded is not null && !stored) await DropRemoteAsync(uploaded.Id, token).ConfigureAwait(false);
            Fail(job, code, ex.RetryAt, needsLogin);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var shutdown = _work.Stopping.IsCancellationRequested;
            if (uploaded is not null && (!stored || !shutdown))
            {
                // A user cancel removes even a stored link (they asked for no share); a shutdown
                // keeps a finished share and only drops a copy that was never recorded.
                if (stored) RemoveStored(job.GameId, uploaded.Id);
                await DropRemoteAsync(uploaded.Id, token).ConfigureAwait(false);
            }
            if (!shutdown)
            {
                string? code;
                lock (_gate) code = job.CancelCode;
                if (code == "game_started") Fail(job, "game_started");
                else Report(job, "cancelled", "cancelled", job.Percent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Full-game share failed for game {GameId} in {Phase}", job.GameId, job.Phase);
            if (uploaded is not null && !stored) await DropRemoteAsync(uploaded.Id, token).ConfigureAwait(false);
            Fail(job, job.Phase == "preparing" ? "encode_failed" : "upload_failed");
        }
        finally
        {
            watch.Cancel();
            try { await watcher.ConfigureAwait(false); } catch { /* watcher ends quietly */ }
            if (file is not null)
            {
                TryDelete(file);
                TryDelete(file + ".tmp");
            }
            Retire(job);
        }
    }

    /// <summary>
    /// Encode the share copy and upload it at the same time (4.3). The upload reserves the
    /// copy's expected size plus 3% (<see cref="VodShareEncoderArgs.ReserveBytes"/>) and sends
    /// each 16 MiB part as soon as the encoder has written past it. Once encoded, the copy is
    /// padded to exactly that size and the header and last parts follow, so the link is ready
    /// moments after the encode instead of a whole upload later. A copy that came out larger,
    /// or an encode that restarted on another encoder, gives that upload up (its remote copy is
    /// deleted) and the finished file is uploaded on its own. Null when the job already failed
    /// with a reported code.
    /// </summary>
    private async Task<(VodEncodeResult Encoded, VodUploadResult Uploaded)?> EncodeAndUploadAsync(VodShareJob job,
        string file, string token, Action<string> assigned, CancellationToken ct)
    {
        var declared = VodShareEncoderArgs.ReserveBytes(job.Quality, job.Fps, job.DurationSeconds);
        if (declared <= 0 || declared > GameConstants.MaxVodShareBytes)
            return await EncodeThenUploadAsync(job, file, token, assigned, ct).ConfigureAwait(false);

        var written = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var showUpload = 0;
        long lastSent = 0;
        var progress = new InlineProgress<ClipUploadProgress>(p =>
        {
            Interlocked.Exchange(ref lastSent, p.SentBytes);
            // While the encode runs the sheet shows its percent; the upload's shows from then on.
            if (Volatile.Read(ref showUpload) == 1) ReportUpload(job, p);
        });
        using var encodeStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var upload = _client.UploadVodWhileWritingAsync(file, declared, written.Task, token, Request(job), progress,
            assigned, ct);
        // An upload refused at init, or failed for good, stops the encode: there is nowhere to send it.
        _ = upload.ContinueWith(t =>
        {
            if (t.Exception?.InnerException is VodCopyAbandonedException) return;
            try { encodeStop.Cancel(); } catch (ObjectDisposedException) { }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var pipelined = true;
        async Task GiveUpAsync()
        {
            if (!pipelined) return;
            pipelined = false;
            written.TrySetResult(false);
            try { await upload.ConfigureAwait(false); }
            catch (Exception ex) when (ex is VodCopyAbandonedException or OperationCanceledException or ClipUploadException)
            {
                // Given up on purpose; the remote copy was deleted (or queued for cleanup).
            }
        }

        VodEncodeResult encoded;
        try
        {
            var encodeProgress = new InlineProgress<int>(p => Report(job, "running", "preparing", Math.Clamp(p, 0, 99)));
            encoded = await _encoder.EncodeAsync(job.SourcePath, file, job.Quality, job.Fps, job.DurationSeconds,
                encodeProgress, encodeStop.Token, beforeRetry: _ => GiveUpAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && upload.IsFaulted)
        {
            // The upload failed first and stopped the encode: its error is the one to report.
            written.TrySetResult(false);
            await upload.ConfigureAwait(false);
            throw;
        }
        catch
        {
            await GiveUpAsync().ConfigureAwait(false);
            throw;
        }

        if (pipelined)
        {
            if (encoded.SizeBytes <= declared && PadCopy(file, declared))
            {
                Volatile.Write(ref showUpload, 1);
                ReportUpload(job, new ClipUploadProgress("uploading", Interlocked.Read(ref lastSent), declared));
                written.TrySetResult(true);
                return (encoded, await upload.ConfigureAwait(false));
            }
            _logger.LogInformation("Full-game share copy for game {GameId} is {Bytes} bytes, past the {Declared} reserved; "
                + "uploading it on its own", job.GameId, encoded.SizeBytes, declared);
            await GiveUpAsync().ConfigureAwait(false);
        }
        return await UploadFinishedAsync(job, file, token, assigned, encoded, ct).ConfigureAwait(false);
    }

    /// <summary>Encode, then upload the finished copy (the path when the size can't be reserved up front).</summary>
    private async Task<(VodEncodeResult Encoded, VodUploadResult Uploaded)?> EncodeThenUploadAsync(VodShareJob job,
        string file, string token, Action<string> assigned, CancellationToken ct)
    {
        var encoded = await EncodeAsync(job, file, ct).ConfigureAwait(false);
        return await UploadFinishedAsync(job, file, token, assigned, encoded, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Upload an encoded copy on its own. An oversized 1080p copy is prepared again at 720p
    /// first; one still over 2 GiB fails as too_large (null).
    /// </summary>
    private async Task<(VodEncodeResult Encoded, VodUploadResult Uploaded)?> UploadFinishedAsync(VodShareJob job,
        string file, string token, Action<string> assigned, VodEncodeResult encoded, CancellationToken ct)
    {
        if (encoded.SizeBytes > GameConstants.MaxVodShareBytes && job.Quality == VodShareQuality.P1080)
        {
            _logger.LogInformation("Full-game share copy for game {GameId} is {Bytes} bytes; preparing 720p", job.GameId,
                encoded.SizeBytes);
            TryDelete(file);
            job.Quality = VodShareQuality.P720;
            job.QualityReason = "size";
            Report(job, "running", "preparing", 0);
            encoded = await EncodeAsync(job, file, ct).ConfigureAwait(false);
        }
        if (encoded.SizeBytes > GameConstants.MaxVodShareBytes)
        {
            Fail(job, "too_large");
            return null;
        }

        Report(job, "running", "uploading", 0);
        var progress = new InlineProgress<ClipUploadProgress>(p => ReportUpload(job, p));
        var uploaded = await _client.UploadVodAsync(file, token, Request(job), progress, assigned, ct).ConfigureAwait(false);
        return (encoded, uploaded);
    }

    private async Task<VodEncodeResult> EncodeAsync(VodShareJob job, string file, CancellationToken ct)
    {
        var progress = new InlineProgress<int>(p => Report(job, "running", "preparing", Math.Clamp(p, 0, 99)));
        return await _encoder.EncodeAsync(job.SourcePath, file, job.Quality, job.Fps, job.DurationSeconds, progress, ct)
            .ConfigureAwait(false);
    }

    private static VodUploadRequest Request(VodShareJob job) =>
        new(VodShareEncoderArgs.Wire(job.Quality), (int)Math.Ceiling(job.DurationSeconds), job.Fps);

    private void ReportUpload(VodShareJob job, ClipUploadProgress p)
    {
        if (p.Phase == "finishing") Report(job, "running", "finishing", 100);
        else Report(job, "running", "uploading", Percent(p.SentBytes, p.TotalBytes));
    }

    /// <summary>Build and PUT the markers document; 3 attempts. False leaves the share without markers.</summary>
    private async Task<bool> AttachMarkersAsync(VodShareJob job, string id, string token, CancellationToken ct)
    {
        string json;
        try
        {
            var vod = await LoadSnapshotAsync(job.GameId, ct).ConfigureAwait(false);
            Revu.Core.Models.GameStats? game = null;
            try { game = await _games.GetAsync(job.GameId).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Full-game share: game {GameId} lookup failed", job.GameId); }
            var built = VodShareMarkers.Build(vod, game, job.IncludeNotes);
            if (built.MarkersDropped > 0 || built.NotesDropped > 0)
            {
                _logger.LogInformation("Full-game share markers for game {GameId}: dropped {Markers} marker(s) and {Notes} note(s) to fit",
                    job.GameId, built.MarkersDropped, built.NotesDropped);
            }
            json = built.Json;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Full-game share markers for game {GameId} could not be built", job.GameId);
            return false;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _client.PutVodMarkersAsync(id, token, json, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var retry = attempt <= MarkerRetryDelays.Length && (ex is not ClipUploadException c || c.Code == "network");
                _logger.LogInformation(ex, "Full-game share markers for {Id}: attempt {Attempt} failed", id, attempt);
                if (!retry) return false;
            }
            await Task.Delay(MarkerRetryDelays[attempt - 1], ct).ConfigureAwait(false);
        }
    }

    /// <summary>Every <see cref="GameCheckInterval"/> while preparing or uploading: a game start cancels the share.</summary>
    private async Task WatchForGameAsync(VodShareJob job, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(GameCheckInterval, ct).ConfigureAwait(false);
                if (!_live.IsGameInProgress) continue;
                _logger.LogInformation("Full-game share for game {GameId} stopped: a game started", job.GameId);
                Cancel(job.GameId, "game_started");
                return;
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static (string Code, bool NeedsLogin) MapUploadError(ClipUploadException ex)
    {
        if (ex.Unauthorized || ex.Code == "unauthorized") return ("needs_login", true);
        return (ex.Code switch
        {
            "not_allowed" => "not_allowed",
            "vod_sharing_disabled" => "sharing_paused",
            "pool_full" => "pool_full",
            "live_limit" => "live_limit",
            "daily_limit" => "daily_limit",
            "too_many_pending_uploads" or "rate_limited" => "busy",
            "payload_too_large" => "too_large",
            "network" or "timeout" => "network",
            _ => "upload_failed",
        }, false);
    }

    private void RemoveStored(long gameId, string id)
    {
        if (_store.Get(gameId) is { } current && string.Equals(current.Id, id, StringComparison.Ordinal))
            _store.Remove(gameId);
    }

    private async Task DropRemoteAsync(string id, string token)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var gone = false;
        // Not bound to the job's token (usually cancelled here), but cut short once the host is
        // stopping so a stalled delete cannot push shutdown past its drain window.
        using var budget = new CancellationBudget(_work.Stopping, DropBudgetWhenStopping);
        try { gone = await _clips.DeleteAsync(id, token, budget.Token).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Full-game share: remote delete of {Id} failed", id); }
        if (gone) _cleanup.Remove(id);
        else _cleanup.Add(id);
    }

    private void Retire(VodShareJob job)
    {
        lock (_gate)
        {
            if (job.Status is { } last) _last[job.GameId] = last;
            if (ReferenceEquals(_running, job)) _running = null;
        }
        job.Finished.TrySetResult();
        job.Cts.Dispose();
    }

    // ── Publishing ───────────────────────────────────────────────────────────

    private void Fail(VodShareJob job, string code, long? retryAt = null, bool needsLogin = false) =>
        Report(job, "error", "error", job.Percent, errorCode: code, error: Message(code), retryAt: retryAt,
            needsLogin: needsLogin);

    private void Report(VodShareJob job, string state, string phase, int percent, string? url = null,
        long? expiresAt = null, bool? markersAttached = null, string? errorCode = null, string? error = null,
        long? retryAt = null, bool needsLogin = false)
    {
        var quality = VodShareEncoderArgs.Wire(job.Quality);
        var now = Stopwatch.GetTimestamp();
        bool send;
        lock (_gate)
        {
            job.Phase = phase;
            job.Percent = percent;
            job.Status = new VodShareJobDto(state, phase, percent, quality, job.QualityReason, errorCode, error, retryAt,
                needsLogin);
            // Percent changes within one phase are throttled; a phase change always goes out.
            send = phase != job.LastPhase || state != "running" || now - job.LastPublish >= ThrottleTicks;
            if (send)
            {
                job.LastPhase = phase;
                job.LastPublish = now;
            }
        }
        if (!send) return;
        _hub.Publish("vodShareProgress", new
        {
            gameId = job.GameId,
            state,
            phase,
            percent,
            quality,
            qualityReason = job.QualityReason,
            url,
            expiresAt,
            markersAttached,
            errorCode,
            error,
            retryAt,
            needsLogin,
        });
    }

    private static int Percent(long sent, long total) =>
        total <= 0 ? 0 : (int)Math.Clamp(Math.Floor(sent * 100.0 / total), 0, 100);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; posts asynchronously and can reorder).</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
