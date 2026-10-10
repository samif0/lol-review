#nullable enable

using Revu.Core.Constants;

namespace Revu.Sidecar;

/// <summary>
/// Pure validation for the narration and clip-share request bodies (C1 limits, C10 copy).
/// </summary>
internal static class NarrationRequestValidator
{
    public const int OffsetMinMs = -10000;
    public const int OffsetMaxMs = 10000;
    public const int DurationMinMs = 1000;
    public const int DurationMaxMs = 625000;
    public const double GameVolumeMax = 1.5;
    public const double NarrationVolumeMax = 2.0;
    public const long AudioMaxBytes = 33554432;
    private static readonly byte[] EbmlMagic = [0x1A, 0x45, 0xDF, 0xA3];

    public const string ClipTooLongExtract = "Clips can be up to 10 minutes. Trim the range and try again.";
    public const string ClipTooLongShare = "Clips can be up to 10 minutes. Trim the range and save a new clip.";
    public const string ClipFileMissing = "Clip file is missing. Save the clip again.";
    public const string ShareTypeUnsupported = "Only MP4 and WebM clips can be shared.";
    public const string ShareTooLarge = "Clip is too large to share (2 GB max).";
    public const string RenderFailed = "Revu could not render the narrated clip. Your recording was not saved.";
    public const string AlreadyRendering = "This clip is already rendering.";
    public const string NoNarration = "No narration for this clip.";
    public const string ShuttingDown = "Revu is shutting down.";
    public const string ExtractLongFailed = "Revu could not save this clip. Try a shorter range.";

    /// <summary>422 text for an extract range over 10 minutes, else null.</summary>
    public static string? ExtractRangeError(int startS, int endS) =>
        endS - startS > GameConstants.MaxClipSeconds ? ClipTooLongExtract : null;

    /// <summary>Validate a save body; on success <paramref name="narrationId"/> is the parsed GUID.</summary>
    public static string? ValidateSave(SaveNarrationBody? body, out Guid narrationId)
    {
        narrationId = Guid.Empty;
        if (body is null || body.GameId <= 0 || body.BookmarkId <= 0) return "gameId and bookmarkId required";
        // Lowercase D format only: the id becomes a file name, so no other spelling passes.
        if (string.IsNullOrEmpty(body.NarrationId)
            || !Guid.TryParseExact(body.NarrationId, "D", out var parsed)
            || !string.Equals(body.NarrationId, parsed.ToString("D"), StringComparison.Ordinal))
            return "Invalid narration id.";
        if (string.IsNullOrEmpty(body.MimeType) || !body.MimeType.StartsWith("audio/webm", StringComparison.Ordinal))
            return "Narration audio must be WebM.";
        if (body.DurationMs is < DurationMinMs or > DurationMaxMs) return "Narration length is out of range.";
        var mixError = ValidateMix(body.OffsetMs, body.GameVolume, body.NarrationVolume);
        if (mixError is not null) return mixError;
        narrationId = parsed;
        return null;
    }

    public static string? ValidateMix(MixNarrationBody? body)
    {
        if (body is null || body.GameId <= 0 || body.BookmarkId <= 0) return "gameId and bookmarkId required";
        return ValidateMix(body.OffsetMs, body.GameVolume, body.NarrationVolume);
    }

    public static string? ValidateMix(int offsetMs, double gameVolume, double narrationVolume)
    {
        if (offsetMs is < OffsetMinMs or > OffsetMaxMs) return "Sync offset is out of range.";
        if (!double.IsFinite(gameVolume) || gameVolume < 0 || gameVolume > GameVolumeMax) return "Game volume is out of range.";
        if (!double.IsFinite(narrationVolume) || narrationVolume < 0 || narrationVolume > NarrationVolumeMax)
            return "Voice volume is out of range.";
        return null;
    }

    /// <summary>The voice track must exist, be at most 32 MiB and start with the EBML magic.</summary>
    public static string? ValidateAudioFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "Narration audio file not found.";
            if (info.Length > AudioMaxBytes || info.Length < EbmlMagic.Length) return "Narration audio file size is out of range.";
            Span<byte> head = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4 || !head.SequenceEqual(EbmlMagic))
                return "Narration audio is not a WebM file.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "Narration audio file could not be read.";
        }
    }

    /// <summary>
    /// Share request checks (C6 6.2 422s), in order: length, file present (narrated render
    /// first), container type, size. Null when the clip can be shared.
    /// </summary>
    public static string? ShareFileError(int? clipStartS, int? clipEndS, string narratedPathOnDisk, string clipPath,
        out string file)
    {
        file = "";
        if (clipStartS is { } start && clipEndS is { } end && end - start > GameConstants.MaxClipSeconds)
            return ClipTooLongShare;
        if (narratedPathOnDisk.Length > 0) file = narratedPathOnDisk;
        else if (NarrationDtos.FileOnDisk(clipPath).Length > 0) file = clipPath;
        else return ClipFileMissing;
        var ext = Path.GetExtension(file);
        if (!ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".webm", StringComparison.OrdinalIgnoreCase))
            return ShareTypeUnsupported;
        try
        {
            if (new FileInfo(file).Length > 2147483648L) return ShareTooLarge;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ClipFileMissing;
        }
        return null;
    }
}
