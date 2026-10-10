#nullable enable

using System.Collections.Concurrent;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using V = Revu.Sidecar.NarrationRequestValidator;

namespace Revu.Sidecar;

/// <summary>
/// The narration routes' logic (C6 6.4 to 6.7), independent of ASP.NET so it can be tested.
/// Renders are single-flight per bookmark and bound to request-aborted plus app-stopping.
/// </summary>
internal sealed class NarrationCommands
{
    // One render per clip at a time, across save and mix.
    private static readonly ConcurrentDictionary<long, byte> Rendering = new();

    private readonly IVodRepository _vod;
    private readonly IClipNarrationRepository _narrations;
    private readonly IConfigService _config;
    private readonly INarrationMixer _mixer;
    private readonly IClipUploadService _clips;
    private readonly Func<Task> _ensureBackedUp;
    private readonly ClipShareWorker _share;
    private readonly NarrationTranscriptionWorker _transcriber;
    private readonly RemoteClipCleanupStore _cleanup;
    private readonly SidecarEventHub _hub;
    private readonly CancellationToken _stopping;
    private readonly string _narrationDirectory;
    private readonly ILogger _log;

    public NarrationCommands(IVodRepository vod, IClipNarrationRepository narrations, IConfigService config,
        INarrationMixer mixer, IClipUploadService clips, Func<Task> ensureBackedUp, ClipShareWorker share,
        NarrationTranscriptionWorker transcriber, RemoteClipCleanupStore cleanup, SidecarEventHub hub,
        CancellationToken stopping, string narrationDirectory, ILogger log)
    {
        _vod = vod;
        _narrations = narrations;
        _config = config;
        _mixer = mixer;
        _clips = clips;
        _ensureBackedUp = ensureBackedUp;
        _share = share;
        _transcriber = transcriber;
        _cleanup = cleanup;
        _hub = hub;
        _stopping = stopping;
        _narrationDirectory = narrationDirectory;
        _log = log;
    }

    public async Task<ApiReply> SaveAsync(SaveNarrationBody? body, CancellationToken requestAborted)
    {
        var invalid = V.ValidateSave(body, out var narrationGuid);
        if (invalid is not null) return ApiReply.Error(400, invalid);
        var narrationId = narrationGuid.ToString("D");
        var audio = Path.Combine(_narrationDirectory, narrationId + ".webm");
        var audioError = V.ValidateAudioFile(audio);
        if (audioError is not null) return ApiReply.Error(400, audioError);

        var bookmark = await FindBookmarkAsync(body!.GameId, body.BookmarkId).ConfigureAwait(false);
        if (bookmark is null || NarrationDtos.FileOnDisk(bookmark.ClipPath).Length == 0)
            return ApiReply.Error(404, V.ClipFileMissing);

        if (!Rendering.TryAdd(bookmark.Id, 0)) return ApiReply.Error(409, V.AlreadyRendering);
        try
        {
            await _ensureBackedUp().ConfigureAwait(false);
            var render = await RenderAsync(bookmark.ClipPath, audio, body.OffsetMs, body.GameVolume,
                body.NarrationVolume, body.Duck, requestAborted).ConfigureAwait(false);
            if (render.Reply is { } failed) return failed;

            // The clip (or its game) may have been deleted during the render: store nothing,
            // spend no transcript, and drop the render that now belongs to nothing.
            if (await FindBookmarkAsync(body.GameId, body.BookmarkId).ConfigureAwait(false) is null)
            {
                NarrationFileGuard.TryDeleteClipFile(render.Output, _log);
                return ApiReply.Error(404, V.ClipFileMissing);
            }

            var signedIn = (await ClipShareLinks.SessionAsync(_config).ConfigureAwait(false)).SignedIn;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var previous = await _narrations.UpsertAsync(new ClipNarrationRecord(
                BookmarkId: bookmark.Id,
                GameId: bookmark.GameId,
                NarrationId: narrationId,
                AudioPath: audio,
                NarratedClipPath: render.Output,
                SourceClipPath: bookmark.ClipPath,
                OffsetMs: body.OffsetMs,
                DurationMs: body.DurationMs,
                GameVolume: body.GameVolume,
                NarrationVolume: body.NarrationVolume,
                Duck: body.Duck,
                TranscriptStatus: signedIn ? TranscriptStatuses.Pending : TranscriptStatuses.NeedsLogin,
                TranscriptGeneration: 0,
                TranscriptLanguage: "",
                TranscriptJson: "",
                TranscriptError: "",
                TranscriptPushedSlug: "",
                CreatedAt: now,
                UpdatedAt: now)).ConfigureAwait(false);

            if (previous is not null)
            {
                if (!SamePath(previous.AudioPath, audio)) NarrationFileGuard.TryDeleteNarrationAudio(previous.AudioPath, _narrationDirectory, _log);
                if (!SamePath(previous.NarratedClipPath, render.Output)) NarrationFileGuard.TryDeleteClipFile(previous.NarratedClipPath, _log);
            }

            var shareCleared = await ClearShareForNarrationChangeAsync(bookmark).ConfigureAwait(false);
            if (signedIn) _transcriber.Enqueue(bookmark.Id);
            return await NarrationReplyAsync(bookmark, shareCleared).ConfigureAwait(false);
        }
        finally
        {
            Rendering.TryRemove(bookmark.Id, out _);
        }
    }

