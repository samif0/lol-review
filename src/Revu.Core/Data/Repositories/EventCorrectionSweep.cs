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
    /// per-game try/catch. Never inserts game_events rows. <paramref name="beforeFirstWrite"/> (the
    /// session backup) runs once, right before the first change this sweep actually makes: a correction
    /// that still has no twin writes nothing, so it no longer costs a backup on every launch. It never
    /// runs while this sweep holds a write transaction.</summary>
    public async Task<EventCorrectionSweepResult> RunAsync(int maxRows = 20_000, CancellationToken ct = default,
        Func<Task>? beforeFirstWrite = null)
    {
        if (beforeFirstWrite is not null && await CountUnkeyedAsync() > 0)
        {
            await beforeFirstWrite();
            beforeFirstWrite = null;
        }
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
                using (var conn = _factory.CreateConnection())
                using (var tx = conn.BeginTransaction())
                {
                    var changesBefore = beforeFirstWrite is null ? 0 : await TotalChangesAsync(conn, tx);
                    var count = await EventCorrectionApplier.ResolveOrphansAsync(conn, tx, gameId);
                    if (beforeFirstWrite is null || await TotalChangesAsync(conn, tx) == changesBefore)
                    {
                        await tx.CommitAsync(ct);
                        resolved += count;
                        touched++;
                        continue;
                    }
                    // This game is the sweep's first real change. Roll it back before the
                    // backup, so the backup never keeps every other writer waiting on this
                    // transaction's write lock, then redo the game below.
                    await tx.RollbackAsync(ct);
                }
                await beforeFirstWrite();
                beforeFirstWrite = null;
                using (var conn = _factory.CreateConnection())
                using (var tx = conn.BeginTransaction())
                {
                    resolved += await EventCorrectionApplier.ResolveOrphansAsync(conn, tx, gameId);
                    await tx.CommitAsync(ct);
                    touched++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad game never aborts the sweep; it is retried on the next launch.
                CoreDiagnostics.WriteVerbose($"Event corrections: sweep failed for game {gameId}: {ex.Message}");
            }
        }
        return new EventCorrectionSweepResult(stamped, touched, resolved);
    }

    private async Task<long> CountUnkeyedAsync()
    {
        try
        {
            using var conn = _factory.CreateConnection();
            return await ScalarAsync(conn, "SELECT COUNT(*) FROM game_events WHERE event_key IS NULL");
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static async Task<long> TotalChangesAsync(SqliteConnection conn, SqliteTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT total_changes()";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<long> ScalarAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }
}
