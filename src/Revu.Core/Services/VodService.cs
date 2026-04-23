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

        var recordings = await FindRecordingsAsync(folder).ConfigureAwait(false);
        if (recordings.Count == 0)
        {
            return false;
        }

        // Exclude files already linked to other games
        var allVods = await _vods.GetAllVodsAsync().ConfigureAwait(false);
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
    /// Compute the match delta between a game and a recording, or null if outside the window.
    /// Uses asymmetric bounds for filename branch: Ascent starts recording before gameCreation
    /// (queue/champ select), so negative deltas up to VodMatchWindowS are normal, but positive
    /// deltas (recording after game creation) are capped at VodFilenamePositiveSlackS.
    /// </summary>
    private static double? TryComputeMatchDelta(GameStats game, VodRecordingInfo rec)
    {
        if (game.Timestamp == 0) return null;

        if (rec.StartTs is not null)
        {
            // signed = rec.start − game.start
            // negative → recording started before game (expected for Ascent queue/champ select)
            // positive → recording started after game creation (rare jitter only)
            var signed = rec.StartTs.Value - game.Timestamp;
            if (signed < -GameConstants.VodMatchWindowS) return null;
            if (signed > GameConstants.VodFilenamePositiveSlackS) return null;
            return Math.Abs(signed);
        }

        // mtime fallback: compare file mtime vs game end
        var gameEnd = game.Timestamp + game.GameDuration;
        var signedDelta = rec.Mtime - gameEnd;
        if (signedDelta < -GameConstants.VodMtimeGraceS) return null;
        if (Math.Abs(signedDelta) >= GameConstants.VodMatchWindowS) return null;
        return Math.Abs(signedDelta);
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
