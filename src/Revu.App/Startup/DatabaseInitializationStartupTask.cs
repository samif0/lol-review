#nullable enable

using Revu.App.Helpers;
using Revu.Core.Data;

namespace Revu.App.Startup;

internal sealed class DatabaseInitializationStartupTask : IStartupTask
{
    private readonly DatabaseInitializer _databaseInitializer;
    private readonly IDbConnectionFactory _connectionFactory;

    public DatabaseInitializationStartupTask(
        DatabaseInitializer databaseInitializer,
        IDbConnectionFactory connectionFactory)
    {
        _databaseInitializer = databaseInitializer;
        _connectionFactory = connectionFactory;
    }

    public string Name => "database-initialization";

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await _databaseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var connection = _connectionFactory.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM games";
            var gameCount = command.ExecuteScalar();

            AppDiagnostics.WriteVerbose(
                "startup.log",
                $"DB path: {_connectionFactory.DatabasePath}; " +
                $"DB game count: {gameCount}; " +
                $"DB file size: {new FileInfo(_connectionFactory.DatabasePath).Length} bytes");
        }
        catch (Exception diagnosticException)
        {
            AppDiagnostics.WriteVerbose("startup.log", $"DB diagnostic failed: {diagnosticException.Message}");
        }
    }
}
