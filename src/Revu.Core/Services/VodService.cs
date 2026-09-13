#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Timestamp-based Ascent fallback. Filename time and last-write time are an
/// estimate, not verified match identity or video duration. Only a configured
/// folder is scanned; native receipts remain the authority for Revu recordings.
/// </summary>
public sealed partial class VodService : IVodService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".webm", ".mov" };
    private readonly IGameRepository _games;
    private readonly IVodRepository _vods;
    private readonly IConfigService _config;
    private readonly ILogger<VodService> _logger;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    public VodService(IGameRepository games, IVodRepository vods, IConfigService config, ILogger<VodService> logger)
        => (_games, _vods, _config, _logger) = (games, vods, config, logger);

    [GeneratedRegex(@"(?<!\d)(\d{1,2})[-_](\d{1,2})[-_](\d{4})[ _-](\d{1,2})[-_](\d{2})(?:[-_](\d{2}))?(?!\d)")]
    private static partial Regex AscentTimestamp();
    [GeneratedRegex(@"(?<!\d)(\d{4})[-_](\d{1,2})[-_](\d{1,2})[ _-](\d{1,2})[-_](\d{2})(?:[-_](\d{2}))?(?!\d)")]
    private static partial Regex IsoTimestamp();

    public async Task<List<VodRecordingInfo>> FindRecordingsAsync(string? folder = null, CancellationToken cancellationToken = default)
    {
        folder ??= _config.AscentFolder;
        if (!TryFolder(folder, out var root)) return [];
        // Enumeration stays off the game-monitor thread, is awaited, and observes
        // shutdown. All candidates share one stability interval, not one per file.
        var found = await Task.Run(() => Enumerate(root, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (found.Count == 0) return found;
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < found.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = found[i];
            using var read = OpenStable(item, root);
            found[i] = item with { IsReady = read is not null };
        }
        return found.OrderByDescending(r => r.StartTs ?? r.Mtime).ToList();
    }

    public string? MatchRecordingToGame(GameStats game, IReadOnlyList<VodRecordingInfo> recordings,
        IReadOnlySet<string>? excludePaths = null) => recordings
        .Where(r => r.IsReady && !(excludePaths?.Contains(r.Path) ?? false))
        .Select(r => (Recording: r, Score: MatchScore(game, r)))
        .Where(x => x.Score.HasValue).OrderBy(x => x.Score)
        .Select(x => x.Recording.Path).FirstOrDefault();

    public async Task<bool> TryLinkRecordingAsync(GameStats game, string? folder = null, CancellationToken cancellationToken = default)
    {
        if (game.GameId <= 0) return false;
        if (await _vods.GetVodAsync(game.GameId).ConfigureAwait(false) is not null) return true;
        // Global best-first assignment also considers neighbouring matches. A
        // post-game retry must not steal a closer candidate from another game.
        await ScanCoreAsync(folder, cancellationToken).ConfigureAwait(false);
        return await _vods.GetVodAsync(game.GameId).ConfigureAwait(false) is not null;
    }

    public Task<VodScanResult> ScanAsync(CancellationToken cancellationToken = default, IReadOnlySet<long>? reservedGameIds = null)
        => ScanCoreAsync(null, cancellationToken, reservedGameIds);
    public async Task<int> AutoMatchRecordingsAsync(CancellationToken cancellationToken = default)
        => (await ScanAsync(cancellationToken).ConfigureAwait(false)).Matched;

    private async Task<VodScanResult> ScanCoreAsync(string? requestedFolder, CancellationToken cancellationToken, IReadOnlySet<long>? reservedGameIds = null)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = requestedFolder ?? _config.AscentFolder;
            if (string.IsNullOrWhiteSpace(folder)) return new(false, 0, 0, "Choose an Ascent recording folder first.");
            if (!TryFolder(folder, out var root)) return new(false, 0, 0, "The Ascent folder is unavailable, uses a linked path, or belongs to Revu's native recorder.");
            var recordings = await FindRecordingsAsync(root, cancellationToken).ConfigureAwait(false);
            var existing = await _vods.GetAllVodsAsync().ConfigureAwait(false);
            var occupiedGames = existing.Select(v => v.GameId).ToHashSet();
            var usedPaths = existing.Select(v => CanonicalPath(v.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var games = await _games.GetRecentAsync(limit: 100).ConfigureAwait(false);
            var candidates = new List<(GameStats Game, VodRecordingInfo Recording, double Score)>();
            foreach (var rec in recordings.Where(r => r.IsReady && !usedPaths.Contains(r.Path)))
            {
                // An occupied match still participates in identity resolution: its
                // duplicate external video must not spill into the following match.
                var scores = games.Select(g => (Game: g, Score: MatchScore(g, rec)))
                    .Where(x => x.Score.HasValue).OrderBy(x => x.Score).ToArray();
                // Minute-resolution filenames cannot disambiguate near ties.
                if (scores.Length == 0 || (scores.Length > 1 && scores[1].Score - scores[0].Score <= 60)) continue;
                var best = scores[0];
                if (occupiedGames.Contains(best.Game.GameId) || reservedGameIds?.Contains(best.Game.GameId) == true) continue;
                candidates.Add((best.Game, rec, best.Score!.Value));
            }
            var matched = 0;
            var linked = new List<long>();
            foreach (var (game, rec, _) in candidates.OrderBy(x => x.Score))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Disconnecting/changing the folder stops an already-running scan
                // before its next insert. Explicit folder calls are test/manual APIs.
                if (requestedFolder is null && !SameFolder(_config.AscentFolder, root)) break;
                if (occupiedGames.Contains(game.GameId) || usedPaths.Contains(rec.Path)) continue;
                using var read = OpenStable(rec, root);
                if (read is null) continue;
                if (await _vods.TryLinkUnownedVodAsync(game.GameId, rec.Path, rec.Size).ConfigureAwait(false))
                {
                    occupiedGames.Add(game.GameId);
                    usedPaths.Add(rec.Path);
                    matched++;
                    linked.Add(game.GameId);
                    _logger.LogInformation("Linked external recording {File} to match {GameId}", rec.Name, game.GameId);
                }
            }
            return new(true, matched, recordings.Count,
                VodScanMessages.Success(matched, recordings.Count, recordings.Count(r => !r.IsReady))) { LinkedGameIds = linked };
        }
        finally { _scanGate.Release(); }
    }

    private List<VodRecordingInfo> Enumerate(string root, CancellationToken ct)
    {
        var found = new List<VodRecordingInfo>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsSafePath(directory, root) || IsNativePath(directory)) continue;
            try
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((file.Attributes & FileAttributes.Directory) != 0) { pending.Push(file.FullName); continue; }
                    if (!VideoExtensions.Contains(file.Extension)) continue;
                    try
                    {
                        var info = new FileInfo(file.FullName);
                        found.Add(new(info.FullName, info.Name, info.Length,
                            new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000d,
                            ParseTimestamp(info.Name), info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), false));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _logger.LogDebug(ex, "Skipping inaccessible recording folder {Directory}", directory); }
        }
        return found;
    }

    private static FileStream? OpenStable(VodRecordingInfo recording, string root)
    {
        try
        {
            if (!IsSafePath(recording.Path, root) || IsNativePath(recording.Path)) return null;
            var file = new FileInfo(recording.Path);
            if (!file.Exists || file.Length <= 0 || file.Length != recording.Size
                || new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000d != recording.Mtime
                || file.LastWriteTimeUtc > DateTime.UtcNow.AddSeconds(-30)) return null;
            // Deny other writers/deletion while linking. An Ascent handle still
            // open for encoding fails even if it shares read/write with others.
            return new FileStream(recording.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static double? MatchScore(GameStats game, VodRecordingInfo rec)
    {
        if (game.Timestamp <= 0 || game.GameDuration <= 0 || rec.StartTs is not double start) return null;
        // Do not assign arbitrary clips by mtime alone. Filename time is local
        // Ascent recording start; mtime is only an estimate of finalization time.
        if (rec.Mtime - start < game.GameDuration - 90) return null;
        double? best = null;
        // New rows use start time; pre-fix live rows sometimes stored end time.
        foreach (var gameStart in new[] { game.Timestamp, game.Timestamp - game.GameDuration })
        {
            var startDelta = gameStart - start;
            var endDelta = rec.Mtime - (gameStart + game.GameDuration);
            if (startDelta < -90 || startDelta > 600 || endDelta < -90 || endDelta > 600) continue;
            var score = Math.Abs(startDelta) + Math.Abs(endDelta);
            if (best is null || score < best) best = score;
        }
        return best;
    }

    private static double? ParseTimestamp(string name)
    {
        var m = IsoTimestamp().Match(name);
        var iso = m.Success;
        if (!iso) m = AscentTimestamp().Match(name);
        if (!m.Success) return null;
        try
        {
            int N(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
            var date = new DateTime(iso ? N(1) : N(3), iso ? N(2) : N(1), iso ? N(3) : N(2), N(4), N(5),
                m.Groups[6].Success ? N(6) : 0, DateTimeKind.Local);
            return new DateTimeOffset(date).ToUnixTimeSeconds();
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool TryFolder(string? folder, out string root)
    {
        root = "";
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            root = Path.GetFullPath(folder);
            return Directory.Exists(root) && IsSafePath(root, root) && !IsNativePath(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static bool SameFolder(string? candidate, string root)
    {
        try { return !string.IsNullOrWhiteSpace(candidate) && Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static bool IsSafePath(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = normalizedRoot + Path.DirectorySeparatorChar;
            if (!full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool IsNativePath(string path)
    {
        var native = Path.GetFullPath(AppDataPaths.RecordingsDirectory).TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(native, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(native + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; } // A bad historical link still owns its game.
    }
}
