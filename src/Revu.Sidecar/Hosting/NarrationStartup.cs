#nullable enable

using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// 3.14 narrated clips at startup and sign-in: crash recovery, orphan and stale-file sweeps,
/// the share and transcription workers, and the signed-in catch-up (transcribe what waited
/// for sign-in, drain deferred remote clip deletes). Never runs in isolated host mode.
/// </summary>
public static class NarrationStartup
{
    /// <summary>Unreferenced voice tracks and renders younger than this are left alone (in flight).</summary>
    internal static readonly TimeSpan StaleFileAge = TimeSpan.FromHours(24);

    public static void StartNarrationServices(this WebApplication app)
    {
        var services = app.Services;
        var work = services.GetRequiredService<SidecarBackgroundWork>();
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("NarrationStartup");
        if (!work.TryRun("startup narration", async () =>
            {
                try
                {
                    var w = services.GetRequiredService<WriteServices>();
                    await SweepAsync(w.ClipNarrations, AppDataPaths.NarrationDirectory, w.Config.ClipsFolder,
                        w.BackupGuard.EnsureBackedUpAsync, DateTime.UtcNow, log);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Narration startup sweep failed (non-fatal)");
                }

                services.GetRequiredService<ClipShareWorker>().Start();
                services.GetRequiredService<NarrationTranscriptionWorker>().Start();
                await OnSignedInAsync(services, log);
            }))
        {
            log.LogInformation("Narration services not started: the host is stopping");
        }
    }

    /// <summary>
    /// Steps 1 to 3: rows a crashed run left <c>processing</c> go back to <c>pending</c>;
    /// narration rows whose bookmark is gone (a downgraded build deleted it) are removed
    /// with their files; unreferenced voice tracks, partial files and narrated renders older
    /// than 24 h are deleted.
    /// </summary>
    internal static async Task SweepAsync(IClipNarrationRepository narrations, string narrationDirectory,
        string clipsFolder, Func<Task> ensureBackedUp, DateTime nowUtc, ILogger log)
    {
        // Each write below is gated on a read first, so a sweep with nothing to do never
        // writes, and the first write of the session always follows the backup.
        var backedUp = false;
        async Task BackUpOnceAsync()
        {
            if (backedUp) return;
            await ensureBackedUp();
            backedUp = true;
        }

        if ((await narrations.ListByTranscriptStatusAsync(TranscriptStatuses.Processing)).Count > 0)
        {
            await BackUpOnceAsync();
            var reset = await narrations.ResetProcessingToPendingAsync();
            log.LogInformation("Narration startup: {Count} interrupted transcript(s) back to pending", reset);
        }

        if (await narrations.CountOrphansAsync() > 0)
        {
            await BackUpOnceAsync();
            var orphans = await narrations.DeleteOrphansAsync();
            foreach (var orphan in orphans)
            {
                NarrationFileGuard.DeleteNarrationFiles(orphan.AudioPath, orphan.NarratedClipPath, narrationDirectory, log);
            }
            if (orphans.Count > 0) log.LogInformation("Narration startup: removed {Count} orphaned narration(s)", orphans.Count);
        }

        var referencedAudio = new HashSet<string>(
            (await narrations.ListAudioPathsAsync()).Select(ClipRetentionGuard.Normalize), StringComparer.OrdinalIgnoreCase);
        var cutoff = nowUtc - StaleFileAge;
        var removed = 0;
        if (Directory.Exists(narrationDirectory))
        {
            foreach (var file in SafeEnumerate(narrationDirectory, "*.webm").Concat(SafeEnumerate(narrationDirectory, "*.part")))
            {
                if (IsStaleUnreferenced(file, referencedAudio, cutoff) && TryDelete(file.FullName, log)) removed++;
            }
        }

        // Narrated renders whose row is gone (a note-bookmark delete, a crash between render
        // and save, an interrupted .part.mp4). Only the configured clips folder's narrated\.
        var narratedDir = string.IsNullOrWhiteSpace(clipsFolder) ? "" : Path.Combine(clipsFolder, ClipService.NarratedFolderName);
        if (narratedDir.Length > 0 && Directory.Exists(narratedDir))
        {
            var referencedClips = new HashSet<string>(
                (await narrations.ListProtectedClipPathsAsync()).Select(ClipRetentionGuard.Normalize), StringComparer.OrdinalIgnoreCase);
            foreach (var file in SafeEnumerate(narratedDir, "*.mp4"))
            {
                if (IsStaleUnreferenced(file, referencedClips, cutoff) && TryDelete(file.FullName, log)) removed++;
            }
        }
        if (removed > 0) log.LogInformation("Narration startup: deleted {Count} stale narration file(s)", removed);
    }

