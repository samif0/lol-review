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

    /// <summary>
    /// <see cref="ScanAsync(CancellationToken, IReadOnlySet{long})"/> that awaits
    /// <paramref name="beforeFirstLink"/> once, right before the first recording is linked
    /// (the scan's only database write), so a scan that links nothing takes no backup.
    /// </summary>
    Task<VodScanResult> ScanAsync(CancellationToken cancellationToken, IReadOnlySet<long>? reservedGameIds,
        Func<Task> beforeFirstLink) => ScanAsync(cancellationToken, reservedGameIds);
}

public record VodRecordingInfo(string Path, string Name, long Size, double Mtime, double? StartTs,
    string MtimeStr, bool IsReady = true);

public sealed record VodScanResult(bool Ok, int Matched, int RecordingCount, string Message)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<long> LinkedGameIds { get; init; } = [];
}
