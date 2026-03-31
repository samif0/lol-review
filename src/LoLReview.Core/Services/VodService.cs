#nullable enable

using System.Globalization;
using System.Text.RegularExpressions;
using LoLReview.Core.Constants;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

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
    public string? MatchRecordingToGame(GameStats game, IReadOnlyList<VodRecordingInfo> recordings)
    {
        if (game.Timestamp == 0) return null;

        string? bestPath = null;
        double bestDelta = double.MaxValue;

        foreach (var rec in recordings)
        {
            double delta;

            if (rec.StartTs is not null)
            {
                // Filename-based: compare recording start vs game start.
                // game.Timestamp is the game creation/start time from Riot API.
                delta = Math.Abs(rec.StartTs.Value - game.Timestamp);
            }
            else
            {
                // mtime fallback: compare file mtime vs game end
                // game end ≈ game start + duration
                var gameEnd = game.Timestamp + game.GameDuration;
                var signedDelta = rec.Mtime - gameEnd;
                // Recording mtime should be near or after game end (allow grace period)
                if (signedDelta < -GameConstants.VodMtimeGraceS)
                    continue;
                delta = Math.Abs(signedDelta);
            }

            if (delta < GameConstants.VodMatchWindowS && delta < bestDelta)
            {
                bestDelta = delta;
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

        var recordings = await FindRecordingsAsync(folder).ConfigureAwait(false);
        if (recordings.Count == 0)
        {
            return false;
        }

        var matchedPath = MatchRecordingToGame(game, recordings);
        if (matchedPath is null)
        {
            return false;
        }

        var fi = new FileInfo(matchedPath);
        await _vods.LinkVodAsync(game.GameId, matchedPath, fi.Length).ConfigureAwait(false);
        _logger.LogInformation("Linked VOD {File} to game {GameId}", fi.Name, game.GameId);
        return true;
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

        // Get all VODs to find which games already have one
        var allVods = await _vods.GetAllVodsAsync().ConfigureAwait(false);
        var linkedGameIds = new HashSet<long>();
        foreach (var vod in allVods)
        {
            linkedGameIds.Add(vod.GameId);
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

        int matched = 0;
        var matchedGameIds = new HashSet<long>();

        foreach (var game in unmatchedGames)
        {
            if (matchedGameIds.Contains(game.GameId)) continue;

            var matchedPath = MatchRecordingToGame(game, recordings);
            if (matchedPath is null) continue;

            try
            {
                var fi = new FileInfo(matchedPath);
                await _vods.LinkVodAsync(game.GameId, matchedPath, fi.Length).ConfigureAwait(false);
                matchedGameIds.Add(game.GameId);
                matched++;
                _logger.LogInformation("Auto-matched VOD {File} to game {GameId}", fi.Name, game.GameId);
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
