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
