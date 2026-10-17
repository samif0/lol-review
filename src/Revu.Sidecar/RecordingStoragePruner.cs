using System.Text.Json;
using Revu.Core.Services;

namespace Revu.Sidecar;

public sealed record RecordingPruneResult(bool Ok, int Deleted, long FreedBytes, long FolderBytes, string Status);

/// <summary>
/// Holds Revu's recordings folder under the user's size limit (RecordingsMaxSizeGb, 0 = no
/// limit). Runs only between games: the host asks after a match's recording has settled, and
/// a game in progress defers it to the next match end. Oldest finished sessions go first.
/// Never deleted: the newest session, a session still recording or awaiting its match, a
/// receipt still pending its match, the game whose VOD is sharing, and any session whose
/// files another process has open (playback, clip extraction, speed-up). Only the deleted
/// files' VOD links are removed; clips, bookmarks and notes stay.
/// </summary>
public sealed class RecordingStoragePruner(string root, IConfigService config,
    RecordingRegistrationService registrations, RecordingLinkStore links,
    Func<long?> sharingGameId, Func<bool> gameInProgress, Func<Task> ensureBackup,
    ILogger<RecordingStoragePruner> logger, long bytesPerGb = 1024L * 1024 * 1024)
{
    private static readonly HashSet<string> FinishedStates = new(StringComparer.Ordinal) { "saved", "partial" };
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private sealed record Session(Guid Id, DirectoryInfo Directory, long Bytes, string? State, DateTimeOffset StartedAt, long? GameId);
    private sealed record SessionFile(string? State, DateTimeOffset? StartedAt, long? GameId);

    public async Task<RecordingPruneResult> PruneAsync(CancellationToken ct = default)
    {
        var limitGb = (await config.LoadAsync()).RecordingsMaxSizeGb;
        if (limitGb <= 0) return new(true, 0, 0, 0, "no-limit");
        if (gameInProgress()) return new(true, 0, 0, 0, "game-in-progress");
        if (!Directory.Exists(_root)) return new(true, 0, 0, 0, "empty");
        var limit = limitGb * bytesPerGb;

        return await registrations.WithReceiptsAsync(async (receipts, deleteReceipt) =>
        {
            var sessions = ListSessions(ct);
            var total = sessions.Sum(s => s.Bytes);
            if (total <= limit) return new RecordingPruneResult(true, 0, 0, total, "under-limit");

            var newest = sessions.MaxBy(s => s.StartedAt)?.Id;
            var sharing = sharingGameId();
            var candidates = sessions
                .Where(s => s.Id != newest && s.State is not null && FinishedStates.Contains(s.State)
                    && !(receipts.TryGetValue(s.Id, out var status) && status == "pending-match")
                    && !(sharing is not null && s.GameId == sharing))
                .OrderBy(s => s.StartedAt);

            int deleted = 0;
            long freed = 0;
            var backedUp = false;
            foreach (var session in candidates)
            {
                if (total <= limit) break;
                ct.ThrowIfCancellationRequested();
                if (!NothingElseHasOpen(session.Directory))
                {
                    logger.LogInformation("Recording {SessionId} is in use; kept by the storage limit", session.Id);
                    continue;
                }
                if (!backedUp) { await ensureBackup(); backedUp = true; }
                var removed = DeleteSessionFiles(session.Directory);
                // Unlink only files that are really gone: a file that survived keeps its link.
                await links.UnlinkMissingUnderAsync(session.Directory.FullName);
                if (!Directory.Exists(session.Directory.FullName) || !session.Directory.EnumerateFileSystemInfos().Any())
                {
                    try { session.Directory.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    deleteReceipt(session.Id);
                }
                if (removed == 0) continue;
                total -= removed; freed += removed; deleted++;
                logger.LogInformation("Deleted old recording {SessionId} ({Mb:F0} MB) to stay under the {Limit} GB limit",
                    session.Id, removed / (1024.0 * 1024.0), limitGb);
            }
            if (total > limit)
                logger.LogWarning("Recordings folder still over its limit ({Gb:F1} GB / {Limit} GB): the rest is new, in use or unfinished",
                    total / (double)bytesPerGb, limitGb);
            return new RecordingPruneResult(true, deleted, freed, total, deleted > 0 ? "pruned" : "nothing-deletable");
        });
    }

    /// <summary>The bytes the limit counts: every file inside a session folder. No reparse point is followed.</summary>
    public static long MeasureFolderBytes(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (var directory in SessionDirectories(new DirectoryInfo(root)))
        {
            ct.ThrowIfCancellationRequested();
            total += DirectoryBytes(directory);
        }
        return total;
    }

    private List<Session> ListSessions(CancellationToken ct)
    {
        var sessions = new List<Session>();
        foreach (var directory in SessionDirectories(new DirectoryInfo(_root)))
        {
            ct.ThrowIfCancellationRequested();
            var id = Guid.ParseExact(directory.Name, "D");
            var file = ReadSessionFile(directory);
            // A folder without readable metadata still counts toward the limit but is never deleted.
            var startedAt = file?.StartedAt ?? new DateTimeOffset(directory.CreationTimeUtc, TimeSpan.Zero);
            sessions.Add(new(id, directory, DirectoryBytes(directory), file?.State, startedAt, file?.GameId));
        }
        return sessions;
    }

    private static IEnumerable<DirectoryInfo> SessionDirectories(DirectoryInfo root) =>
        root.EnumerateDirectories()
            .Where(d => Guid.TryParseExact(d.Name, "D", out _) && (d.Attributes & FileAttributes.ReparsePoint) == 0);

    private static IEnumerable<FileInfo> SessionFiles(DirectoryInfo directory) =>
        directory.EnumerateFiles("*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
        });

    private static long DirectoryBytes(DirectoryInfo directory)
    {
        try { return SessionFiles(directory).Sum(f => f.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private SessionFile? ReadSessionFile(DirectoryInfo directory)
    {
        var path = Path.Combine(directory.FullName, "session.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 64 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var rootElement = json.RootElement;
            if (!rootElement.TryGetProperty("sessionId", out var sessionId) || sessionId.GetString() != directory.Name) return null;
            string? state = rootElement.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            DateTimeOffset? started = rootElement.TryGetProperty("startedAt", out var t) && t.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(t.GetString(), out var parsed) ? parsed : null;
            long? gameId = rootElement.TryGetProperty("gameId", out var g) && g.ValueKind == JsonValueKind.Number
                && g.TryGetInt64(out var id) && id > 0 ? id : null;
            return new(state, started, gameId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Recording session metadata unreadable in {Folder}", directory.Name);
            return null;
        }
    }

    // Windows refuses an exclusive open while any other handle is open, so this proves no
    // player, clip extraction, share upload or speed-up is reading the files right now.
    private static bool NothingElseHasOpen(DirectoryInfo directory)
    {
        var handles = new List<FileStream>();
        try
        {
            foreach (var file in SessionFiles(directory))
                handles.Add(new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { foreach (var handle in handles) handle.Dispose(); }
    }

    private long DeleteSessionFiles(DirectoryInfo directory)
    {
        long removed = 0;
        var failed = false;
        // Media first. session.json goes only once everything else is gone, so a folder that
        // kept a file still reads as a finished session and the next prune can finish it.
        var files = SessionFiles(directory).ToList();
        var journal = files.FirstOrDefault(f => f.Name.Equals("session.json", StringComparison.OrdinalIgnoreCase));
        foreach (var file in files.Where(f => f != journal).Append(journal))
        {
            if (file is null || (file == journal && failed)) continue;
            try { var size = file.Length; file.Delete(); removed += size; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { failed = true; logger.LogWarning(ex, "Could not delete recording file {Name}", file.Name); }
        }
        var folders = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var sub in directory.EnumerateDirectories("*", folders).OrderByDescending(d => d.FullName.Length).ToList())
            try { sub.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return removed;
    }
}
