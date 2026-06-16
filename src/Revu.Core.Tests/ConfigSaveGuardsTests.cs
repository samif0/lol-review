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

    // ── TryResolveIdentityWrite (riot_id / region) — same empty-overwrite class ──
    // The folder guard left the Riot identity fields exposed: settings.js sent
    // riotId/region unconditionally and the handler applied them with a bare null
    // check, so a pre-render save sending "" blanked the linked account (detaching
    // match-history sync). Identity has no clear affordance, so empty ALWAYS means
    // "leave unchanged" — there is no sentinel.

    [Fact]
    public void Identity_Null_LeavesUnchanged()
    {
        Assert.False(ConfigSaveGuards.TryResolveIdentityWrite(null, out var resolved));
        Assert.Equal("", resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Identity_EmptyOrWhitespace_LeavesUnchanged(string value)
    {
        // The core fix: a blank riot_id/region input must NOT blank the saved identity.
        Assert.False(ConfigSaveGuards.TryResolveIdentityWrite(value, out var resolved));
        Assert.Equal("", resolved);
    }

    [Fact]
    public void Identity_RealValue_ResolvesTrimmed()
    {
        Assert.True(ConfigSaveGuards.TryResolveIdentityWrite("  bye#world  ", out var resolved));
        Assert.Equal("bye#world", resolved);
    }

    [Fact]
    public void Identity_HasNoClearSentinel()
    {
        // Unlike folders, the folder-clear sentinel is just an ordinary value here —
        // it is trimmed and written, never treated as "clear" (no identity clear path).
        Assert.True(ConfigSaveGuards.TryResolveIdentityWrite(ConfigSaveGuards.FolderClearSentinel, out var resolved));
        Assert.Equal("__REVU_CLEAR__", resolved);
    }

    // End-to-end: the save handler's riot branch with EMPTY inputs must leave the
    // stored identity intact. This is the case the folder-only test never exercised.

    [Fact]
    public void EmptyRiotInputs_DoNotBlankExistingIdentity_WhileOtherFieldsSave()
    {
        var cfg = new AppConfig
        {
            AscentFolder = @"C:\Users\me\Videos\Ascent",
            RiotId = "bye#world",
            RiotRegion = "na1",
            RiotSessionExpiresAt = 9999999999, // a live session in the far future
            PrimaryRole = "BOTTOM",
        };

        // The bug trigger: a save with empty riotId/region inputs (page not rendered)
        // while an unrelated folder field carries a real value.
        ApplyIdentityWrite(cfg, riotId: "", region: "");
        if (ConfigSaveGuards.TryResolveFolderWrite(@"C:\Users\me\Videos\Ascent", out var a)) cfg.AscentFolder = a;

        Assert.Equal("bye#world", cfg.RiotId);          // identity survived
        Assert.Equal("na1", cfg.RiotRegion);
        Assert.Equal(9999999999, cfg.RiotSessionExpiresAt); // session untouched by config-save
        Assert.Equal(@"C:\Users\me\Videos\Ascent", cfg.AscentFolder);
    }

    [Fact]
    public void RealRiotInputs_OverwriteAndLowercaseRegion()
    {
        var cfg = new AppConfig { RiotId = "old#tag", RiotRegion = "na1" };

        // A genuine edit goes through; region is lower-cased at the call site.
        ApplyIdentityWrite(cfg, riotId: "  new#name  ", region: "  EUW1  ");

        Assert.Equal("new#name", cfg.RiotId);
        Assert.Equal("euw1", cfg.RiotRegion);
    }

    // Mirrors the riot-identity branch of POST /api/config/save (Program.cs): region
    // is lower-cased at the call site on the already-trimmed resolved value.
    private static void ApplyIdentityWrite(AppConfig cfg, string? riotId, string? region)
    {
        if (ConfigSaveGuards.TryResolveIdentityWrite(riotId, out var rid)) cfg.RiotId = rid;
        if (ConfigSaveGuards.TryResolveIdentityWrite(region, out var rgn)) cfg.RiotRegion = rgn.ToLowerInvariant();
    }
}
