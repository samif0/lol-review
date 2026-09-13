#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>Optional discovery of externally recorded Ascent matches. Never records or replaces media.</summary>
public interface IVodService
{
    Task<List<VodRecordingInfo>> FindRecordingsAsync(string? folder = null, CancellationToken cancellationToken = default);
    string? MatchRecordingToGame(GameStats game, IReadOnlyList<VodRecordingInfo> recordings,
        IReadOnlySet<string>? excludePaths = null);
    Task<bool> TryLinkRecordingAsync(GameStats game, string? folder = null, CancellationToken cancellationToken = default);
    Task<int> AutoMatchRecordingsAsync(CancellationToken cancellationToken = default);
    Task<VodScanResult> ScanAsync(CancellationToken cancellationToken = default, IReadOnlySet<long>? reservedGameIds = null);
}

public record VodRecordingInfo(string Path, string Name, long Size, double Mtime, double? StartTs,
    string MtimeStr, bool IsReady = true);

public sealed record VodScanResult(bool Ok, int Matched, int RecordingCount, string Message)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<long> LinkedGameIds { get; init; } = [];
}
