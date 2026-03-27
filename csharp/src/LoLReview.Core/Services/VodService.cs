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
            var root = new DirectoryInfo(folder);

            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (!VideoExtensions.Contains(file.Extension))
                    continue;

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
                // game.Timestamp is actually game END time, derive start by subtracting duration.
                var gameStart = game.Timestamp - game.GameDuration;
                delta = Math.Abs(rec.StartTs.Value - gameStart);
            }
            else
            {
                // mtime fallback: compare file mtime vs game end
                var gameEnd = game.Timestamp + game.GameDuration;
                var signedDelta = rec.Mtime - gameEnd;
                // Recording should be AFTER game end (allow grace period)
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
    public async Task<int> AutoMatchRecordingsAsync()
    {
        var folder = _config.AscentFolder;
        if (string.IsNullOrEmpty(folder)) return 0;

        var recordings = await FindRecordingsAsync(folder).ConfigureAwait(false);
        if (recordings.Count == 0) return 0;

        // Get all VODs to find which games already have one
        var allVods = await _vods.GetAllVodsAsync().ConfigureAwait(false);
        var linkedGameIds = new HashSet<long>();
        foreach (var vod in allVods)
        {
            if (vod.TryGetValue("game_id", out var gid) && gid is long id)
                linkedGameIds.Add(id);
        }

        // Get recent games to try matching
        var recentGames = await _games.GetRecentAsync(limit: 50).ConfigureAwait(false);
        var unmatchedGames = recentGames
            .Where(g => !linkedGameIds.Contains(g.GameId))
            .ToList();

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
        var match = FilenameTimestampRegex().Match(filename);
        if (!match.Success) return null;

        try
        {
            var month = int.Parse(match.Groups[1].Value);
            var day = int.Parse(match.Groups[2].Value);
            var year = int.Parse(match.Groups[3].Value);
            var hour = int.Parse(match.Groups[4].Value);
            var minute = int.Parse(match.Groups[5].Value);

            var dt = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
            return new DateTimeOffset(dt).ToUnixTimeSeconds();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