    public async Task<ApiReply> MixAsync(MixNarrationBody? body, CancellationToken requestAborted)
    {
        var invalid = V.ValidateMix(body);
        if (invalid is not null) return ApiReply.Error(400, invalid);

        var narration = await _narrations.GetAsync(body!.BookmarkId).ConfigureAwait(false);
        if (narration is null || narration.GameId != body.GameId) return ApiReply.Error(404, V.NoNarration);
        var bookmark = await FindBookmarkAsync(body.GameId, body.BookmarkId).ConfigureAwait(false);
        if (bookmark is null || NarrationDtos.FileOnDisk(bookmark.ClipPath).Length == 0)
            return ApiReply.Error(404, V.ClipFileMissing);
        if (!NarrationFileGuard.IsDeletableNarrationAudio(narration.AudioPath, _narrationDirectory)
            || V.ValidateAudioFile(narration.AudioPath) is not null)
            return ApiReply.Error(404, V.NoNarration);

        if (!Rendering.TryAdd(bookmark.Id, 0)) return ApiReply.Error(409, V.AlreadyRendering);
        try
        {
            await _ensureBackedUp().ConfigureAwait(false);
            var render = await RenderAsync(bookmark.ClipPath, narration.AudioPath, body.OffsetMs, body.GameVolume,
                body.NarrationVolume, body.Duck, requestAborted).ConfigureAwait(false);
            if (render.Reply is { } failed) return failed;

            var signedIn = (await ClipShareLinks.SessionAsync(_config).ConfigureAwait(false)).SignedIn;
            var resetTranscript = body.OffsetMs != narration.OffsetMs;
            var previous = await _narrations.UpdateMixAsync(bookmark.Id, render.Output, body.OffsetMs,
                body.GameVolume, body.NarrationVolume, body.Duck, resetTranscript,
                signedIn ? TranscriptStatuses.Pending : TranscriptStatuses.NeedsLogin).ConfigureAwait(false);
            if (previous is null)
            {
                // Removed while rendering: the new render belongs to nothing.
                NarrationFileGuard.TryDeleteClipFile(render.Output, _log);
                return ApiReply.Error(404, V.NoNarration);
            }
            if (!SamePath(previous.NarratedClipPath, render.Output))
                NarrationFileGuard.TryDeleteClipFile(previous.NarratedClipPath, _log);

            var shareCleared = await ClearShareForNarrationChangeAsync(bookmark).ConfigureAwait(false);
            if (resetTranscript && signedIn) _transcriber.Enqueue(bookmark.Id);
            return await NarrationReplyAsync(bookmark, shareCleared).ConfigureAwait(false);
        }
        finally
        {
            Rendering.TryRemove(bookmark.Id, out _);
        }
    }

    public async Task<ApiReply> DeleteAsync(NarrationTargetBody? body)
    {
        if (body is null || body.GameId <= 0 || body.BookmarkId <= 0)
            return ApiReply.Error(400, "gameId and bookmarkId required");
        var existing = await _narrations.GetAsync(body.BookmarkId).ConfigureAwait(false);
        if (existing is null || existing.GameId != body.GameId)
            return ApiReply.Ok(new { ok = true, shareCleared = false });

        await _ensureBackedUp().ConfigureAwait(false);
        var deleted = await _narrations.DeleteAsync(body.BookmarkId).ConfigureAwait(false);
        if (deleted is not null) NarrationFileGuard.DeleteNarrationFiles(deleted.AudioPath, deleted.NarratedClipPath, _narrationDirectory, _log);

        var shareCleared = false;
        var bookmark = await FindBookmarkAsync(body.GameId, body.BookmarkId).ConfigureAwait(false);
        if (bookmark is not null) shareCleared = await ClearShareForNarrationChangeAsync(bookmark).ConfigureAwait(false);
        PublishNarration(body.GameId, body.BookmarkId, "");
        return ApiReply.Ok(new { ok = true, shareCleared });
    }

    public async Task<ApiReply> TranscribeAsync(NarrationTargetBody? body)
    {
        if (body is null || body.GameId <= 0 || body.BookmarkId <= 0)
            return ApiReply.Error(400, "gameId and bookmarkId required");
        var narration = await _narrations.GetAsync(body.BookmarkId).ConfigureAwait(false);
        if (narration is null || narration.GameId != body.GameId) return ApiReply.Error(404, V.NoNarration);

        await _ensureBackedUp().ConfigureAwait(false);
        if (!(await ClipShareLinks.SessionAsync(_config).ConfigureAwait(false)).SignedIn)
        {
            await _narrations.ResetTranscriptAsync(body.BookmarkId, TranscriptStatuses.NeedsLogin).ConfigureAwait(false);
            PublishNarration(body.GameId, body.BookmarkId, TranscriptStatuses.NeedsLogin);
            return ApiReply.Ok(new { ok = false, needsLogin = true, transcriptStatus = TranscriptStatuses.NeedsLogin });
        }
        await _narrations.ResetTranscriptAsync(body.BookmarkId, TranscriptStatuses.Pending).ConfigureAwait(false);
        _transcriber.Enqueue(body.BookmarkId);
        PublishNarration(body.GameId, body.BookmarkId, TranscriptStatuses.Pending);
        return ApiReply.Ok(new { ok = true, transcriptStatus = TranscriptStatuses.Pending });
    }

