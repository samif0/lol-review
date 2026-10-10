#nullable enable

using System.Text.Json;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// C6 NarrationDto: a clip's narration as the VOD player sees it. NarratedClipPath is the
/// absolute render path when the file is on disk, else "". Transcript is the C2 document,
/// or null while none is stored (or the stored JSON is invalid).
/// </summary>
public sealed record NarrationDto(
    long BookmarkId,
    string NarratedClipPath,
    int DurationMs,
    int OffsetMs,
    double GameVolume,
    double NarrationVolume,
    bool Duck,
    string TranscriptStatus,
    string TranscriptLanguage,
    string TranscriptError,
    TranscriptDocument? Transcript,
    long UpdatedAt);

public static class NarrationDtos
{
    public static NarrationDto Map(ClipNarrationRecord r) => new(
        BookmarkId: r.BookmarkId,
        NarratedClipPath: FileOnDisk(r.NarratedClipPath),
        DurationMs: r.DurationMs,
        OffsetMs: r.OffsetMs,
        GameVolume: r.GameVolume,
        NarrationVolume: r.NarrationVolume,
        Duck: r.Duck,
        TranscriptStatus: r.TranscriptStatus,
        TranscriptLanguage: r.TranscriptLanguage,
        TranscriptError: r.TranscriptError,
        Transcript: TranscriptDocument.TryParse(r.TranscriptJson),
        UpdatedAt: r.UpdatedAt);

    public static NarrationDto? MapOrNull(ClipNarrationRecord? r) => r is null ? null : Map(r);

    /// <summary>The path when it names an existing file, else "".</summary>
    public static string FileOnDisk(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return File.Exists(path) ? path : ""; }
        catch { return ""; }
    }
}

/// <summary>
/// A handler outcome (HTTP status + JSON body) kept separate from ASP.NET so the narration
/// and share request logic can be tested without a host.
/// </summary>
internal readonly record struct ApiReply(int Status, object Body)
{
    public static ApiReply Ok(object body) => new(200, body);
    public static ApiReply Error(int status, string error) => new(status, new { ok = false, error });

    public IResult ToResult(JsonSerializerOptions json) => Results.Json(Body, json, statusCode: Status);
}
