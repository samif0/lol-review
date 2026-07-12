#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Data;

/// <summary>
/// Self-heal + forensics for "SQLite Error 14: 'unable to open database file'"
/// (SQLITE_CANTOPEN) and "attempt to write a readonly database" (SQLITE_READONLY).
///
/// <para>
/// Field context (P-041): a released user's "Scan for VODs" failed every time with
/// error 14 while filesystem-only features kept working. Error 14 carries no detail
/// about WHY the file couldn't be opened, so the app could neither fix it nor tell
/// the user what to do. This helper closes both gaps:
/// <list type="bullet">
///   <item><see cref="TryHeal"/> repairs the causes that are safe to repair
///   automatically: a missing data directory, and Windows read-only attributes on
///   the DB or its WAL sidecar files (<c>-wal</c>/<c>-shm</c>) — the classic
///   aftermath of restoring files from a backup drive or a cloud-sync tool.</item>
///   <item><see cref="Describe"/> inspects the filesystem and names the actual
///   problem in a sentence a user can act on.</item>
/// </list>
/// </para>
///
/// <para>
/// SAFETY: nothing here creates, deletes, moves, or writes INTO a database file.
/// The only mutations are <c>Directory.CreateDirectory</c> on the data folder and
/// clearing the <see cref="FileAttributes.ReadOnly"/> flag on Revu's own files.
/// </para>
/// </summary>
public static class SqliteOpenHealth
{
    /// <summary>Main DB file plus SQLite's WAL sidecar files.</summary>
    private static readonly string[] SidecarSuffixes = ["", "-wal", "-shm"];

    /// <summary>SQLITE_CANTOPEN — "unable to open database file".</summary>
    public const int SqliteCantOpen = 14;

    /// <summary>SQLITE_READONLY — "attempt to write a readonly database".</summary>
    public const int SqliteReadOnly = 8;

    /// <summary>
    /// True when the exception (or an inner one) is a SqliteException whose base
    /// error code says the file could not be opened or written: CANTOPEN (14) or
    /// READONLY (8). READONLY is included because SQLite silently downgrades a
    /// ReadWrite open of a write-protected file to read-only, deferring the
    /// failure to the first INSERT — same root causes, different code.
    /// </summary>
    public static bool IsOpenOrWriteAccessFailure(Exception ex) =>
        TryGetSqliteError(ex, out var code)
        && code is SqliteCantOpen or SqliteReadOnly;

    /// <summary>
    /// The single classification the sidecar's endpoints use to route a failure
    /// to the "database unavailable" handling (actionable message + pool clear):
    /// either the write factory's wrapped <see cref="DatabaseUnavailableException"/>
    /// or a raw SqliteException open/write-access failure that surfaced deeper
    /// (WAL sidecar problems appear at the first repository query, not at open).
    /// Endpoints and tests MUST share this predicate so they can't drift apart.
    /// </summary>
    public static bool IndicatesDatabaseUnavailable(Exception ex) =>
        ex is DatabaseUnavailableException || IsOpenOrWriteAccessFailure(ex);

