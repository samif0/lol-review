using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Backups are consistent online copies that pass quick_check, keep tiered history,
/// give game deletes their own budget, and the user's automatic backup runs at most
/// daily. Restore accepts only a listed backup and validates it before the live
/// database is touched.
/// </summary>
public sealed class BackupServiceSafetyTests
{
    private static BackupService Service(TestDatabaseScope scope, AppConfig? config = null) =>
        new(new TestConfigService(config), scope.ConnectionFactory, NullLogger<BackupService>.Instance);

    private static string BackupDir(TestDatabaseScope scope) =>
        Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "backups");

    private static SqliteConnection Open(string path) =>
        Open(path, SqliteOpenMode.ReadOnly);

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static Task<long> SeedObjectiveAsync(TestDatabaseScope scope, string title) =>
        scope.Objectives.CreateWithPhasesAsync(title, "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);

    // Breaks one table's root page in place (an invalid b-tree page type): the file still
    // opens and copies, but PRAGMA quick_check fails.
    private static void DamageTablePage(TestDatabaseScope scope, string table)
    {
        SqliteConnection.ClearAllPools();
        long page, pageSize;
        using (var c = Open(scope.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            using (var checkpoint = c.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
            page = Scalar(c, $"SELECT rootpage FROM sqlite_schema WHERE type = 'table' AND name = '{table}'");
            pageSize = Scalar(c, "PRAGMA page_size;");
        }
        Assert.True(page > 1);
        using var file = new FileStream(scope.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        file.Position = (page - 1) * pageSize;
        file.WriteByte(0xFF);
    }

    // ── Backups ─────────────────────────────────────────────────────

    [Fact]
    public async Task SafetyBackup_IsAVerifiedSelfContainedCopyOfTheLatestWrites()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedObjectiveAsync(scope, "LATEST_WRITE");

        await Service(scope).CreateSafetyBackupAsync("test");

        var backup = Assert.Single(Directory.GetFiles(BackupDir(scope), "safety_backup_*.db"));
        Assert.False(File.Exists(backup + "-wal"));
        Assert.Empty(Directory.GetFiles(BackupDir(scope), "*.tmp"));
        using var c = Open(backup);
        Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM objectives WHERE title = 'LATEST_WRITE'"));
        using var journal = c.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("delete", Convert.ToString(journal.ExecuteScalar()));
    }

    [Fact]
    public async Task GameDeleteBackups_HaveTheirOwnBudget_AndNeverEvictSafetyBackups()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        Directory.CreateDirectory(BackupDir(scope));
        var today = DateTime.Now;
        var safety = Enumerable.Range(1, 3)
            .Select(i => Path.Combine(BackupDir(scope), $"safety_backup_{today.AddMinutes(-i):yyyyMMdd_HHmmss}.db"))
            .ToList();
        foreach (var path in safety) File.WriteAllText(path, "s");
        for (var i = 1; i <= 6; i++)
            File.WriteAllText(Path.Combine(BackupDir(scope), $"delete_backup_{today.AddMinutes(-10 - i):yyyyMMdd_HHmmss}.db"), "d");

        await Service(scope).CreateSafetyBackupAsync("delete-game-42");

        foreach (var path in safety) Assert.True(File.Exists(path));
        Assert.Equal(5, Directory.GetFiles(BackupDir(scope), "delete_backup_*.db").Length);
        var labels = (await Service(scope).ListBackupsAsync()).Select(b => b.Label).ToList();
        Assert.Contains(labels, l => l.StartsWith("Before a game was deleted", StringComparison.Ordinal));
        Assert.All(labels, l => Assert.DoesNotContain("—", l));
    }

    [Fact]
    public void TieredRetention_KeepsRecentDailyAndWeeklyHistory()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0); // a Wednesday
        var backups = new List<(string Path, DateTime Stamp)>();
        // Three launches today, then one backup a day for 40 days.
        backups.Add(("today-3", now.AddHours(-1)));
        backups.Add(("today-2", now.AddHours(-2)));
        backups.Add(("today-1", now.AddHours(-3)));
        for (var d = 1; d <= 40; d++) backups.Add(($"day-{d}", now.AddDays(-d)));

        var deleted = BackupService.SelectForDeletion(backups, new BackupRetention(3, 7, 4), now).ToHashSet();
        var kept = backups.Select(b => b.Path).Where(p => !deleted.Contains(p)).ToList();

        Assert.Contains("today-1", kept);       // the last 3
        Assert.Contains("day-6", kept);         // newest of each of the last 7 days
        Assert.DoesNotContain("day-7", kept);   // outside the daily window, not newest of its week
        Assert.Contains("day-3", kept);
        Assert.Contains("day-10", kept);        // Sunday Oct 4: newest of the week before last
        Assert.Contains("day-17", kept);        // Sunday Sep 27: oldest week in the 4-week window
        Assert.DoesNotContain("day-24", kept);  // Sunday Sep 20: a fifth week back
        Assert.DoesNotContain("day-40", kept);
        Assert.Equal(3 + 6 + 2, kept.Count);
    }

    [Fact]
    public async Task AutomaticUserBackup_RunsOncePerDay_IntoTheChosenFolder()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var folder = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "user-backups");
        Directory.CreateDirectory(folder);

        await Service(scope, new AppConfig { BackupEnabled = false, BackupFolder = folder }).RunBackupAsync();
        Assert.Empty(Directory.GetFiles(folder));

        var service = Service(scope, new AppConfig { BackupEnabled = true, BackupFolder = folder });
        await service.RunBackupAsync();
        var made = Assert.Single(Directory.GetFiles(folder, "lol_review_backup_*.db"));
        using (var c = Open(made)) Assert.True(Scalar(c, "SELECT COUNT(*) FROM sqlite_schema") > 0);

        await service.RunBackupAsync();
        Assert.Single(Directory.GetFiles(folder, "lol_review_backup_*.db"));
    }

    [Fact]
    public async Task AutomaticUserBackup_WithNoFolderChosen_GoesToTheDefaultLocation()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var service = Service(scope, new AppConfig { BackupEnabled = true, BackupFolder = "" });

        await service.RunBackupAsync();

        Assert.Single(Directory.GetFiles(BackupDir(scope), "lol_review_backup_*.db"));
        // The default location is Revu's backups folder, so it can be restored from Settings.
        Assert.Contains(await service.ListBackupsAsync(), b => b.Label.StartsWith("User backup", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task AutomaticUserBackup_IntoAFolderThatIsNotLocal_IsSkipped_WithoutTouchingIt()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var folder = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "user-backups");
        Directory.CreateDirectory(folder);

        // The device form names a real, reachable folder, so only the path policy stops it;
        // the share is a documentation address that nothing should ever try to reach.
        foreach (var notLocal in new[] { @"\\?\" + folder, @"\\192.0.2.1\backups" })
            await Service(scope, new AppConfig { BackupEnabled = true, BackupFolder = notLocal }).RunBackupAsync();

        Assert.Empty(Directory.GetFiles(folder));
        Assert.False(Directory.Exists(BackupDir(scope)) && Directory.GetFiles(BackupDir(scope), "lol_review_backup_*.db").Length > 0);
    }

    [Fact]
    public async Task GameDeleteBackup_OfADamagedDatabase_IsStillKept()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedObjectiveAsync(scope, "LIVE");
        DamageTablePage(scope, "objectives");
        var service = Service(scope);

        await service.CreateSafetyBackupAsync("delete-game-7");
        await service.CreateSafetyBackupAsync("test");

        // The delete goes ahead regardless, so its snapshot is kept; a routine safety
        // backup still refuses a copy that fails quick_check.
        Assert.Single(Directory.GetFiles(BackupDir(scope), "delete_backup_*.db"));
        Assert.Empty(Directory.GetFiles(BackupDir(scope), "safety_backup_*.db"));
        Assert.Empty(Directory.GetFiles(BackupDir(scope), "*.tmp*"));
    }

    // ── Restore ─────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_OfAPathOutsideTheBackupList_IsRefused()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedObjectiveAsync(scope, "LIVE");
        using var other = new TestDatabaseScope();
        await other.InitializeAsync();
        SqliteConnection.ClearAllPools();

        var result = await Service(scope).RestoreFromBackupAsync(other.DatabasePath);

        Assert.False(result.Success);
        Assert.Null(result.PreRestoreBackupFilePath);
        using var c = Open(scope.DatabasePath);
        Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM objectives WHERE title = 'LIVE'"));
    }

    [Theory]
    [InlineData("not a database at all")]
    [InlineData("SQLite format 3\0 but the rest is garbage, not pages")]
    public async Task Restore_OfADamagedBackup_IsRefused_BeforeTheLiveDatabaseIsTouched(string content)
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedObjectiveAsync(scope, "LIVE");
        Directory.CreateDirectory(BackupDir(scope));
        var bad = Path.Combine(BackupDir(scope), "safety_backup_20260101_000000.db");
        await File.WriteAllTextAsync(bad, content + new string('x', 4096));

        var result = await Service(scope).RestoreFromBackupAsync(bad);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.PreRestoreBackupFilePath);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(scope.DatabasePath)!, "*.tmp"));
        using var c = Open(scope.DatabasePath);
        Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM objectives WHERE title = 'LIVE'"));
    }

    [Fact]
    public async Task Restore_OfABackupFromANewerRevu_IsRefused()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var service = Service(scope);
        await service.CreateSafetyBackupAsync("test");
        var backup = (await service.ListBackupsAsync()).Single();
        using (var c = Open(backup.FilePath, SqliteOpenMode.ReadWrite))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE schema_metadata SET value = $v WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", Schema.AppSchemaVersionKey);
            cmd.Parameters.AddWithValue("$v", (Schema.CurrentAppSchemaVersion + 1).ToString());
            cmd.ExecuteNonQuery();
        }

        var result = await service.RestoreFromBackupAsync(backup.FilePath);

        Assert.False(result.Success);
        Assert.Contains("newer version of Revu", result.ErrorMessage);
        Assert.True(File.Exists(scope.DatabasePath));
    }

    [Fact]
    public async Task Restore_ByFileName_SwapsInTheBackup_AndLeavesNoStagingFiles()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedObjectiveAsync(scope, "IN_BACKUP");
        var service = Service(scope);
        await service.CreateSafetyBackupAsync("test");
        var backup = (await service.ListBackupsAsync()).Single();
        await SeedObjectiveAsync(scope, "AFTER_BACKUP");

        var result = await service.RestoreFromBackupAsync(backup.FileName);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(result.PreRestoreBackupFilePath!));
        var dataDir = Path.GetDirectoryName(scope.DatabasePath)!;
        Assert.Empty(Directory.GetFiles(dataDir, "*.tmp"));
        using (var c = Open(scope.DatabasePath))
        {
            Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM objectives WHERE title = 'IN_BACKUP'"));
            Assert.Equal(0, Scalar(c, "SELECT COUNT(*) FROM objectives WHERE title = 'AFTER_BACKUP'"));
        }
        // The pre-restore backup holds what was replaced, including the latest write.
        using (var c = Open(result.PreRestoreBackupFilePath!))
            Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM objectives WHERE title = 'AFTER_BACKUP'"));
    }

    [Fact]
    public async Task Restore_KeepsTheReplacedFiles_WhenThePreRestoreBackupIsAPlainCopy()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var service = Service(scope);
        await service.CreateSafetyBackupAsync("test");
        var backup = (await service.ListBackupsAsync()).Single();
        // A live file the online backup cannot read forces the plain-copy fallback.
        SqliteConnection.ClearAllPools();
        foreach (var sidecar in new[] { scope.DatabasePath + "-wal", scope.DatabasePath + "-shm" })
            if (File.Exists(sidecar)) File.Delete(sidecar);
        var unreadable = Enumerable.Repeat((byte)0x42, 8192).ToArray();
        await File.WriteAllBytesAsync(scope.DatabasePath, unreadable);

        var result = await service.RestoreFromBackupAsync(backup.FileName);

        Assert.True(result.Success, result.ErrorMessage);
        var dataDir = Path.GetDirectoryName(scope.DatabasePath)!;
        var kept = Assert.Single(Directory.GetFiles(dataDir, Path.GetFileName(scope.DatabasePath) + ".replaced-*.tmp"));
        Assert.Equal(unreadable, await File.ReadAllBytesAsync(kept));
    }

    [WindowsFact]
    public async Task Restore_WhileTheLiveDatabaseIsHeldOpen_ChangesNothing()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedObjectiveAsync(scope, "LIVE");
        var service = Service(scope);
        await service.CreateSafetyBackupAsync("test");
        var backup = (await service.ListBackupsAsync()).Single();
        await SeedObjectiveAsync(scope, "AFTER_BACKUP");

        RestoreResult result;
        using (var holder = Open(scope.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            Assert.Equal(1, Scalar(holder, "SELECT COUNT(*) FROM objectives WHERE title = 'AFTER_BACKUP'"));
            result = await service.RestoreFromBackupAsync(backup.FilePath);
        }

        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(scope.DatabasePath)!, "*.tmp"));
        using var c = Open(scope.DatabasePath);
        Assert.Equal(1, Scalar(c, "SELECT COUNT(*) FROM objectives WHERE title = 'AFTER_BACKUP'"));
    }
}
