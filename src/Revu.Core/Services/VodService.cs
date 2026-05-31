#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// VOD file discovery and matching for Ascent recordings.
/// Ported from Python vod.py.
/// </summary>
public sealed partial class VodService : IVodService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".webm", ".mov"
    };

    // Ascent filename pattern: M-DD-YYYY-HH-MM or MM-DD-YYYY-HH-MM
    [GeneratedRegex(@"(\d{1,2})-(\d{1,2})-(\d{4})-(\d{1,2})-(\d{2})")]
    private static partial Regex FilenameTimestampRegex();

    [GeneratedRegex(@"(\d{4})[-_](\d{1,2})[-_](\d{1,2})[ _-](\d{1,2})[-_](\d{2})(?:[-_](\d{2}))?")]
    private static partial Regex IsoFilenameTimestampRegex();

    [GeneratedRegex(@"(\d{1,2})[-_](\d{1,2})[-_](\d{4})[ _-](\d{1,2})[-_](\d{2})(?:[-_](\d{2}))?")]
    private static partial Regex AlternateFilenameTimestampRegex();

    private readonly IGameRepository _games;
    private readonly IVodRepository _vods;
    private readonly IConfigService _config;
    private readonly ILogger<VodService> _logger;

    public VodService(
        IGameRepository games,
        IVodRepository vods,
        IConfigService config,
        ILogger<VodService> logger)
    {
        _games = games;
        _vods = vods;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<List<VodRecordingInfo>> FindRecordingsAsync(string? folder = null)
    {
        folder ??= _config.AscentFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return Task.FromResult(new List<VodRecordingInfo>());

        return Task.Run(() =>
        {
            var recordings = new List<VodRecordingInfo>();

            foreach (var file in EnumerateVideoFilesSafe(folder))
            {
                try
                {
                    var startTs = ParseFilenameTimestamp(file.Name);
                    var mtime = new DateTimeOffset(file.LastWriteTime).ToUnixTimeSeconds();

                    recordings.Add(new VodRecordingInfo(
                        Path: file.FullName,
                        Name: file.Name,
                        Size: file.Length,
                        Mtime: mtime,
                        StartTs: startTs,
                        MtimeStr: file.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not stat {File}", file.FullName);
                }
            }

            // Sort by start_ts if available, otherwise mtime — newest first
            recordings.Sort((a, b) =>
            {
                var aKey = a.StartTs ?? a.Mtime;
                var bKey = b.StartTs ?? b.Mtime;
                return bKey.CompareTo(aKey);
            });

            return recordings;
        });
    }

    /// <inheritdoc />
    public string? MatchRecordingToGame(GameStats game, IReadOnlyList<VodRecordingInfo> recordings,
        IReadOnlySet<string>? excludePaths = null)
    {
        if (game.Timestamp == 0) return null;

        string? bestPath = null;
        double bestDelta = double.MaxValue;

        foreach (var rec in recordings)
        {
            if (excludePaths is not null && excludePaths.Contains(rec.Path))
                continue;

            var delta = TryComputeMatchDelta(game, rec);
            if (delta is null)
            {
                _logger.LogDebug("VOD candidate {File} rejected for game {GameId}: outside match window",
                    rec.Name, game.GameId);
                continue;
            }

            if (delta.Value < bestDelta)
            {
                bestDelta = delta.Value;
                bestPath = rec.Path;
            }
        }

        return bestPath;
    }

    /// <inheritdoc />
    public async Task<bool> TryLinkRecordingAsync(GameStats game, string? folder = null)
    {
        if (game.GameId <= 0)
        {
            return false;
        }

        var existing = await _vods.GetVodAsync(game.GameId).ConfigureAwait(false);
        if (existing != null
            && File.Exists(existing.FilePath))
        {
            return true;
        }

        // Exclude files already linked to other games
        var allVods = await _vods.GetAllVodsAsync().ConfigureAwait(false);
        var recordings = await FindRecordingsForLinkAsync(folder, allVods, game.GameId).ConfigureAwait(false);
        if (recordings.Count == 0)
        {
            return false;
        }

        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in allVods)
        {
            if (v.GameId != game.GameId)
                usedPaths.Add(v.FilePath);
        }

        var matchedPath = MatchRecordingToGame(game, recordings, usedPaths);
        if (matchedPath is null)
        {
            return false;
        }

        try
        {
            var fi = new FileInfo(matchedPath);
            await _vods.LinkVodAsync(game.GameId, matchedPath, fi.Length).ConfigureAwait(false);
            _logger.LogInformation("Linked VOD {File} to game {GameId}", fi.Name, game.GameId);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Refused to link VOD to game {GameId}", game.GameId);
            return false;
        }
    }

    private async Task<List<VodRecordingInfo>> FindRecordingsForLinkAsync(
        string? requestedFolder,
        IReadOnlyList<VodSummary> existingVods,
        long gameId)
    {
        var folders = GetCandidateRecordingFolders(requestedFolder, existingVods, gameId).ToList();
        if (folders.Count == 0)
        {
            return [];
        }

        if (folders.Count == 1)
        {
            return await FindRecordingsAsync(folders[0]).ConfigureAwait(false);
        }

        var recordings = new List<VodRecordingInfo>();
        foreach (var folder in folders)
        {
            var found = await FindRecordingsAsync(folder).ConfigureAwait(false);
            recordings.AddRange(found);
        }

        recordings.Sort((a, b) =>
        {
            var aKey = a.StartTs ?? a.Mtime;
            var bKey = b.StartTs ?? b.Mtime;
            return bKey.CompareTo(aKey);
        });

        return recordings;
    }

    private IEnumerable<string> GetCandidateRecordingFolders(
        string? requestedFolder,
        IReadOnlyList<VodSummary> existingVods,
        long gameId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TryAddFolder(requestedFolder, seen, out var explicitFolder))
        {
            yield return explicitFolder;
            yield break;
        }

        if (TryAddFolder(_config.AscentFolder, seen, out var configuredFolder))
        {
            yield return configuredFolder;
            yield break;
        }

        // If config was cleared or lost, reuse folders we have already linked
        // from. This covers a common failure mode where the VOD is present in
        // the user's Ascent directory but the app no longer has the setting.
        var yieldedKnownFolder = false;
        foreach (var vod in existingVods)
        {
            if (vod.GameId == gameId || string.IsNullOrWhiteSpace(vod.FilePath))
            {
                continue;
            }

            var folder = Path.GetDirectoryName(vod.FilePath);
            if (TryAddFolder(folder, seen, out var knownFolder))
            {
                _logger.LogInformation("Using known VOD folder {Folder} while linking game {GameId}", knownFolder, gameId);
                yieldedKnownFolder = true;
                yield return knownFolder;
            }
        }

        if (!yieldedKnownFolder && TryAddFolder(GetDefaultAscentFolder(), seen, out var defaultFolder))
        {
            _logger.LogInformation("Using default Ascent folder {Folder} while linking game {GameId}", defaultFolder, gameId);
            yield return defaultFolder;
        }
    }

    private static bool TryAddFolder(string? folder, HashSet<string> seen, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            var candidate = Path.GetFullPath(folder);
            if (!Directory.Exists(candidate) || !seen.Add(candidate))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetDefaultAscentFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!string.IsNullOrWhiteSpace(videos))
        {
            return Path.Combine(videos, "Ascent");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Videos",
            "Ascent");
    }

    /// <inheritdoc />
    public async Task<int> AutoMatchRecordingsAsync()
    {
        var folder = _config.AscentFolder;
        if (string.IsNullOrEmpty(folder))
        {
            _logger.LogWarning("VOD scan skipped: no Ascent folder configured");
            return 0;
        }

        _logger.LogInformation("VOD scan starting — folder: {Folder}", folder);

        var recordings = await FindRecordingsAsync(folder).ConfigureAwait(false);
        _logger.LogInformation("VOD scan found {Count} recordings in {Folder}", recordings.Count, folder);
        if (recordings.Count == 0) return 0;

        // Get all VODs to find which games already have one and which paths are taken
        var allVods = await _vods.GetAllVodsAsync().ConfigureAwait(false);
        var linkedGameIds = new HashSet<long>();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var vod in allVods)
        {
            linkedGameIds.Add(vod.GameId);
            usedPaths.Add(vod.FilePath);
        }

        _logger.LogInformation("VOD scan: {Linked} games already have linked VODs", linkedGameIds.Count);

        // Get recent games to try matching — use larger limit to catch more
        var recentGames = await _games.GetRecentAsync(limit: 100).ConfigureAwait(false);
        var unmatchedGames = recentGames
            .Where(g => !linkedGameIds.Contains(g.GameId))
            .ToList();

        _logger.LogInformation("VOD scan: {Total} recent games, {Unmatched} without VODs",
            recentGames.Count, unmatchedGames.Count);

        if (unmatchedGames.Count == 0) return 0;

        // Build all viable (delta, game, recording) candidates, then assign by ascending delta
        // so the closest match wins globally instead of greedy-per-game-order.
        var candidates = new List<(double Delta, GameStats Game, VodRecordingInfo Recording)>();
        foreach (var game in unmatchedGames)
        {
            foreach (var rec in recordings)
            {
                if (usedPaths.Contains(rec.Path)) continue;
                var delta = TryComputeMatchDelta(game, rec);
                if (delta is null) continue;
                candidates.Add((delta.Value, game, rec));
            }
        }

        candidates.Sort((a, b) => a.Delta.CompareTo(b.Delta));

        int matched = 0;
        var matchedGameIds = new HashSet<long>();

        foreach (var (delta, game, rec) in candidates)
        {
            if (matchedGameIds.Contains(game.GameId)) continue;
            if (usedPaths.Contains(rec.Path)) continue;

            try
            {
                var fi = new FileInfo(rec.Path);
                await _vods.LinkVodAsync(game.GameId, rec.Path, fi.Length).ConfigureAwait(false);
                matchedGameIds.Add(game.GameId);
                usedPaths.Add(rec.Path);
                matched++;
                _logger.LogInformation("Auto-matched VOD {File} to game {GameId} (delta={Delta}s)",
                    fi.Name, game.GameId, (int)delta);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to link VOD to game {GameId}", game.GameId);
            }
        }

        _logger.LogInformation("Auto-matched {Count} recordings to games", matched);
        return matched;
    }

    // ── Private helpers ─────────────────────────────────────────────

    /// <summary>
    /// Score how well a recording matches a game; lower is better, null = no match.
    ///
    /// Robust by construction: a recording's true span is [StartTs (filename) ..
    /// Mtime (last write = encode end)]. The real recording for a game must
    /// CONTAIN the game's play window. We never assume which reference the game's
    /// stored Timestamp uses — historically the live-capture path stored game END
    /// while the match-history path stored game START — so we test the game window
    /// under BOTH interpretations and accept a recording that contains either.
    ///
    /// This is what makes wrong matches (e.g. a short Valorant clip, or a
    /// neighbouring game's file) impossible: a recording that doesn't span the
    /// game's full duration can't win, regardless of how close its start is.
    /// Falls back to the legacy filename/mtime proximity test only when the
    /// recording's span can't be established.
    /// </summary>
    private static double? TryComputeMatchDelta(GameStats game, VodRecordingInfo rec)
    {
        if (game.Timestamp == 0) return null;

        var gameDur = Math.Max(game.GameDuration, 0);

        // Establish the recording's true span. StartTs is the filename time
        // (recording start); Mtime is the file's last-write time (recording end).
        // When the filename couldn't be parsed, fall back to legacy proximity.
        if (rec.StartTs is null)
        {
            return LegacyProximityDelta(game, rec, gameDur);
        }

        var recStart = rec.StartTs.Value;
        var recEnd = rec.Mtime > recStart ? rec.Mtime : recStart; // guard bad mtime
        var recSpan = recEnd - recStart;

        // The game's stored Timestamp is either its start or its end. Compute the
        // candidate START under each interpretation and try to contain the window.
        // slack absorbs encode-finalisation lag and champ-select/loading lead-in.
        const double edgeSlack = 300;          // 5 min tolerance on each edge
        double? best = null;
        foreach (var candidateStart in new[] { game.Timestamp, game.Timestamp - gameDur })
        {
            var candidateEnd = candidateStart + gameDur;

            // Recording must cover the game window (within slack) AND be at least
            // roughly as long as the game (it records champ-select → game end, so
            // it's normally longer; allow a little under for rounding).
            var coversStart = recStart <= candidateStart + edgeSlack;
            var coversEnd = recEnd >= candidateEnd - edgeSlack;
            var longEnough = recSpan >= gameDur - edgeSlack;
            if (!coversStart || !coversEnd || !longEnough) continue;

            // Score: tightest container wins — how much longer the recording is
            // than the game, plus how far its start leads the game start. Both
            // small = the recording that actually wraps this game.
            var slackBefore = Math.Max(0, candidateStart - recStart);
            var slackAfter = Math.Max(0, recEnd - candidateEnd);
            var score = slackBefore + slackAfter;
            if (best is null || score < best) best = score;
        }

        if (best is not null) return best;

        // No containment under either interpretation → not this game's recording.
        return null;
    }

    /// <summary>
    /// Legacy proximity fallback for recordings whose filename timestamp couldn't
    /// be parsed (so we only have file mtime ≈ recording end). Compares mtime to
    /// the game end under both timestamp interpretations.
    /// </summary>
    private static double? LegacyProximityDelta(GameStats game, VodRecordingInfo rec, double gameDur)
    {
        double? best = null;
        // game.Timestamp as START → end = ts + dur; as END → end = ts.
        foreach (var gameEnd in new[] { game.Timestamp + gameDur, (double)game.Timestamp })
        {
            var signedDelta = rec.Mtime - gameEnd;
            if (signedDelta < -GameConstants.VodMtimeGraceS) continue;
            if (Math.Abs(signedDelta) >= GameConstants.VodMatchWindowS) continue;
            var d = Math.Abs(signedDelta);
            if (best is null || d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// Extract a unix timestamp from an Ascent filename like '03-01-2026-14-43.mp4'.
    /// Returns the timestamp as a double, or null if the filename doesn't match.
    /// </summary>
    private static double? ParseFilenameTimestamp(string filename)
    {
        if (TryParseTimestamp(FilenameTimestampRegex().Match(filename), out var parsedTimestamp, monthFirst: true))
        {
            return parsedTimestamp;
        }

        if (TryParseTimestamp(IsoFilenameTimestampRegex().Match(filename), out parsedTimestamp, isoOrder: true))
        {
            return parsedTimestamp;
        }

        if (TryParseTimestamp(AlternateFilenameTimestampRegex().Match(filename), out parsedTimestamp, monthFirst: true))
        {
            return parsedTimestamp;
        }

        return null;
    }

    private static bool TryParseTimestamp(
        Match match,
        out double timestamp,
        bool monthFirst = false,
        bool isoOrder = false)
    {
        timestamp = 0;
        if (!match.Success)
        {
            return false;
        }

        try
        {
            int year;
            int month;
            int day;

            if (isoOrder)
            {
                year = int.Parse(match.Groups[1].Value);
                month = int.Parse(match.Groups[2].Value);
                day = int.Parse(match.Groups[3].Value);
            }
            else if (monthFirst)
            {
                month = int.Parse(match.Groups[1].Value);
                day = int.Parse(match.Groups[2].Value);
                year = int.Parse(match.Groups[3].Value);
            }
            else
            {
                return false;
            }

            var hour = int.Parse(match.Groups[4].Value);
            var minute = int.Parse(match.Groups[5].Value);
            var second = match.Groups.Count > 6 && match.Groups[6].Success
                ? int.Parse(match.Groups[6].Value)
                : 0;

            var dt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
            timestamp = new DateTimeOffset(dt).ToUnixTimeSeconds();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<FileInfo> EnumerateVideoFilesSafe(string rootFolder)
    {
        var pending = new Stack<string>();
        pending.Push(rootFolder);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            DirectoryInfo currentDir;
            try
            {
                currentDir = new DirectoryInfo(current);
            }
            catch
            {
                continue;
            }

            FileInfo[] files;
            try
            {
                files = currentDir.GetFiles();
            }
            catch
            {
                files = [];
            }

            foreach (var file in files)
            {
                if (VideoExtensions.Contains(file.Extension))
                {
                    yield return file;
                }
            }

            DirectoryInfo[] directories;
            try
            {
                directories = currentDir.GetDirectories();
            }
            catch
            {
                directories = [];
            }

            foreach (var directory in directories)
            {
                pending.Push(directory.FullName);
            }
        }
    }
}