    /// <summary>Unwraps to the first SqliteException and returns its base error code.</summary>
    public static bool TryGetSqliteError(Exception ex, out int errorCode)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqliteException sqlite)
            {
                errorCode = sqlite.SqliteErrorCode;
                return true;
            }
        }

        errorCode = 0;
        return false;
    }

    /// <summary>
    /// Repair the automatically-fixable open failures for the DB at
    /// <paramref name="dbPath"/>: recreate a missing parent directory and clear
    /// read-only attributes on <c>db</c>/<c>db-wal</c>/<c>db-shm</c>.
    /// Returns true iff something was actually changed (so callers know a retry
    /// is worthwhile). Never throws.
    /// </summary>
    public static bool TryHeal(string dbPath, ILogger logger)
    {
        var healed = false;
        try
        {
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                logger.LogWarning("SqliteOpenHealth: recreated missing data directory {Dir}", directory);
                healed = true;
            }

            foreach (var suffix in SidecarSuffixes)
            {
                var path = dbPath + suffix;
                if (!File.Exists(path))
                {
                    continue;
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) == 0)
                {
                    continue;
                }

                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                logger.LogWarning("SqliteOpenHealth: cleared read-only attribute on {Path}", path);
                healed = true;
            }
        }
        catch (Exception ex)
        {
            // Healing is best-effort by contract; the caller surfaces the original
            // open failure (now with Describe()'s diagnosis) if the retry fails too.
            logger.LogWarning(ex, "SqliteOpenHealth: self-heal attempt failed for {Path}", dbPath);
        }

        return healed;
    }

    /// <summary>
    /// Name the reason the DB at <paramref name="dbPath"/> can't be opened for
    /// writing, in one user-actionable sentence. Pure filesystem forensics —
    /// checks run in causality order and the first hit wins. Never throws.
    /// </summary>
    public static string Describe(string dbPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(dbPath) ?? "";

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                return File.Exists(directory)
                    ? $"a file is blocking Revu's data folder location ({directory}). Remove or rename that file."
                    : $"Revu's data folder is missing ({directory}).";
            }

            if (Directory.Exists(dbPath))
            {
                return $"a folder exists where Revu's database file should be ({dbPath}). Remove or rename that folder.";
            }

            if (!File.Exists(dbPath))
            {
                // Orphan journals mean a database DID exist here (its -wal holds
                // real frames) — creating a fresh file next to them would let
                // SQLite replay foreign WAL frames over it, so the app refuses.
                if (File.Exists(dbPath + "-wal") || File.Exists(dbPath + "-shm"))
                {
                    return $"Revu's database file is missing, but its journal files were left behind ({dbPath}). Restore revu.db from a backup, or delete the leftover revu.db-wal and revu.db-shm files to start fresh.";
                }

                return $"Revu's database file is missing (expected at {dbPath}).";
            }

            foreach (var suffix in SidecarSuffixes)
            {
                var path = dbPath + suffix;
                if (!File.Exists(path))
                {
                    continue;
                }

                // Attribute check per-file, tolerating deny-all ACLs (a sync or
                // backup tool can recreate -wal/-shm with ACLs that block even
                // reading attributes) — one broken file must not hide the diagnosis.
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    {
                        return $"the file {Path.GetFileName(path)} is marked read-only ({path}). Clear the read-only attribute in the file's Properties.";
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return $"Revu has no permission to access {Path.GetFileName(path)} ({path}). Fix the file's permissions (or delete it if it is a leftover from a backup tool).";
                }

                if (suffix.Length > 0 && !ProbeFileShareable(path))
                {
                    return $"Revu can't open its journal file {Path.GetFileName(path)} ({path}). Check the file's permissions, and whether an antivirus or sync tool is holding it.";
                }
            }

            if (!ProbeDirectoryWritable(directory))
            {
                return $"Revu's data folder isn't writable ({directory}). Check the folder's permissions, and whether an antivirus or sync tool is blocking it.";
            }

            if (!ProbeFileShareable(dbPath))
            {
                return $"another program has locked the database file ({dbPath}). Close other tools that might be scanning or syncing it, then try again.";
            }

            return $"the database file could not be opened ({dbPath}). A restart of Revu may fix this; if it persists, check antivirus or disk errors.";
        }
        catch (Exception ex)
        {
            return $"the database file could not be opened ({dbPath}): {ex.Message}";
        }
    }

    /// <summary>Can we create (and delete) a file in the data directory? WAL needs this.</summary>
    private static bool ProbeDirectoryWritable(string directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return true; // relative path — no directory to probe.
        }

        var probe = Path.Combine(directory, $".revu-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, bufferSize: 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Can the DB file be opened with the sharing SQLite itself uses? SQLite opens
    /// with read+write sharing, so a healthy file (even one held by live SQLite
    /// connections) passes; a file locked exclusively by another program fails.
    /// </summary>
    private static bool ProbeFileShareable(string dbPath)
    {
        try
        {
            using var stream = new FileStream(
                dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            return true; // unknown failure shape — don't claim a lock we can't prove.
        }
    }
}

/// <summary>
/// Thrown by the sidecar's write connection factory when the database cannot be
/// opened even after self-heal — carries the user-actionable diagnosis so
/// endpoints can surface it verbatim instead of "SQLite Error 14".
/// </summary>
public sealed class DatabaseUnavailableException : Exception
{
    public string DatabasePath { get; }

    /// <summary>The user-actionable reason from <see cref="SqliteOpenHealth.Describe"/>.</summary>
    public string Reason { get; }

    public DatabaseUnavailableException(string databasePath, string reason, Exception inner)
        : base($"Revu couldn't open its database: {reason}", inner)
    {
        DatabasePath = databasePath;
        Reason = reason;
    }
}
