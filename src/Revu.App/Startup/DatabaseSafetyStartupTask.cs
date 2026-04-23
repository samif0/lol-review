#nullable enable

using Revu.Core.Data;
using Revu.Core.Services;

namespace Revu.App.Startup;

internal sealed class DatabaseSafetyStartupTask : IStartupTask
{
    private readonly DatabaseIntegrityChecker _databaseIntegrityChecker;
    private readonly IBackupService _backupService;

    public DatabaseSafetyStartupTask(
        DatabaseIntegrityChecker databaseIntegrityChecker,
        IBackupService backupService)
    {
        _databaseIntegrityChecker = databaseIntegrityChecker;
        _backupService = backupService;
    }

    public string Name => "database-safety";

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _databaseIntegrityChecker.RunPreFlightChecks();
        await _backupService.CreateSafetyBackupAsync("pre-migration startup").ConfigureAwait(false);
        await _backupService.RunBackupAsync().ConfigureAwait(false);
    }
}
