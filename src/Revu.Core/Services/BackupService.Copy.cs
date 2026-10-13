#nullable enable

using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>How many backups of one kind survive pruning.</summary>
/// <param name="KeepRecent">The newest N, whatever their age.</param>
/// <param name="KeepDaily">The newest per calendar day, for this many days (today included).</param>
/// <param name="KeepWeekly">The newest per calendar week (Monday start), for this many weeks.</param>
internal sealed record BackupRetention(int KeepRecent, int KeepDaily, int KeepWeekly);

/// <summary>Consistent, verified backup copies, tiered pruning and the leftover-temp sweep.</summary>
public sealed partial class BackupService
{
    /// <summary>
    /// Copy <paramref name="sourceDb"/> to <paramref name="destination"/> with SQLite's
    /// online backup API: a transactionally consistent snapshot that includes WAL
    /// content and stays consistent while other connections write (a File.Copy of the
    /// live file could miss WAL frames or tear mid-checkpoint). The copy is made
    /// self-contained (rollback journal), checked with PRAGMA quick_check, and only then
    /// moved to its final name, so a backup on disk is always complete.
    /// When <paramref name="requireIntact"/> is false (a snapshot of a live database
    /// the user may be replacing BECAUSE it is damaged), a failed check is logged and the
    /// copy is kept anyway.
    /// </summary>
    private void CreateVerifiedCopy(string sourceDb, string destination, bool requireIntact)
    {
        var temp = $"{destination}.partial-{Guid.NewGuid():N}.tmp";
        try
        {
            using (var source = OpenForBackup(sourceDb))
            using (var target = OpenFile(temp, SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(target);
                UseRollbackJournal(target);
                if (QuickCheck(target) is { } problem)
                {
                    if (requireIntact)
                        throw new InvalidDataException($"Backup copy failed PRAGMA quick_check: {problem}");
                    _logger.LogWarning("Backup of a damaged database kept as-is ({Problem}): {Dest}", problem, destination);
                }
            }
            File.Move(temp, destination, overwrite: false);
        }
        finally
        {
            DeleteDatabaseFiles(temp);
        }
    }

    // A private (non-shared-cache, unpooled) read connection: it reads a committed
    // snapshot without blocking the app's writers and releases the file on dispose.
    private static SqliteConnection OpenForBackup(string path)
    {
        try { return OpenFile(path, SqliteOpenMode.ReadOnly); }
        catch (SqliteException)
        {
            // A read-only open can fail on a WAL database whose -shm cannot be created
            // read-only; a read-write open (never create) reads the same snapshot.
            return OpenFile(path, SqliteOpenMode.ReadWrite);
        }
    }

    private static SqliteConnection OpenFile(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 10,
        }.ToString());
        try
        {
            connection.Open();
            using var busy = connection.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout=5000;";
            busy.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // The online backup copies the source header, WAL flag included; a backup must not
    // need a -wal beside it to be complete.
    private static void UseRollbackJournal(SqliteConnection connection)
    {
        using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=DELETE;";
        var mode = Convert.ToString(journal.ExecuteScalar());
        if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Could not make the backup self-contained.");
    }

    /// <summary>Null when PRAGMA quick_check reports "ok", else its first finding.</summary>
    private static string? QuickCheck(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        using var reader = check.ExecuteReader();
        if (!reader.Read()) return "no result";
        var first = reader.IsDBNull(0) ? "" : reader.GetString(0);
        if (first == "ok" && !reader.Read()) return null;
        return first.Length == 0 ? "unknown" : first;
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort: a stray temp file is harmless */ }
    }

    // A temp database and whatever SQLite made beside it. The copied header says WAL
    // until journal_mode=DELETE runs, so a failure before then can leave -wal and -shm.
    private static void DeleteDatabaseFiles(string path)
    {
        DeleteQuietly(path);
        DeleteQuietly(path + "-journal");
        DeleteQuietly(path + "-wal");
        DeleteQuietly(path + "-shm");
    }

    // ── Leftover temp files ─────────────────────────────────────────

    /// <summary>
    /// A copy or restore temp file older than this belongs to a process that is gone: the
    /// sweep runs once per launch, before this sidecar can have a copy of its own that old.
    /// </summary>
    private static readonly TimeSpan LeftoverTempAge = TimeSpan.FromHours(1);

