using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// v2.15.4: full coverage of reset + restore mechanics. These tests exist
/// because v2.15.2 had a bug where File.Delete failed silently when SQLite
/// still held the connection pool — config was wiped but DB survived, and
/// the VM reported success anyway. Every assertion here maps to a real
/// production failure mode.
/// </summary>
public sealed class BackupServiceResetTests
{
    // ─── ResetAllDataAsync ────────────────────────────────────────────

    [Fact]
    public async Task ResetAllDataAsync_CreatesPreResetBackupBeforeWipe()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedSomeDataAsync(scope);
        var svc = CreateService(scope);

        var result = await svc.ResetAllDataAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.BackupFilePath);
        Assert.True(File.Exists(result.BackupFilePath),
            $"Pre-reset backup should exist on disk at {result.BackupFilePath}");
    }

    [Fact]
    public async Task ResetAllDataAsync_ActuallyDeletesLiveDatabase()
    {
        // This is the key test — the v2.15.2 bug was that the DB survived
        // the reset because the connection pool blocked File.Delete.
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedSomeDataAsync(scope);

        // Simulate production: repositories have opened + returned connections
        // that the pool has cached. Force that state explicitly by opening
        // and immediately "disposing" a connection (which returns it to pool).
        using (var conn = scope.ConnectionFactory.CreateConnection())
        {
            // Do some work so the pool definitely has a warm connection.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM objectives";
            cmd.ExecuteScalar();
        }

        var svc = CreateService(scope);
        var result = await svc.ResetAllDataAsync();

        Assert.True(result.Success, $"Reset should succeed even with pooled connections. Error: {result.ErrorMessage}");
        Assert.False(File.Exists(scope.DatabasePath),
            "revu.db must not exist after reset — that was the v2.15.2 bug");
        Assert.False(File.Exists(scope.DatabasePath + "-shm"),
            "SHM sidecar must also be deleted");
        Assert.False(File.Exists(scope.DatabasePath + "-wal"),
            "WAL sidecar must also be deleted");
    }

    [Fact]
    public async Task ResetAllDataAsync_PreservesOtherBackupsInFolder()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = CreateService(scope);

        // Drop a sibling file into the backups dir to simulate an earlier
        // safety backup. Reset must not touch it.
        var backupDir = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "backups");
        Directory.CreateDirectory(backupDir);
        var siblingBackup = Path.Combine(backupDir, "safety_backup_20260101_120000.db");
        File.WriteAllText(siblingBackup, "sentinel");

        var result = await svc.ResetAllDataAsync();

        Assert.True(result.Success);
        Assert.True(File.Exists(siblingBackup), "Existing safety backups must survive reset");
        Assert.Equal("sentinel", File.ReadAllText(siblingBackup));
    }

    [Fact]
    public async Task ResetAllDataAsync_LeavesBackupsFolderIntact()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = CreateService(scope);

        var result = await svc.ResetAllDataAsync();

        var backupDir = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "backups");
        Assert.True(result.Success);
        Assert.True(Directory.Exists(backupDir),
            "Reset must leave the backups folder in place so the user can restore");
        Assert.True(File.Exists(result.BackupFilePath),
            "The pre-reset backup itself must be in that folder");
    }

    [Fact]
    public async Task ResetAllDataAsync_NoDbFile_ReturnsSuccessWithEmptyBackupPath()
    {
        using var scope = new TestDatabaseScope();
        // Intentionally skip InitializeAsync — no DB file on disk.
        var svc = CreateService(scope);

        var result = await svc.ResetAllDataAsync();

        Assert.True(result.Success);
        Assert.Equal("", result.BackupFilePath);
    }

    // ─── Round-trip: reset + restore ─────────────────────────────────

    [Fact]
    public async Task RestoreFromBackupAsync_AfterReset_RecoversPreResetData()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Seed a signature row, then reset.
        var seededId = await scope.Objectives.CreateWithPhasesAsync(
            "SIGNATURE_TITLE", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);

        var svc = CreateService(scope);
        var resetResult = await svc.ResetAllDataAsync();
        Assert.True(resetResult.Success);
        Assert.False(File.Exists(scope.DatabasePath), "DB should be gone after reset");

        // Restore from the pre-reset backup.
        var restoreResult = await svc.RestoreFromBackupAsync(resetResult.BackupFilePath);
        Assert.True(restoreResult.Success, restoreResult.ErrorMessage);
        Assert.True(File.Exists(scope.DatabasePath), "DB should be back after restore");

        // Sanity check via raw SQL first — the backup file on disk should
        // contain the signature row. This isolates "did restore copy the
        // right bytes" from "does the connection factory see them".
        using (var raw = scope.ConnectionFactory.CreateConnection())
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM objectives WHERE title = 'SIGNATURE_TITLE'";
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(1, count);
        }

        // And via the repository API.
        var restored = await scope.Objectives.GetAsync(seededId);
        Assert.NotNull(restored);
        Assert.Equal("SIGNATURE_TITLE", restored!.Title);
    }

    [Fact]
    public async Task RestoreFromBackupAsync_CreatesPreRestoreBackupOfCurrentDb()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedSomeDataAsync(scope);

        var svc = CreateService(scope);
        // Make a backup to restore from.
        await svc.CreateSafetyBackupAsync("test-fixture");
        var backups = await svc.ListBackupsAsync();
        Assert.NotEmpty(backups);
        var sourceBackup = backups[0];

        var result = await svc.RestoreFromBackupAsync(sourceBackup.FilePath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.PreRestoreBackupFilePath);
        Assert.True(File.Exists(result.PreRestoreBackupFilePath!),
            "Pre-restore backup must exist so the restore is reversible");
    }

    [Fact]
    public async Task RestoreFromBackupAsync_NonexistentFile_ReportsFailure()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = CreateService(scope);

        var result = await svc.RestoreFromBackupAsync("C:/definitely/not/a/real/path.db");

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        // Live DB should be untouched.
        Assert.True(File.Exists(scope.DatabasePath), "Failed restore must not wipe the live DB");
    }

    // ─── ListBackupsAsync ────────────────────────────────────────────

    [Fact]
    public async Task ListBackupsAsync_ReturnsBackupsNewestFirst()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var backupDir = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "backups");
        Directory.CreateDirectory(backupDir);

        // Three backups with known timestamps embedded in filename.
        var older = Path.Combine(backupDir, "safety_backup_20260101_000000.db");
        var middle = Path.Combine(backupDir, "safety_backup_20260301_000000.db");
        var newer = Path.Combine(backupDir, "safety_backup_20260501_000000.db");
        File.WriteAllText(older, "a");
        File.WriteAllText(middle, "b");
        File.WriteAllText(newer, "c");

        var svc = CreateService(scope);
        var backups = await svc.ListBackupsAsync();

        Assert.Equal(3, backups.Count);
        // Newest first.
        Assert.Equal(Path.GetFileName(newer), backups[0].FileName);
        Assert.Equal(Path.GetFileName(middle), backups[1].FileName);
        Assert.Equal(Path.GetFileName(older), backups[2].FileName);
    }

    [Fact]
    public async Task ListBackupsAsync_LabelsIncludeKindTimestampAndSize()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var backupDir = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "backups");
        Directory.CreateDirectory(backupDir);
        var file = Path.Combine(backupDir, "pre_reset_20260401_140000.db");
        File.WriteAllText(file, new string('x', 2048)); // ~2 KB so MB shows 0.0

        var svc = CreateService(scope);
        var backups = await svc.ListBackupsAsync();

        var entry = Assert.Single(backups);
        // Label format: "<Kind> — <date> — <size> MB"
        Assert.Contains("Pre-reset", entry.Label);
        Assert.Contains("2026", entry.Label); // timestamp rendered
        Assert.Contains("MB", entry.Label);
    }

    [Fact]
    public async Task ListBackupsAsync_NoBackupsDir_ReturnsEmpty()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = CreateService(scope);

        var backups = await svc.ListBackupsAsync();
        Assert.Empty(backups);
    }

    [Fact]
    public async Task ListBackupsAsync_IncludesAllBackupKinds()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var backupDir = Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "backups");
        Directory.CreateDirectory(backupDir);

        foreach (var name in new[] {
            "safety_backup_20260101_000000.db",
            "lol_review_backup_20260101_000000.db",
            "pre_reset_20260101_000000.db",
            "pre_restore_20260101_000000.db",
            "coach-pre-migration-20260101_000000.db",
        })
        {
            File.WriteAllText(Path.Combine(backupDir, name), "x");
        }

        var svc = CreateService(scope);
        var backups = await svc.ListBackupsAsync();

        Assert.Equal(5, backups.Count);
    }

    // ─── Post-reset subsequent writes behave normally ───────────────

    [Fact]
    public async Task AfterReset_NextInitializeCreatesFreshEmptyDatabase()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedSomeDataAsync(scope);

        var before = await scope.Objectives.GetAllAsync();
        Assert.NotEmpty(before);

        var svc = CreateService(scope);
        await svc.ResetAllDataAsync();

        // Re-initialize — should produce a fresh, empty DB.
        await scope.InitializeAsync();
        var after = await scope.Objectives.GetAllAsync();
        Assert.Empty(after);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static BackupService CreateService(TestDatabaseScope scope)
    {
        var config = new TestConfigService();
        return new BackupService(
            config,
            scope.ConnectionFactory,
            NullLogger<BackupService>.Instance);
    }

    private static async Task SeedSomeDataAsync(TestDatabaseScope scope)
    {
        await scope.Objectives.CreateWithPhasesAsync(
            "seed objective", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
    }
}
