#nullable enable

using System.Threading.Channels;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Automatic narration transcripts. A deduplicating queue of bookmark ids; each item runs
/// through <see cref="SidecarBackgroundWork.TryRun"/> (never the forever loop itself), with
/// <see cref="SidecarBackgroundWork.Stopping"/> linked into ffmpeg, HTTP and delays. Every
/// transcript write is compare-and-set on <c>transcript_generation</c>, so a stale run can
/// never overwrite a newer narration, and a run stopped by shutdown puts its row back to
/// <c>pending</c> for the next start.
/// </summary>
public sealed class NarrationTranscriptionWorker
{
    internal const string QuotaMessage = "Daily transcript limit reached. Try again tomorrow.";
    internal const string FailedMessage = "Transcript failed. Try again.";
    /// <summary>Stored error for <see cref="TranscriptionErrorKind.Unavailable"/>; startup re-queues these.</summary>
    internal const string UnavailableMessage = "Transcripts are not available yet. Revu will try again later.";

    private readonly IClipNarrationRepository _narrations;
    private readonly IVodRepository _vod;
    private readonly IConfigService _config;
    private readonly ITranscriptionClient _client;
    private readonly INarrationTranscriptChunker _chunker;
    private readonly INarrationMixer _mixer;
    private readonly IClipUploadService _clips;
    private readonly Func<Task> _ensureBackedUp;
    private readonly SidecarEventHub _hub;
    private readonly SidecarBackgroundWork _work;
    private readonly ILogger<NarrationTranscriptionWorker> _logger;

    private readonly Channel<long> _queue = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _gate = new();
    private readonly HashSet<long> _queued = new();
    private int _started;

    public NarrationTranscriptionWorker(
        IClipNarrationRepository narrations,
        IVodRepository vod,
        IConfigService config,
        ITranscriptionClient client,
        INarrationTranscriptChunker chunker,
        INarrationMixer mixer,
        IClipUploadService clips,
        Func<Task> ensureBackedUp,
        SidecarEventHub hub,
        SidecarBackgroundWork work,
        ILogger<NarrationTranscriptionWorker> logger)
    {
        _narrations = narrations;
        _vod = vod;
        _config = config;
        _client = client;
        _chunker = chunker;
        _mixer = mixer;
        _clips = clips;
        _ensureBackedUp = ensureBackedUp;
        _hub = hub;
        _work = work;
        _logger = logger;
    }

    /// <summary>A new narration waits this long after its last change (a quick re-record wins).</summary>
    internal TimeSpan Settle { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delays before the two retries of a transient chunk failure.</summary>
    internal TimeSpan[] TransientRetryDelays { get; set; } = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];