    // CreateVerifiedCopy's "<backup>.db.partial-<guid>.tmp" for Revu's own backup names,
    // plus whatever SQLite made beside it.
    private static readonly Regex CopyTempName = new(
        "^(?:" + string.Join("|", new[]
            {
                SafetyPrefix, DeletePrefix, ObjectiveDeletePrefix, UserPrefix, PreResetPrefix, PreRestorePrefix,
            }.Select(Regex.Escape))
        + @")[^\\/]*\.db\.partial-[0-9a-f]{32}\.tmp(?:-journal|-wal|-shm)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // RestoreFromBackupAsync's staged "<db>.restoring-<guid>.tmp", plus its SQLite files.
    private static bool IsRestoreStagingName(string fileName, string dbFileName) =>
        Regex.IsMatch(fileName,
            "^" + Regex.Escape(dbFileName) + @"\.restoring-[0-9a-f]{32}\.tmp(?:-journal|-wal|-shm)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Removes the database-sized temp files a backup copy or a restore leaves behind when
    /// the process is killed part way (nothing else ever deletes them): a restore's staged
    /// copy beside the database and copy temps in the backups folder. Only Revu's own temp
    /// names match: never the live database or its -wal/-shm, and never the
    /// <c>.replaced-</c> files a restore keeps on purpose.
    /// </summary>
    private void SweepLeftoverTempFiles(string dbPath)
    {
        var dataDir = Path.GetDirectoryName(dbPath);
        if (string.IsNullOrEmpty(dataDir)) return;
        var dbFileName = Path.GetFileName(dbPath);
        SweepLeftoverTempFiles(dataDir, name => IsRestoreStagingName(name, dbFileName));
        SweepLeftoverTempFiles(Path.Combine(dataDir, "backups"), CopyTempName.IsMatch);
    }

    private void SweepLeftoverTempFiles(string folder, Func<string, bool> isLeftover)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            var cutoff = DateTime.UtcNow - LeftoverTempAge;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.tmp*"))
            {
                if (!isLeftover(file.Name) || file.LastWriteTimeUtc > cutoff) continue;
                try
                {
                    file.Delete();
                    _logger.LogInformation("Removed {Name}, left by a backup or restore that did not finish", file.Name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not remove leftover temp file {Name}", file.Name);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Leftover temp file sweep skipped in {Folder}", folder);
        }
    }

    // ── Pruning ─────────────────────────────────────────────────────

    private Task PruneNewestAsync(string folder, string prefix, int maxKeep) =>
        PruneAsync(folder, prefix, backups => backups
            .OrderByDescending(b => b.Stamp)
            .Skip(maxKeep)
            .Select(b => b.Path)
            .ToList());

    private Task PruneTieredAsync(string folder, string prefix, BackupRetention policy) =>
        PruneAsync(folder, prefix, backups => SelectForDeletion(backups, policy, DateTime.Now));

    private Task PruneAsync(string folder, string prefix,
        Func<IReadOnlyList<(string Path, DateTime Stamp)>, IReadOnlyList<string>> select)
    {
        return Task.Run(() =>
        {
            try
            {
                var backups = new DirectoryInfo(folder)
                    .EnumerateFiles($"{prefix}*.db")
                    .Select(f => (Path: f.FullName, Stamp: TryParseBackupTimestamp(f.Name) ?? f.LastWriteTime))
                    .ToList();

                foreach (var path in select(backups))
                {
                    try
                    {
                        File.Delete(path);
                        _logger.LogInformation("Pruned old backup: {Name}", System.IO.Path.GetFileName(path));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete old backup {Name}", System.IO.Path.GetFileName(path));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backup pruning failed");
            }
        });
    }

    /// <summary>
    /// The backups a tiered policy lets go: everything that is not among the newest
    /// <see cref="BackupRetention.KeepRecent"/>, not the newest of its day within the
    /// daily window, and not the newest of its week within the weekly window.
    /// </summary>
    internal static IReadOnlyList<string> SelectForDeletion(
        IReadOnlyList<(string Path, DateTime Stamp)> backups, BackupRetention policy, DateTime now)
    {
        var ordered = backups.OrderByDescending(b => b.Stamp).ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in ordered.Take(policy.KeepRecent)) keep.Add(b.Path);

        var firstDay = now.Date.AddDays(1 - policy.KeepDaily);
        foreach (var day in ordered.Where(b => b.Stamp.Date >= firstDay).GroupBy(b => b.Stamp.Date))
            keep.Add(day.First().Path);

        var firstWeek = WeekStart(now).AddDays(-7 * (policy.KeepWeekly - 1));
        foreach (var week in ordered.Where(b => WeekStart(b.Stamp) >= firstWeek).GroupBy(b => WeekStart(b.Stamp)))
            keep.Add(week.First().Path);

        return ordered.Where(b => !keep.Contains(b.Path)).Select(b => b.Path).ToList();
    }

    private static DateTime WeekStart(DateTime stamp) =>
        stamp.Date.AddDays(-(((int)stamp.DayOfWeek + 6) % 7));
}
