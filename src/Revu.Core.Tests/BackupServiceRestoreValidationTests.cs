using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// RestoreFromBackupAsync only ever receives a path chosen from the restore
/// picker, but the sidecar route accepts any string. These tests pin down the
/// contract that a restore candidate must be one of Revu's own backup files
/// (inside the app backups directory or the user-configured backup folder)
/// AND a healthy SQLite database before the live DB is touched. A rejected
/// candidate must leave the live DB byte-for-byte unchanged and must not drop
/// a pre-restore snapshot into the backups folder.
/// </summary>
public sealed class BackupServiceRestoreValidationTests
{
    // ─── RestoreFromBackupAsync end-to-end ──────────────────────────

    [Fact]
    public async Task RestoreFromBackupAsync_DbOutsideBackupsDir_IsRejectedAndLiveDbUntouched()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedSomeDataAsync(scope);
        var svc = CreateService(scope);

        // A perfectly valid Revu database — but living in an unrelated folder.
        var backupDir = BackupDirOf(scope);
        await svc.CreateSafetyBackupAsync("fixture");
        var genuine = Assert.Single(Directory.EnumerateFiles(backupDir, "*.db"));

        var outsideDir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsideDb = Path.Combine(outsideDir, "revu_copy.db");
        File.Copy(genuine, outsideDb);

        var liveBefore = SnapshotLiveDb(scope);
        var backupsBefore = Directory.GetFiles(backupDir).OrderBy(f => f).ToArray();

        var result = await svc.RestoreFromBackupAsync(outsideDb);

