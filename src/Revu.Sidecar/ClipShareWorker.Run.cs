#nullable enable

using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>One share job, start to finish (C6 6.2 job steps).</summary>
public sealed partial class ClipShareWorker
{
    internal const string NarrationChangedMessage = "Narration changed. Share again to update the link.";
    private const string MissingFileMessage = "Clip file is missing. Save the clip again.";
    private const string GenericFailureMessage = "Couldn't upload the clip. Try again.";

    internal async Task RunJobAsync(ShareJob job)
    {
        lock (_gate) job.Started = true;
        var ct = job.Cts.Token;
        IDisposable? pin = null;
        ClipUploadResult? uploaded = null;
        var stored = false;
        var token = "";
        try
        {
            ct.ThrowIfCancellationRequested();
            Publish(job, "preparing");

            var bookmark = (await _vod.GetBookmarksAsync(job.GameId).ConfigureAwait(false))
                .FirstOrDefault(b => b.Id == job.BookmarkId);
            if (bookmark is null)
            {
                PublishError(job, "Clip not found.", retryable: false, needsLogin: false);
                return;
            }

            // The narrated render when it is on disk, else the source clip (which may be gone
            // when a narrated render exists: eviction never touches renders).
            var narration = await _narrations.GetAsync(job.BookmarkId).ConfigureAwait(false);
            var startNarratedPath = narration?.NarratedClipPath ?? "";
            var narratedOnDisk = NarrationDtos.FileOnDisk(startNarratedPath);
            string file;
            if (narratedOnDisk.Length > 0) file = narratedOnDisk;
            else if (NarrationDtos.FileOnDisk(bookmark.ClipPath).Length > 0) file = bookmark.ClipPath;
            else
            {
                PublishError(job, MissingFileMessage, retryable: false, needsLogin: false);
                return;
            }
            job.Narrated = narratedOnDisk.Length > 0;
            pin = _retention.Pin(file);

            var duration = await ResolveDurationAsync(bookmark, file, ct).ConfigureAwait(false);
            var session = await ClipShareLinks.SessionAsync(_config).ConfigureAwait(false);
            if (!session.SignedIn)
            {
                PublishError(job, "You need to be logged in to share clips.", retryable: false, needsLogin: true);
                return;
            }
            token = session.Token;

            var size = new FileInfo(file).Length;
            Publish(job, "uploading", 0, size);
            var progress = new InlineProgress<ClipUploadProgress>(p => Publish(job, p.Phase, p.SentBytes, p.TotalBytes));
            uploaded = await _clips.UploadAsync(file, token, job.Title, job.Champion, duration, progress,
                narrated: job.Narrated, onRemoteIdAssigned: id => _cleanup.Add(id), ct: ct).ConfigureAwait(false);

            // The narration (or its presence) changed during the upload: the remote copy is
            // stale. Drop it and make the user share again.
            var after = await _narrations.GetAsync(job.BookmarkId).ConfigureAwait(false);
            if (!SamePath(after?.NarratedClipPath ?? "", startNarratedPath))
            {
                await DropRemoteAsync(uploaded.Id, token).ConfigureAwait(false);
                uploaded = null;
                PublishError(job, NarrationChangedMessage, retryable: false, needsLogin: false);
                return;
            }

            ct.ThrowIfCancellationRequested();
            await _ensureBackedUp().ConfigureAwait(false);
            var linked = await _vod.SetBookmarkShareUrlAsync(job.BookmarkId, uploaded.Url).ConfigureAwait(false);
            if (!linked || (ct.IsCancellationRequested && !_work.Stopping.IsCancellationRequested))
            {
                // Deleted, or cancelled by a clip delete or narration change, between the check
                // above and the store: that caller may have read an empty link, so nobody else
                // will remove this copy. Drop it and keep the bookmark's link empty. (A shutdown
                // landing here keeps the finished share.)
                if (linked) await _vod.SetBookmarkShareUrlAsync(job.BookmarkId, "").ConfigureAwait(false);
                await DropRemoteAsync(uploaded.Id, token).ConfigureAwait(false);
                uploaded = null;
                PublishError(job, linked ? CancelMessage(job) : "Clip not found.", retryable: false, needsLogin: false);
                return;
            }
            stored = true;
            _cleanup.Remove(uploaded.Id);
            _logger.LogInformation("Clip shared: bm {BookmarkId} -> {Url}", job.BookmarkId, uploaded.Url);

            var transcriptAttached = job.Narrated
                && await TryPushTranscriptAsync(job, after, uploaded.Id, token, ct).ConfigureAwait(false);
            Publish(job, "done", size, size, uploaded.Url, transcriptAttached);
        }
        catch (ClipUploadException ex)
        {
            if (ex.Unauthorized)
            {
                // Re-prompt login WITHOUT clearing the stored session: a single 401 can be
                // transient, and wiping a valid multi-week session locks the user out.
                _logger.LogInformation("Share: proxy returned unauthorized; prompting re-login without clearing the session.");
            }
            else
            {
                _logger.LogWarning(ex, "Share failed for bm {BookmarkId}", job.BookmarkId);
            }
            PublishError(job, ex.Message, ex.Retryable, ex.Unauthorized);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (uploaded is not null && !stored) await DropRemoteAsync(uploaded.Id, token).ConfigureAwait(false);
            PublishError(job, CancelMessage(job), retryable: false, needsLogin: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Share failed for bm {BookmarkId}", job.BookmarkId);
            if (uploaded is not null && !stored) await DropRemoteAsync(uploaded.Id, token).ConfigureAwait(false);
            PublishError(job, GenericFailureMessage, retryable: false, needsLogin: false);
        }
        finally
        {
            pin?.Dispose();
            Retire(job);
        }
    }

    private async Task<int> ResolveDurationAsync(VodBookmarkRecord bookmark, string file, CancellationToken ct)
    {
        if (bookmark.ClipStartSeconds is { } start && bookmark.ClipEndSeconds is { } end && end > start)
            return ClampDuration(end - start);
        try
        {
            var probe = await _mixer.ProbeAsync(file, ct).ConfigureAwait(false);
            if (probe?.DurationSeconds is { } d && d > 0) return ClampDuration(d);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Share: duration probe failed for {File}", file);
        }
        return 1;
    }

    private async Task<bool> TryPushTranscriptAsync(ShareJob job, ClipNarrationRecord? narration, string slug,
        string token, CancellationToken ct)
    {
        if (narration is null || narration.TranscriptStatus != TranscriptStatuses.Ready) return false;
        var doc = TranscriptDocument.TryParse(narration.TranscriptJson);
        if (doc is null || doc.Segments.Count == 0) return false;
        Publish(job, "transcript");
        try
        {
            await _clips.PutTranscriptAsync(slug, token, doc, ct).ConfigureAwait(false);
            await _narrations.SetTranscriptPushedSlugAsync(job.BookmarkId, slug).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // The link is stored either way; the transcript is a bonus.
            _logger.LogInformation(ex, "Share: transcript push failed for bm {BookmarkId}", job.BookmarkId);
            return false;
        }
    }

    /// <summary>How long a remote drop may take once the host is stopping (the id stays queued).</summary>
    internal static readonly TimeSpan DropBudgetWhenStopping = TimeSpan.FromSeconds(2);

    private async Task DropRemoteAsync(string id, string token)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var gone = false;
        // Not bound to the job's token (it is usually cancelled here), but cut short once the
        // host is stopping so a stalled delete cannot push shutdown past its drain window.
        using var budget = new CancellationBudget(_work.Stopping, DropBudgetWhenStopping);
        try { gone = await _clips.DeleteAsync(id, token, budget.Token).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Share: remote delete of {Id} failed", id); }
        if (gone) _cleanup.Remove(id);
        else _cleanup.Add(id);
    }
}
