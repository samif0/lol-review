#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Data;

/// <summary>
/// Safely migrates legacy portable databases into the AppData location used by
/// the installed app. Legacy candidates inside the Velopack install tree are
/// ignored so stale install artifacts cannot replace live user data.
/// </summary>
public sealed class LegacyDatabaseMigrationService
{
    private const int MaxAncestorSearchDepth = 4;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<LegacyDatabaseMigrationService> _logger;

    public LegacyDatabaseMigrationService(
        IDbConnectionFactory connectionFactory,
        ILogger<LegacyDatabaseMigrationService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Promote the best legacy database when it clearly contains more games than
    /// the current AppData database. Returns the migrated source path when a
    /// promotion happens.
    /// </summary>
    public string? TryMigrate()
    {
        var targetPath = Path.GetFullPath(_connectionFactory.DatabasePath);
        var targetCount = NormalizeGameCount(CountGamesInFile(targetPath));
        var candidate = FindBestLegacyDatabaseWithMoreGames(targetCount);

        if (candidate is null)
        {
            return null;
        }

        PromoteLegacyDatabase(candidate.Path, candidate.GameCount, targetPath, targetCount);
        return candidate.Path;
    }

    /// <summary>
    /// Finds the best legacy database that contains more games than the current
    /// AppData database.
    /// </summary>
    public string? FindLegacyDatabaseWithMoreGames(long minimumGameCountExclusive)
    {
        return FindBestLegacyDatabaseWithMoreGames(minimumGameCountExclusive)?.Path;
    }

    private LegacyDatabaseCandidate? FindBestLegacyDatabaseWithMoreGames(long minimumGameCountExclusive)
    {
        LegacyDatabaseCandidate? best = null;

        foreach (var candidatePath in EnumerateCandidatePaths())
        {
            var gameCount = NormalizeGameCount(CountGamesInFile(candidatePath));
            if (gameCount <= minimumGameCountExclusive)
            {
                continue;
            }

            var candidate = new LegacyDatabaseCandidate(
                candidatePath,
                gameCount,
                File.GetLastWriteTimeUtc(candidatePath));

            if (best is null ||
                candidate.GameCount > best.GameCount ||
                (candidate.GameCount == best.GameCount &&
                 candidate.LastWriteTimeUtc > best.LastWriteTimeUtc))
            {
                best = candidate;
            }
        }

        return best;
    }

    private IEnumerable<string> EnumerateCandidatePaths()
    {
        var targetPath = Path.GetFullPath(_connectionFactory.DatabasePath);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "Local");
        }

        var appRoot = Path.GetFullPath(Path.Combine(localAppData, "LoLReview"));
        var installCurrentRoot = Path.Combine(appRoot, "current");
        var installPackagesRoot = Path.Combine(appRoot, "packages");
        var targetDataRoot = Path.Combine(appRoot, "data");
        var oldAppDataPath = Path.Combine(appRoot, "lol_review.db");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string path)
        {
            var resolved = Path.GetFullPath(path);
            if (!File.Exists(resolved))
            {
                return;
            }

            if (string.Equals(resolved, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.Equals(resolved, oldAppDataPath, StringComparison.OrdinalIgnoreCase))
            {
                if (IsUnderRoot(resolved, installCurrentRoot) ||
                    IsUnderRoot(resolved, installPackagesRoot) ||
                    IsUnderRoot(resolved, targetDataRoot))
                {
                    return;
                }

                if (IsUnderRoot(resolved, appRoot))
                {
                    return;
                }
            }

            seen.Add(resolved);
        }

        AddCandidate(oldAppDataPath);

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Directory.GetCurrentDirectory())
        };

        var processPath = Environment.ProcessPath ?? typeof(LegacyDatabaseMigrationService).Assembly.Location;
        var processDir = Path.GetDirectoryName(processPath);
        if (!string.IsNullOrEmpty(processDir))
        {
            roots.Add(Path.GetFullPath(processDir));
        }

        foreach (var root in roots)
        {
            var current = new DirectoryInfo(root);
            for (var depth = 0; depth <= MaxAncestorSearchDepth && current is not null; depth++)
            {
                AddCandidate(Path.Combine(current.FullName, "data", "lol_review.db"));
                current = current.Parent;
            }
        }

        foreach (var candidate in seen)
        {
            yield return candidate;
        }
    }

    private void PromoteLegacyDatabase(
        string sourcePath,
        long sourceCount,
        string targetPath,
        long targetCount)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        var tempPath = targetPath + $".legacy-import-{Guid.NewGuid():N}.tmp";

        try
        {
            SnapshotDatabase(sourcePath, tempPath);

            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                _logger.LogInformation(
                    "Migrated legacy database from {Source} to {Target} ({SourceCount} games)",
                    sourcePath,
                    targetPath,
                    sourceCount);
                return;
            }

            var backupPath = targetPath + $".pre-legacy-migration-{DateTime.Now:yyyyMMdd_HHmmssfff}.bak";
            File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);

            _logger.LogInformation(
                "Promoted legacy database from {Source} to {Target} ({SourceCount} vs {TargetCount} games). Backup: {Backup}",
                sourcePath,
                targetPath,
                sourceCount,
                targetCount,
                backupPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not remove temporary legacy migration file {Path}", tempPath);
                }
            }
        }
    }

    private static void SnapshotDatabase(string sourcePath, string targetPath)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        var targetConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = targetPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var source = new SqliteConnection(sourceConnectionString);
        using var target = new SqliteConnection(targetConnectionString);

        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }

    private long CountGamesInFile(string dbFilePath)
    {
        if (!File.Exists(dbFilePath))
        {
            return -1;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbFilePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var tableCheck = connection.CreateCommand();
            tableCheck.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='games'";
            if (tableCheck.ExecuteScalar() is null)
            {
                return -1;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM games";
            var result = cmd.ExecuteScalar();
            return result is long count ? count : 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not count games in candidate legacy database {Path}", dbFilePath);
            return -1;
        }
    }

    private static long NormalizeGameCount(long rawCount)
    {
        return rawCount < 0 ? 0 : rawCount;
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var normalizedPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }

    private sealed record LegacyDatabaseCandidate(string Path, long GameCount, DateTime LastWriteTimeUtc);
}
