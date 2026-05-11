#nullable enable

namespace Revu.Core.Services;

public interface IReviewExportService
{
    Task<string> ExportAllAsync(CancellationToken cancellationToken = default);

    Task<string?> ExportGameAsync(long gameId, CancellationToken cancellationToken = default);
}
