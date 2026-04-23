#nullable enable

using Revu.App.Helpers;
using Revu.Core.Data;

namespace Revu.App.Startup;

internal sealed class LegacyDatabaseMigrationStartupTask : IStartupTask
{
    private readonly LegacyDatabaseMigrationService _legacyDatabaseMigrationService;

    public LegacyDatabaseMigrationStartupTask(LegacyDatabaseMigrationService legacyDatabaseMigrationService)
    {
        _legacyDatabaseMigrationService = legacyDatabaseMigrationService;
    }

    public string Name => "legacy-db-migration";

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var migratedFrom = _legacyDatabaseMigrationService.TryMigrate();
        if (migratedFrom is not null)
        {
            AppDiagnostics.WriteVerbose("startup.log", $"Migrated legacy DB from: {migratedFrom}");
        }

        return Task.CompletedTask;
    }
}
