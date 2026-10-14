#nullable enable

using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Whether a saved pattern moment can still be watched, decided the same way for the
/// Patterns page and the review nudge. vod_files rows can outlive their recordings
/// (files moved or removed), so the disk is the authority, as in GamesSnapshotBuilder.
/// A recording whose timing companion cannot be read degrades to no recording, the
/// way the page always treated it, and so does one that starts after the moment ends
/// (Revu or the recorder started late). A kept clip outlives its game's recording.
///
/// <para>Each distinct path is checked once per probe: a playlist's moments mostly
/// share their game's recording. Not thread-safe; callers own one probe per build or
/// serialize their use of it.</para>
/// </summary>
public sealed class PatternMediaProbe
{
    private readonly Dictionary<string, bool> _onDisk = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double?> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, bool> _fileExists;

    public PatternMediaProbe(Func<string, bool>? fileExists = null)
    {
        _fileExists = fileExists ?? File.Exists;
    }

    /// <summary>Whether the moment's recording can play it, and the recording's
    /// game-clock offset. False when the file is gone, its timing is unreadable, or the
    /// moment's window ends before the recording starts.</summary>
    public (bool Playable, double GameTimeAtVideoStart) Recording(PatternMoment moment)
    {
        if (!OnDisk(moment.VodPath)) return (false, 0);
        var key = $"{moment.GameId}|{moment.VodPath}";
        if (!_offsets.TryGetValue(key, out var offset))
        {
            try { offset = RecordingTimeline.ReadGameTimeAtVideoStart(moment.VodPath, moment.GameId); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { offset = null; }
            _offsets[key] = offset;
        }
        if (offset is not double start) return (false, 0);
        // The page plays [max(start, offset), end] and says the moment falls outside the
        // recording when that is empty; such a moment has nothing to watch here.
        var (from, to) = Window(moment);
        if (from is int windowStart && to is int windowEnd && windowEnd > windowStart && windowEnd <= start)
            return (false, 0);
        return (true, start);
    }

    /// <summary>The moment's playback window in game seconds, as the Patterns page plays
    /// it: a bookmark previews up to 15 seconds either side of its point, within the game.
    /// Null bounds for a start-less, game-level moment.</summary>
    public static (int? Start, int? End) Window(PatternMoment moment)
    {
        if (moment.SourceKind != "bookmark" || moment.StartTimeSeconds is not int point)
            return (moment.StartTimeSeconds, moment.EndTimeSeconds);
        var upperBound = moment.GameDurationSeconds > 0 ? moment.GameDurationSeconds : int.MaxValue;
        point = Math.Clamp(point, 0, upperBound);
        return (Math.Max(0, point - 15), (int)Math.Min(upperBound, (long)point + 15));
    }

    /// <summary>The kept clip file is still on disk.</summary>
    public bool HasClip(PatternMoment moment) => OnDisk(moment.ClipPath);

    /// <summary>Something can play this moment: its recording or its kept clip.</summary>
    public bool IsWatchable(PatternMoment moment) => Recording(moment).Playable || HasClip(moment);

    private bool OnDisk(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!_onDisk.TryGetValue(path, out var exists))
        {
            exists = _fileExists(path);
            _onDisk[path] = exists;
        }
        return exists;
    }
}