        Assert.False(result.Success, "A .db outside the allowed backup locations must not be restorable");
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.PreRestoreBackupFilePath);
        Assert.Equal(liveBefore, SnapshotLiveDb(scope));
        Assert.Equal(backupsBefore, Directory.GetFiles(backupDir).OrderBy(f => f).ToArray());
    }

    [Fact]
    public async Task RestoreFromBackupAsync_RandomBytesInsideBackupsDir_IsRejectedAndLiveDbUntouched()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedSomeDataAsync(scope);
        var svc = CreateService(scope);

        var backupDir = BackupDirOf(scope);
        Directory.CreateDirectory(backupDir);
        var junk = Path.Combine(backupDir, "safety_backup_20260101_120000.db");
        var bytes = new byte[8192];
        new Random(12345).NextBytes(bytes);
        File.WriteAllBytes(junk, bytes);

        var liveBefore = SnapshotLiveDb(scope);

        var result = await svc.RestoreFromBackupAsync(junk);

        Assert.False(result.Success, "A file that is not a SQLite database must not be restorable");
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.PreRestoreBackupFilePath);
        Assert.Equal(liveBefore, SnapshotLiveDb(scope));
        Assert.Empty(Directory.EnumerateFiles(backupDir, "pre_restore_*.db"));
    }

    [Fact]
    public async Task RestoreFromBackupAsync_TraversalOutOfBackupsDir_IsRejectedAndLiveDbUntouched()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedSomeDataAsync(scope);
        var svc = CreateService(scope);

        var backupDir = BackupDirOf(scope);
        await svc.CreateSafetyBackupAsync("fixture");
        var genuine = Assert.Single(Directory.EnumerateFiles(backupDir, "*.db"));

        // Valid DB planted one level up, referenced through the backups dir.
        var dataDir = Path.GetDirectoryName(scope.DatabasePath)!;
        var planted = Path.Combine(dataDir, "evil.db");
        File.Copy(genuine, planted);
        var traversal = Path.Combine(backupDir, "..", "evil.db");
        Assert.True(File.Exists(traversal));

        var liveBefore = SnapshotLiveDb(scope);

        var result = await svc.RestoreFromBackupAsync(traversal);

        Assert.False(result.Success, "A traversal path that escapes the backups dir must not be restorable");
        Assert.NotNull(result.ErrorMessage);
        Assert.Null(result.PreRestoreBackupFilePath);
        Assert.Equal(liveBefore, SnapshotLiveDb(scope));
        Assert.Empty(Directory.EnumerateFiles(backupDir, "pre_restore_*.db"));
    }

    [Fact]
    public async Task RestoreFromBackupAsync_GenuineSafetyBackup_StillRestores()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var seededId = await scope.Objectives.CreateWithPhasesAsync(
            "SIGNATURE_TITLE", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);
        var svc = CreateService(scope);

        await svc.CreateSafetyBackupAsync("fixture");
        var backups = await svc.ListBackupsAsync();
        var source = Assert.Single(backups);

        // Mutate the live DB after the backup so a successful restore is observable.
        await scope.Objectives.CreateWithPhasesAsync(
            "AFTER_BACKUP", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);

        var result = await svc.RestoreFromBackupAsync(source.FilePath);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.PreRestoreBackupFilePath);
        var restored = await scope.Objectives.GetAsync(seededId);
        Assert.NotNull(restored);
        Assert.Equal("SIGNATURE_TITLE", restored!.Title);
        var all = await scope.Objectives.GetAllAsync();
        Assert.DoesNotContain(all, o => o.Title == "AFTER_BACKUP");
        // The read-only integrity probe must not litter the backups folder
        // with -wal/-shm sidecars next to the backup it inspected.
        Assert.Empty(Directory.EnumerateFiles(BackupDirOf(scope), "*.db-*"));
    }

    [Fact]
    public async Task RestoreFromBackupAsync_ValidDbInConfiguredBackupFolder_IsAccepted()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var seededId = await scope.Objectives.CreateWithPhasesAsync(
            "USER_FOLDER_TITLE", "", "primary", "", "",
            practicePre: false, practiceIn: true, practicePost: false);

        var userFolder = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userFolder);
        var config = new TestConfigService(new AppConfig { BackupEnabled = true, BackupFolder = userFolder });
        var svc = new BackupService(config, scope.ConnectionFactory, NullLogger<BackupService>.Instance);

        await svc.RunBackupAsync();
        var userBackup = Assert.Single(Directory.EnumerateFiles(userFolder, "lol_review_backup_*.db"));

        var result = await svc.RestoreFromBackupAsync(userBackup);

        Assert.True(result.Success, result.ErrorMessage);
        var restored = await scope.Objectives.GetAsync(seededId);
        Assert.NotNull(restored);
        Assert.Equal("USER_FOLDER_TITLE", restored!.Title);
        Assert.Empty(Directory.EnumerateFiles(userFolder, "*.db-*"));
    }

    // ─── ValidateRestoreCandidate unit-level ────────────────────────

    [Fact]
    public async Task ValidateRestoreCandidate_WrongExtensionInsideBackupsDir_IsRejected()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = CreateService(scope);
        var backupDir = BackupDirOf(scope);
        await svc.CreateSafetyBackupAsync("fixture");
        var genuine = Assert.Single(Directory.EnumerateFiles(backupDir, "*.db"));
        var renamed = Path.ChangeExtension(genuine, ".sqlite");
        File.Copy(genuine, renamed);

        var check = BackupService.ValidateRestoreCandidate(renamed, backupDir, null);

        Assert.False(check.Ok);
        Assert.Contains(".db", check.Error);
    }

    [Fact]
    public async Task ValidateRestoreCandidate_NestedSubfolderOfBackupsDir_IsRejected()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = CreateService(scope);
        var backupDir = BackupDirOf(scope);
        await svc.CreateSafetyBackupAsync("fixture");
        var genuine = Assert.Single(Directory.EnumerateFiles(backupDir, "*.db"));
        var nestedDir = Path.Combine(backupDir, "nested");
        Directory.CreateDirectory(nestedDir);
        var nested = Path.Combine(nestedDir, "safety_backup_20260101_120000.db");
        File.Copy(genuine, nested);

        var check = BackupService.ValidateRestoreCandidate(nested, backupDir, null);

        Assert.False(check.Ok);
        Assert.NotNull(check.Error);
    }

    [Fact]
    public void ValidateRestoreCandidate_EmptyFileInsideBackupsDir_IsRejected()
    {
        using var scope = new TestDatabaseScope();
        var backupDir = BackupDirOf(scope);
        Directory.CreateDirectory(backupDir);
        var empty = Path.Combine(backupDir, "safety_backup_20260101_120000.db");
        File.WriteAllBytes(empty, Array.Empty<byte>());

        var check = BackupService.ValidateRestoreCandidate(empty, backupDir, null);

        Assert.False(check.Ok);
        Assert.NotNull(check.Error);
    }

    [Fact]
    public void ValidateRestoreCandidate_SqliteHeaderButTruncatedBody_IsRejected()
    {
        using var scope = new TestDatabaseScope();
        var backupDir = BackupDirOf(scope);
        Directory.CreateDirectory(backupDir);
        var forged = Path.Combine(backupDir, "safety_backup_20260101_120000.db");
        var bytes = new byte[4096];
        "SQLite format 3\0"u8.CopyTo(bytes);
        new Random(777).NextBytes(bytes.AsSpan(16));
        File.WriteAllBytes(forged, bytes);

        var check = BackupService.ValidateRestoreCandidate(forged, backupDir, null);

        Assert.False(check.Ok, "A file with the SQLite magic but garbage pages must fail quick_check");
        Assert.NotNull(check.Error);
    }

    [Fact]
    public void ValidateRestoreCandidate_MissingFile_ReportsNotFound()
    {
        using var scope = new TestDatabaseScope();
        var backupDir = BackupDirOf(scope);

        var check = BackupService.ValidateRestoreCandidate(
            Path.Combine(backupDir, "safety_backup_20260101_120000.db"), backupDir, null);

        Assert.False(check.Ok);
        Assert.Equal("Backup file not found.", check.Error);
    }

    [Fact]
    public async Task ValidateRestoreCandidate_AcceptedCandidate_ReturnsCanonicalPath()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var svc = CreateService(scope);
        var backupDir = BackupDirOf(scope);
        await svc.CreateSafetyBackupAsync("fixture");
        var genuine = Assert.Single(Directory.EnumerateFiles(backupDir, "*.db"));

        // Reach the same file through a redundant "./" segment.
        var roundabout = Path.Combine(backupDir, ".", Path.GetFileName(genuine));

        var check = BackupService.ValidateRestoreCandidate(roundabout, backupDir, null);

        Assert.True(check.Ok, check.Error);
        Assert.Equal(Path.GetFullPath(genuine), check.FullPath);
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static string BackupDirOf(TestDatabaseScope scope) =>
        Path.Combine(Path.GetDirectoryName(scope.DatabasePath)!, "backups");

    // The scope's pooled SQLite connection keeps revu.db open for writing; on
    // Windows a plain File.ReadAllBytes (FileShare.Read) is a sharing violation
    // against that handle, so read with the same share mode SQLite itself uses.
    private static byte[] SnapshotLiveDb(TestDatabaseScope scope)
    {
        using var stream = new FileStream(
            scope.DatabasePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static BackupService CreateService(TestDatabaseScope scope)
    {
        return new BackupService(
            new TestConfigService(),
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
