#nullable enable

using Microsoft.Data.Sqlite;
using Revu.Core.Services;

namespace Revu.Core.Data.Repositories;

public sealed record EventCorrectionSweepResult(int KeysStamped, int GamesTouched, int OrphansResolved);

/// <summary>
/// Rule G, the startup sweep: stamp event_key where a row predates the ledger, then let every
/// correction whose applied row vanished find its twin again. Capped per launch, one transaction
/// per game, and it never inserts a game_events row (rule A does the re-inserting).
/// </summary>
public sealed class EventCorrectionSweep
{
    private const string DanglingPredicate =
        "c.op NOT IN ('revert', 'remove') AND c.state IN ('active', 'absorbed', 'orphaned') " +
        "AND (c.applied_event_id IS NULL OR NOT EXISTS (SELECT 1 FROM game_events e WHERE e.id = c.applied_event_id))";

    private readonly IDbConnectionFactory _factory;

    public EventCorrectionSweep(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>NULL-key rows + applicable non-remove corrections whose applied row is missing. Cheap.</summary>
    public async Task<int> CountPendingAsync()
    {
        try
        {
            using var conn = _factory.CreateConnection();
            var unkeyed = await ScalarAsync(conn, "SELECT COUNT(*) FROM game_events WHERE event_key IS NULL");
            var dangling = await ScalarAsync(conn, $"SELECT COUNT(*) FROM event_corrections c WHERE {DanglingPredicate}");
            return (int)Math.Min(int.MaxValue, unkeyed + dangling);
        }
        catch (SqliteException ex)
        {
            CoreDiagnostics.WriteVerbose($"Event corrections: sweep count skipped: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Stamp keys (EventCorrectionsRepository.StampMissingEventKeysAsync(maxRows)), then
    /// ResolveOrphansAsync per game that has a dangling correction, each game in its own transaction,
    /// per-game try/catch. Never inserts game_events rows.</summary>
    public async Task<EventCorrectionSweepResult> RunAsync(int maxRows = 20_000, CancellationToken ct = default)
    {
        var stamped = await new EventCorrectionsRepository(_factory).StampMissingEventKeysAsync(maxRows);
        ct.ThrowIfCancellationRequested();

        var games = new List<long>();
        try
        {
            using var conn = _factory.CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT c.game_id FROM event_corrections c WHERE {DanglingPredicate} ORDER BY c.game_id DESC";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) games.Add(reader.GetInt64(0));
        }
        catch (SqliteException ex)
        {
            CoreDiagnostics.WriteVerbose($"Event corrections: sweep could not list dangling games: {ex.Message}");
            return new EventCorrectionSweepResult(stamped, 0, 0);
        }

        var touched = 0;
        var resolved = 0;
        foreach (var gameId in games)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var conn = _factory.CreateConnection();
                using var tx = conn.BeginTransaction();
                resolved += await EventCorrectionApplier.ResolveOrphansAsync(conn, tx, gameId);
                await tx.CommitAsync(ct);
                touched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad game never aborts the sweep; it is retried on the next launch.
                CoreDiagnostics.WriteVerbose($"Event corrections: sweep failed for game {gameId}: {ex.Message}");
            }
        }
        return new EventCorrectionSweepResult(stamped, touched, resolved);
    }

    private static async Task<long> ScalarAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }
}
