#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data;

/// <summary>
/// One write connection holding one <c>BEGIN IMMEDIATE</c> transaction, shared by
/// several repository calls so a multi-table save commits all-or-nothing. Repository
/// write methods take an optional session: with one they join it, without one they
/// open their own connection exactly as before. Nothing is written until
/// <see cref="Commit"/>; disposing an uncommitted session rolls every write back.
///
/// <para>While a session is open, reads of the tables it wrote must go through the
/// session too (or happen before <see cref="Begin"/>): another connection either sees
/// the pre-session snapshot (WAL) or waits on the session's locks (shared cache).</para>
/// </summary>
public sealed class DbWriteSession : IDisposable
{
    private int _savepoints;

    private DbWriteSession(SqliteConnection connection, SqliteTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    /// <summary>Open a connection and take the write lock up front (<c>BEGIN IMMEDIATE</c>),
    /// so the unit waits out another writer at the start instead of failing midway.</summary>
    public static DbWriteSession Begin(IDbConnectionFactory factory)
    {
        var connection = factory.CreateConnection();
        try
        {
            return new DbWriteSession(connection, connection.BeginTransaction(deferred: false));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Commit() => Transaction.Commit();

    /// <summary>
    /// Run an optional step inside a savepoint. A failure rolls back only that step's
    /// writes and is returned (null on success), so the rest of the unit still commits.
    /// For best-effort side writes that must never fail the whole save.
    /// </summary>
    public async Task<Exception?> TryStepAsync(Func<Task> step)
    {
        var name = $"revu_step_{++_savepoints}";
        Transaction.Save(name);
        try
        {
            await step().ConfigureAwait(false);
            Transaction.Release(name);
            return null;
        }
        catch (Exception ex)
        {
            Transaction.Rollback(name);
            Transaction.Release(name);
            return ex;
        }
    }

    public void Dispose()
    {
        Transaction.Dispose();
        Connection.Dispose();
    }

    /// <summary>
    /// The connection one repository call uses: the session's when it is part of a unit,
    /// else a fresh one the lease owns. With <paramref name="transactional"/> a standalone
    /// call also gets its own transaction (committed by <see cref="DbConnectionLease.Complete"/>);
    /// inside a session it joins the session's transaction and leaves the commit to the unit.
    /// Commands created from <see cref="DbConnectionLease.Connection"/> enlist automatically.
    /// </summary>
    public static DbConnectionLease Lease(IDbConnectionFactory factory, DbWriteSession? session, bool transactional = false)
    {
        if (session is not null)
        {
            return new DbConnectionLease(session.Connection, session.Transaction, ownsConnection: false, ownsTransaction: false);
        }

        var connection = factory.CreateConnection();
        try
        {
            var transaction = transactional ? connection.BeginTransaction(deferred: false) : null;
            return new DbConnectionLease(connection, transaction, ownsConnection: true, ownsTransaction: transactional);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

/// <summary>See <see cref="DbWriteSession.Lease"/>.</summary>
public sealed class DbConnectionLease : IDisposable
{
    private readonly bool _ownsConnection;
    private readonly bool _ownsTransaction;

    internal DbConnectionLease(SqliteConnection connection, SqliteTransaction? transaction, bool ownsConnection, bool ownsTransaction)
    {
        Connection = connection;
        Transaction = transaction;
        _ownsConnection = ownsConnection;
        _ownsTransaction = ownsTransaction;
    }

    public SqliteConnection Connection { get; }

    /// <summary>The session's transaction, the lease's own, or null for a plain call.</summary>
    public SqliteTransaction? Transaction { get; }

    /// <summary>Commit the lease's own transaction; a no-op inside a session.</summary>
    public void Complete()
    {
        if (_ownsTransaction) Transaction?.Commit();
    }

    public void Dispose()
    {
        if (_ownsTransaction) Transaction?.Dispose();
        if (_ownsConnection) Connection.Dispose();
    }
}