    /// <summary>Start the dispatcher (idempotent). Only the non-isolated host calls this.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(() => DispatchAsync(_work.Stopping));
    }

    /// <summary>Queue a bookmark's narration for transcription (deduplicated while queued).</summary>
    public void Enqueue(long bookmarkId)
    {
        lock (_gate)
        {
            if (!_queued.Add(bookmarkId)) return;
        }
        _queue.Writer.TryWrite(bookmarkId);
    }

    private async Task DispatchAsync(CancellationToken stopping)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stopping).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var id))
                {
                    lock (_gate) _queued.Remove(id);
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_work.TryRun("narration-transcribe", async () =>
                        {
                            try { await ProcessAsync(id, _work.Stopping).ConfigureAwait(false); }
                            finally { completion.TrySetResult(); }
                        }))
                    {
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
            _logger.LogError(ex, "Narration transcription dispatcher stopped unexpectedly");
        }
    }

    /// <summary>Transcribe one narration (C6 worker steps 1 to 10).</summary>
    internal async Task ProcessAsync(long bookmarkId, CancellationToken ct)
    {
        var row = await _narrations.GetAsync(bookmarkId).ConfigureAwait(false);
        if (row is null || row.TranscriptStatus != TranscriptStatuses.Pending) return;

        // Settle: let a quick re-record land before spending a transcript on this take.
        var settleUntil = DateTimeOffset.FromUnixTimeSeconds(row.UpdatedAt) + Settle;
        var wait = settleUntil - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
            row = await _narrations.GetAsync(bookmarkId).ConfigureAwait(false);
            if (row is null || row.TranscriptStatus != TranscriptStatuses.Pending) return;
        }

        var session = await ClipShareLinks.SessionAsync(_config).ConfigureAwait(false);
        // Both writes below can be the session's first: back up before either.
        await _ensureBackedUp().ConfigureAwait(false);
        if (!session.SignedIn)
        {
            await _narrations.ResetTranscriptAsync(bookmarkId, TranscriptStatuses.NeedsLogin).ConfigureAwait(false);
            Publish(row, TranscriptStatuses.NeedsLogin, 0, 0);
            return;
        }

        var gen = row.TranscriptGeneration;
        if (!await _narrations.TryClaimTranscriptAsync(bookmarkId, gen).ConfigureAwait(false)) return;
        Publish(row, TranscriptStatuses.Processing, 0, 0);

        var temp = Path.Combine(Path.GetTempPath(), "revu-transcribe-" + Guid.NewGuid().ToString("N"));
        var finalStatus = TranscriptStatuses.Processing;
        int done = 0, total = 0;
        try
        {
            Directory.CreateDirectory(temp);
            var chunks = await _chunker.ExportChunksAsync(row.AudioPath, row.DurationMs / 1000.0, temp, ct)
                .ConfigureAwait(false);
            var speech = chunks.Where(c => !c.Silent).ToList();
            total = speech.Count;
            Publish(row, TranscriptStatuses.Processing, 0, total);

            var segments = new List<TranscriptSegment>();
            var language = "";
            foreach (var chunk in speech)
            {
                var bytes = await File.ReadAllBytesAsync(chunk.Path, ct).ConfigureAwait(false);
                var offsetMs = (int)Math.Round(chunk.StartS * 1000) + row.OffsetMs;
                var durationMs = (int)Math.Round((chunk.EndS - chunk.StartS) * 1000);
                var result = await TranscribeWithRetryAsync(bytes, offsetMs, durationMs, session.Token, ct)
                    .ConfigureAwait(false);
                segments.AddRange(result.Segments);
                if (language.Length == 0 && !string.IsNullOrWhiteSpace(result.Language)) language = result.Language;
                done++;
                Publish(row, TranscriptStatuses.Processing, done, total);
            }

            var clipDuration = await ClipDurationAsync(row, ct).ConfigureAwait(false);
            var doc = TranscriptDocument.Normalize(segments, language.Length > 0 ? language : "en", clipDuration);

            await _ensureBackedUp().ConfigureAwait(false);
            if (!await _narrations.TrySetTranscriptAsync(bookmarkId, gen, TranscriptStatuses.Ready, doc.Language,
                    doc.ToJson(), "").ConfigureAwait(false))
            {
                // Stale: the narration changed while this run worked. Push nothing.
                _logger.LogInformation("Transcript for bm {BookmarkId} is stale (generation {Gen}); discarded", bookmarkId, gen);
                return;
            }
            finalStatus = TranscriptStatuses.Ready;
            await PushToShareAsync(bookmarkId, row.GameId, doc, session.Token, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: give the row back to the next start, only if it is still ours.
            await TrySetQuietlyAsync(bookmarkId, gen, TranscriptStatuses.Pending, "").ConfigureAwait(false);
            finalStatus = TranscriptStatuses.Pending;
        }
        catch (TranscriptionException ex)
        {
            (finalStatus, var error) = ex.Kind switch
            {
                TranscriptionErrorKind.NeedsLogin => (TranscriptStatuses.NeedsLogin, ""),
                TranscriptionErrorKind.Quota => (TranscriptStatuses.Quota, QuotaMessage),
                TranscriptionErrorKind.Unavailable => (TranscriptStatuses.Failed, UnavailableMessage),
                _ => (TranscriptStatuses.Failed, FailedMessage),
            };
            _logger.LogWarning(ex, "Transcript for bm {BookmarkId} ended as {Status}", bookmarkId, finalStatus);
            if (!await TrySetQuietlyAsync(bookmarkId, gen, finalStatus, error).ConfigureAwait(false)) return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Transcript for bm {BookmarkId} failed", bookmarkId);
            finalStatus = TranscriptStatuses.Failed;
            if (!await TrySetQuietlyAsync(bookmarkId, gen, finalStatus, FailedMessage).ConfigureAwait(false)) return;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {Temp}", temp); }
        }

        Publish(row, finalStatus, done, total);
    }

    private async Task<TranscriptionChunkResult> TranscribeWithRetryAsync(byte[] mp3, int offsetMs, int durationMs,
        string token, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _client.TranscribeChunkAsync(mp3, offsetMs, durationMs, "en", token, ct).ConfigureAwait(false);
            }
            catch (TranscriptionException ex) when (ex.Kind == TranscriptionErrorKind.Transient
                                                     && attempt < TransientRetryDelays.Length)
            {
                _logger.LogInformation(ex, "Transcribe chunk failed transiently; retrying");
                await Task.Delay(TransientRetryDelays[attempt], ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The narrated render's length, then the source clip's, then the bookmark window + 2 s.</summary>
    private async Task<double> ClipDurationAsync(ClipNarrationRecord row, CancellationToken ct)
    {
        foreach (var path in new[] { row.NarratedClipPath, row.SourceClipPath })
        {
            if (NarrationDtos.FileOnDisk(path).Length == 0) continue;
            try
            {
                var probe = await _mixer.ProbeAsync(path, ct).ConfigureAwait(false);
                if (probe?.DurationSeconds is { } d && d > 0) return d;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "Duration probe failed for {Path}", path); }
        }
        try
        {
            var bookmark = (await _vod.GetBookmarksAsync(row.GameId).ConfigureAwait(false))
                .FirstOrDefault(b => b.Id == row.BookmarkId);
            if (bookmark?.ClipStartSeconds is { } s && bookmark.ClipEndSeconds is { } e && e > s) return e - s + 2;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Bookmark lookup failed for {Id}", row.BookmarkId); }
        return (row.DurationMs + Math.Max(0, row.OffsetMs)) / 1000.0;
    }

    private async Task PushToShareAsync(long bookmarkId, long gameId, TranscriptDocument doc, string token, CancellationToken ct)
    {
        try
        {
            var bookmark = (await _vod.GetBookmarksAsync(gameId).ConfigureAwait(false)).FirstOrDefault(b => b.Id == bookmarkId);
            var slug = ClipShareLinks.SlugFromUrl(bookmark?.ShareUrl);
            if (slug.Length == 0) return;
            await _clips.PutTranscriptAsync(slug, token, doc, ct).ConfigureAwait(false);
            await _narrations.SetTranscriptPushedSlugAsync(bookmarkId, slug).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Transcript push to the shared clip failed for bm {BookmarkId}", bookmarkId);
        }
    }

    private async Task<bool> TrySetQuietlyAsync(long bookmarkId, long gen, string status, string error)
    {
        try
        {
            return await _narrations.TrySetTranscriptAsync(bookmarkId, gen, status, "", "", error).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record transcript status {Status} for bm {BookmarkId}", status, bookmarkId);
            return false;
        }
    }

    private void Publish(ClipNarrationRecord row, string status, int chunksDone, int chunksTotal) =>
        _hub.Publish("clipNarrationUpdated", new
        {
            gameId = row.GameId,
            bookmarkId = row.BookmarkId,
            transcriptStatus = status,
            chunksDone,
            chunksTotal,
        });
}