    /// <summary>
    /// A narration change invalidates the shared copy: cancel any share job, delete the
    /// remote copy (or queue the delete while signed out) and clear the link. True when a
    /// link existed.
    /// </summary>
    internal async Task<bool> ClearShareForNarrationChangeAsync(VodBookmarkRecord bookmark)
    {
        _share.Cancel(bookmark.Id, ClipShareWorker.NarrationChangedMessage);
        // Re-read: a render can take minutes and the link may have changed meanwhile.
        var current = await FindBookmarkAsync(bookmark.GameId, bookmark.Id).ConfigureAwait(false) ?? bookmark;
        if (string.IsNullOrWhiteSpace(current.ShareUrl)) return false;
        var slug = ClipShareLinks.SlugFromUrl(current.ShareUrl);
        await ClipShareLinks.DeleteOrQueueAsync(slug, _config, _clips, _cleanup, _log).ConfigureAwait(false);
        await _vod.SetBookmarkShareUrlAsync(bookmark.Id, "").ConfigureAwait(false);
        _log.LogInformation("Narration change cleared the share link of bm {BookmarkId}", bookmark.Id);
        return true;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private readonly record struct RenderOutcome(string Output, ApiReply? Reply);

    private async Task<RenderOutcome> RenderAsync(string clipPath, string audioPath, int offsetMs, double gameVolume,
        double narrationVolume, bool duck, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _stopping);
        var output = NarratedOutputPath(clipPath);
        try
        {
            var probe = await _mixer.ProbeAsync(clipPath, cts.Token).ConfigureAwait(false);
            var plan = new MixPlan(clipPath, audioPath, output, offsetMs, gameVolume, narrationVolume, duck,
                ClipHasAudio: probe?.HasAudio ?? true, ClipDurationSeconds: probe?.DurationSeconds);
            var ok = await _mixer.MixAsync(plan, cts.Token).ConfigureAwait(false);
            cts.Token.ThrowIfCancellationRequested();
            if (!ok)
            {
                NarrationFileGuard.TryDeleteClipFile(output, _log);
                return new RenderOutcome("", ApiReply.Error(422, V.RenderFailed));
            }
            return new RenderOutcome(output, null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            NarrationFileGuard.TryDeleteClipFile(output, _log);
            _log.LogInformation("Narration render cancelled ({Reason})", _stopping.IsCancellationRequested ? "stopping" : "request aborted");
            return new RenderOutcome("", _stopping.IsCancellationRequested
                ? ApiReply.Error(503, V.ShuttingDown)
                : ApiReply.Error(499, "The request was cancelled."));
        }
    }

    /// <summary><c>&lt;clip dir&gt;\narrated\&lt;stem&gt;_narrated_&lt;yyyyMMdd_HHmmss&gt;.mp4</c>, unique.</summary>
    internal static string NarratedOutputPath(string clipPath)
    {
        var dir = Path.Combine(Path.GetDirectoryName(clipPath) ?? "", ClipService.NarratedFolderName);
        Directory.CreateDirectory(dir);
        var stem = $"{Path.GetFileNameWithoutExtension(clipPath)}_narrated_{DateTime.Now:yyyyMMdd_HHmmss}";
        var candidate = Path.Combine(dir, stem + ".mp4");
        for (var n = 2; File.Exists(candidate); n++) candidate = Path.Combine(dir, $"{stem}_{n}.mp4");
        return candidate;
    }

    private async Task<VodBookmarkRecord?> FindBookmarkAsync(long gameId, long bookmarkId) =>
        (await _vod.GetBookmarksAsync(gameId).ConfigureAwait(false)).FirstOrDefault(b => b.Id == bookmarkId);

    private async Task<ApiReply> NarrationReplyAsync(VodBookmarkRecord bookmark, bool shareCleared)
    {
        var row = await _narrations.GetAsync(bookmark.Id).ConfigureAwait(false);
        PublishNarration(bookmark.GameId, bookmark.Id, row?.TranscriptStatus ?? "");
        return ApiReply.Ok(new { ok = true, narration = NarrationDtos.MapOrNull(row), shareCleared });
    }

    private void PublishNarration(long gameId, long bookmarkId, string status) =>
        _hub.Publish("clipNarrationUpdated", new
        {
            gameId,
            bookmarkId,
            transcriptStatus = status,
            chunksDone = 0,
            chunksTotal = 0,
        });

    private static bool SamePath(string? a, string? b) =>
        string.Equals(ClipRetentionGuard.Normalize(a), ClipRetentionGuard.Normalize(b), StringComparison.OrdinalIgnoreCase);
}
