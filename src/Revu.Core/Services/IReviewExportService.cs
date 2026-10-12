#nullable enable

namespace Revu.Core.Services;

public interface IReviewExportService
{
    Task<string> ExportAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Write the full export into <paramref name="folder"/> (created when missing) as a new
    /// <c>revu-review-export-{yyyyMMdd-HHmm}.md</c>, never replacing an earlier export, and
    /// return where it went. Nothing is left behind when it fails or is cancelled.
    /// </summary>
    Task<ReviewExportFile> ExportAllToFolderAsync(string folder, CancellationToken cancellationToken = default);

    Task<string?> ExportGameAsync(long gameId, CancellationToken cancellationToken = default);
}

/// <summary>A saved full export: its full path, file name and how many games it holds.</summary>
public sealed record ReviewExportFile(string Path, string FileName, int GameCount);
