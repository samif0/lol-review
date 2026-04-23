#nullable enable

using Revu.App.Helpers;
using Revu.Core.Data;
using Microsoft.Extensions.Logging;

namespace Revu.App.Startup;

/// <summary>
/// Runs before every other startup task. Migrates <c>%LOCALAPPDATA%\LoLReviewData\</c>
/// to <c>%LOCALAPPDATA%\RevuData\</c> when upgrading from pre-Revu builds.
/// Idempotent and safe to run on every launch.
/// </summary>
internal sealed class AppDataFolderMigrationStartupTask : IStartupTask
{
    private readonly ILogger<AppDataFolderMigrationStartupTask> _logger;

    public AppDataFolderMigrationStartupTask(ILogger<AppDataFolderMigrationStartupTask> logger)
    {
        _logger = logger;
    }

    public string Name => "appdata-folder-migration";

    public Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var result = AppDataMigrator.RunIfNeeded(_logger);
        AppDiagnostics.WriteVerbose("startup.log", $"AppData migration result: {result}");
        return Task.CompletedTask;
    }
}