    /// <summary>
    /// Step 5 (startup when signed in, and after a successful sign-in): narrations that
    /// waited for sign-in go to <c>pending</c>, every pending one is queued, and deferred
    /// remote clip deletes are sent.
    /// </summary>
    public static async Task OnSignedInAsync(IServiceProvider services, ILogger log)
    {
        var w = services.GetRequiredService<WriteServices>();
        var session = await ClipShareLinks.SessionAsync(w.Config);
        if (!session.SignedIn) return;

        var hub = services.GetRequiredService<SidecarEventHub>();
        var transcriber = services.GetRequiredService<NarrationTranscriptionWorker>();
        var work = services.GetRequiredService<SidecarBackgroundWork>();

        // needs_login rows, plus failed rows that only failed because the server had no
        // transcription yet (a proxy older than 3.14): both go back to pending.
        var waiting = (await w.ClipNarrations.ListByTranscriptStatusAsync(TranscriptStatuses.NeedsLogin))
            .Concat((await w.ClipNarrations.ListByTranscriptStatusAsync(TranscriptStatuses.Failed))
                .Where(r => r.TranscriptError == NarrationTranscriptionWorker.UnavailableMessage))
            .ToList();
        if (waiting.Count > 0) await w.BackupGuard.EnsureBackedUpAsync();
        foreach (var row in waiting)
        {
            if (!await w.ClipNarrations.ResetTranscriptAsync(row.BookmarkId, TranscriptStatuses.Pending)) continue;
            hub.Publish("clipNarrationUpdated", new
            {
                gameId = row.GameId,
                bookmarkId = row.BookmarkId,
                transcriptStatus = TranscriptStatuses.Pending,
                chunksDone = 0,
                chunksTotal = 0,
            });
        }

        foreach (var row in await w.ClipNarrations.ListByTranscriptStatusAsync(TranscriptStatuses.Pending))
            transcriber.Enqueue(row.BookmarkId);

        try
        {
            await PushMissedTranscriptsAsync(w.ClipNarrations, w.Vod, w.ClipUpload, session.Token,
                w.BackupGuard.EnsureBackedUpAsync, work.Stopping, log);
        }
        catch (OperationCanceledException) when (work.Stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Transcript catch-up failed (will retry next start)");
        }

        try
        {
            await services.GetRequiredService<RemoteClipCleanupStore>().DrainAsync(session.Token, work.Stopping);
        }
        catch (OperationCanceledException) when (work.Stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Remote clip cleanup drain failed (will retry next start)");
        }
    }

    /// <summary>
    /// Ready transcripts whose narrated share never received them (a proxy older than 3.14
    /// had no transcript route, or the push failed) are pushed now. Best effort: a failure
    /// leaves the row for the next start; a share the server no longer has is settled so
    /// it is not retried. Returns how many were pushed.
    /// </summary>
    internal static async Task<int> PushMissedTranscriptsAsync(IClipNarrationRepository narrations, IVodRepository vod,
        IClipUploadService clips, string token, Func<Task> ensureBackedUp, CancellationToken ct, ILogger log)
    {
        var pushed = 0;
        var ready = await narrations.ListByTranscriptStatusAsync(TranscriptStatuses.Ready);
        foreach (var game in ready.GroupBy(r => r.GameId))
        {
            if (ct.IsCancellationRequested) break;
            var bookmarks = (await vod.GetBookmarksAsync(game.Key)).ToDictionary(b => b.Id);
            foreach (var row in game)
            {
                if (ct.IsCancellationRequested) break;
                if (!bookmarks.TryGetValue(row.BookmarkId, out var bookmark)) continue;
                var slug = ClipShareLinks.SlugFromUrl(bookmark.ShareUrl);
                if (slug.Length == 0 || string.Equals(slug, row.TranscriptPushedSlug, StringComparison.Ordinal)) continue;
                // Only a narrated share carries this voice: the share worker uploads the render
                // when it is on disk, and the source clip (no narration) otherwise.
                if (NarrationDtos.FileOnDisk(row.NarratedClipPath).Length == 0) continue;
                var doc = TranscriptDocument.TryParse(row.TranscriptJson);
                if (doc is null || doc.Segments.Count == 0) continue;
                try
                {
                    await clips.PutTranscriptAsync(slug, token, doc, ct);
                    pushed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (ClipUploadException ex) when (ex.Gone)
                {
                    log.LogDebug("Transcript catch-up: shared clip {Slug} is gone", slug);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "Transcript catch-up for bm {BookmarkId} failed (will retry next start)", row.BookmarkId);
                    continue;
                }
                await ensureBackedUp();
                await narrations.SetTranscriptPushedSlugAsync(row.BookmarkId, slug);
            }
        }
        if (pushed > 0) log.LogInformation("Transcript catch-up: attached {Count} transcript(s) to shared clips", pushed);
        return pushed;
    }

    private static bool IsStaleUnreferenced(FileInfo file, HashSet<string> referenced, DateTime cutoffUtc)
    {
        try
        {
            return file.LastWriteTimeUtc < cutoffUtc && !referenced.Contains(ClipRetentionGuard.Normalize(file.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerate(string dir, string pattern)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<FileInfo>(); }
    }

    private static bool TryDelete(string path, ILogger log)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Could not delete stale narration file {Path}", path);
            return false;
        }
    }
}
