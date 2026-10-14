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

            var encoded = await EncodeAsync(job, file, ct).ConfigureAwait(false);
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
                return;
            }

            var session = await ClipShareLinks.SessionAsync(_config).ConfigureAwait(false);
            if (!session.SignedIn)
            {
                Fail(job, "needs_login", needsLogin: true);
                return;
            }
            token = session.Token;

            Report(job, "running", "uploading", 0);
            long createdAt = 0;
            var progress = new InlineProgress<ClipUploadProgress>(p =>
            {
                if (p.Phase == "finishing") Report(job, "running", "finishing", 100);
                else Report(job, "running", "uploading", Percent(p.SentBytes, p.TotalBytes));
            });
            uploaded = await _client.UploadVodAsync(file, token,
                new VodUploadRequest(VodShareEncoderArgs.Wire(job.Quality), (int)Math.Ceiling(job.DurationSeconds)),
                progress, id =>
                {
                    createdAt = Now();
                    _cleanup.Add(id);
                }, ct).ConfigureAwait(false);
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

    private async Task<VodEncodeResult> EncodeAsync(VodShareJob job, string file, CancellationToken ct)
    {
        var progress = new InlineProgress<int>(p => Report(job, "running", "preparing", Math.Clamp(p, 0, 99)));
        return await _encoder.EncodeAsync(job.SourcePath, file, job.Quality, job.DurationSeconds, progress, ct)
            .ConfigureAwait(false);
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
