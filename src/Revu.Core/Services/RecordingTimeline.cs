using System.Text.Json;
using Revu.Core.Data;

namespace Revu.Core.Services;

public sealed record RecordingTiming(int Schema, long GameId, string FileName, double GameTimeAtVideoStart);

/// <summary>Native VOD media zero may precede game zero (the loading screen).</summary>
public static class RecordingTimeline
{
    public const string Suffix = ".revu-timing.json";
    public static bool IsValidOffset(double value) => double.IsFinite(value) && value is >= -600 and <= 7200;

    public static double ReadGameTimeAtVideoStart(string videoPath, long? expectedGameId = null)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return 0;
        var path = videoPath + Suffix;
        // Legacy/imported VODs and extracted clips have no companion and retain their
        // established game-zero convention. Reading never creates or repairs metadata.
        if (!File.Exists(path)) return 0;
        DataRootLease.Canonicalize(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Recording timing cannot be a symbolic link.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4096) throw new InvalidDataException("Recording timing is too large.");
        RecordingTiming? timing;
        try { timing = JsonSerializer.Deserialize<RecordingTiming>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException ex) { throw new InvalidDataException("Recording timing is invalid.", ex); }
        if (timing is null || timing.Schema != 1 || timing.GameId <= 0
            || (expectedGameId.HasValue && timing.GameId != expectedGameId)
            || !Path.GetFileName(videoPath).Equals(timing.FileName, StringComparison.OrdinalIgnoreCase)
            || !IsValidOffset(timing.GameTimeAtVideoStart))
            throw new InvalidDataException("Recording timing does not match this video.");
        return timing.GameTimeAtVideoStart;
    }

    public static (double Start, double End) ToMediaRange(string videoPath, double gameStart, double gameEnd)
    {
        if (!double.IsFinite(gameStart) || !double.IsFinite(gameEnd) || gameStart < 0 || gameEnd <= gameStart)
            throw new ArgumentException("Game clip range is invalid.");
        var offset = ReadGameTimeAtVideoStart(videoPath);
        var start = gameStart - offset;
        var end = gameEnd - offset;
        if (end <= 0) throw new ArgumentException("This game moment precedes the recording.");
        return (Math.Max(0, start), end);
    }
}
