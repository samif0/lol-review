using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Regression coverage for P-023 / P-020: the config-save folder-write guard.
/// The bug was that an empty folder string sent by the Settings page (e.g. a save
/// issued before the page finished rendering) OVERWROTE the saved ascent/clips/backup
/// folders with "", because the save handler only skipped nulls. The guard now treats
/// empty as "leave unchanged" and a sentinel as "explicit clear".
/// </summary>
public sealed class ConfigSaveGuardsTests
{
    // ── TryResolveFolderWrite resolution table ────────────────────────────────

    [Fact]
    public void Null_LeavesUnchanged()
    {
        Assert.False(ConfigSaveGuards.TryResolveFolderWrite(null, out var resolved));
        Assert.Equal("", resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void EmptyOrWhitespace_LeavesUnchanged(string value)
    {
        // The core of P-023: a blank folder must NOT blank the saved path.
        Assert.False(ConfigSaveGuards.TryResolveFolderWrite(value, out var resolved));
        Assert.Equal("", resolved);
    }

    [Fact]
    public void Sentinel_ResolvesToExplicitEmpty()
    {
        // The Clear button's deliberate clear DOES blank the path.
        Assert.True(ConfigSaveGuards.TryResolveFolderWrite(ConfigSaveGuards.FolderClearSentinel, out var resolved));
        Assert.Equal("", resolved);
    }

    [Fact]
    public void RealPath_ResolvesTrimmed()
    {
        Assert.True(ConfigSaveGuards.TryResolveFolderWrite(@"  C:\Users\me\Videos\Ascent  ", out var resolved));
        Assert.Equal(@"C:\Users\me\Videos\Ascent", resolved);
    }

    // ── End-to-end: the save read-modify-write semantics the handler relies on ──
    // This mirrors POST /api/config/save's folder branch against a populated config
    // to prove the exact reported fingerprint can't recur: an unrelated save with
    // empty folder inputs leaves the saved folders AND riot identity intact.

    [Fact]
    public void EmptyFolderInputs_DoNotBlankExistingFolders_WhileOtherFieldsSave()
    {
        // A config as it would be on disk after the user configured everything.
        var cfg = new AppConfig
        {
            AscentFolder = @"C:\Users\me\Videos\Ascent",
            ClipsFolder = @"C:\Users\me\Videos\Clips",
            BackupFolder = @"C:\Users\me\Backups",
            RiotId = "bye#world",
            RiotRegion = "na1",
            PrimaryRole = "BOTTOM",
        };

        // Simulate the handler's folder branch with EMPTY folder inputs (the bug
        // trigger: page not rendered, so the three folder fields are "") while an
        // unrelated field (region) carries a real value.
        ApplyFolderWrite(cfg, ascent: "", clips: "", backup: "");
        cfg.RiotRegion = "na1"; // an unrelated non-folder field still saving fine

        Assert.Equal(@"C:\Users\me\Videos\Ascent", cfg.AscentFolder);
        Assert.Equal(@"C:\Users\me\Videos\Clips", cfg.ClipsFolder);
        Assert.Equal(@"C:\Users\me\Backups", cfg.BackupFolder);
        Assert.Equal("bye#world", cfg.RiotId);
        Assert.Equal("na1", cfg.RiotRegion);
        Assert.Equal("BOTTOM", cfg.PrimaryRole);
    }

    [Fact]
    public void ExplicitClearSentinel_BlanksOnlyThatFolder()
    {
        var cfg = new AppConfig
        {
            AscentFolder = @"C:\Users\me\Videos\Ascent",
            ClipsFolder = @"C:\Users\me\Videos\Clips",
            BackupFolder = @"C:\Users\me\Backups",
        };

        // User pressed Clear on Ascent only; clips/backup inputs were empty (unchanged).
        ApplyFolderWrite(cfg, ascent: ConfigSaveGuards.FolderClearSentinel, clips: "", backup: "");

        Assert.Equal("", cfg.AscentFolder);                       // cleared
        Assert.Equal(@"C:\Users\me\Videos\Clips", cfg.ClipsFolder); // untouched
        Assert.Equal(@"C:\Users\me\Backups", cfg.BackupFolder);     // untouched
    }

    // Mirrors the three folder branches of POST /api/config/save (Program.cs).
    private static void ApplyFolderWrite(AppConfig cfg, string? ascent, string? clips, string? backup)
    {
        if (ConfigSaveGuards.TryResolveFolderWrite(ascent, out var a)) cfg.AscentFolder = a;
        if (ConfigSaveGuards.TryResolveFolderWrite(clips, out var c)) cfg.ClipsFolder = c;
        if (ConfigSaveGuards.TryResolveFolderWrite(backup, out var b)) cfg.BackupFolder = b;
    }
}
