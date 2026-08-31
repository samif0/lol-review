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

    // ── TryResolveTextWrite (riot_id / region) — P-020 identity-clobber guard ─────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void TextWrite_NullOrBlank_LeavesUnchanged(string? value)
    {
        // The P-020 fingerprint: a Save before the Settings page hydrated sends
        // riotId="" / region="" (the <select> default). That must NOT blank a
        // configured account.
        Assert.False(ConfigSaveGuards.TryResolveTextWrite(value, out var resolved));
        Assert.Equal("", resolved);
    }

    [Fact]
    public void TextWrite_RealValue_ResolvesTrimmed()
    {
        Assert.True(ConfigSaveGuards.TryResolveTextWrite("  hello#world  ", out var resolved));
        Assert.Equal("hello#world", resolved);
    }

    [Fact]
    public void TextWrite_HasNoClearSentinel()
    {
        // Unlike folders, the folder-clear sentinel is just an ordinary value here —
        // it is trimmed and written, never treated as "clear" (no identity clear path).
        Assert.True(ConfigSaveGuards.TryResolveTextWrite(ConfigSaveGuards.FolderClearSentinel, out var resolved));
        Assert.Equal("__REVU_CLEAR__", resolved);
    }

    [Fact]
    public void EmptyRiotIdAndRegion_DoNotBlankConfiguredAccount()
    {
        // The exact recurrence guard: an unhydrated/empty Settings save must leave
        // a configured Riot ID + region intact (the dashboard greeting + Riot-proxy
        // features depend on them).
        var cfg = new AppConfig { RiotId = "chapy#na1", RiotRegion = "na1" };

        // Mirror POST /api/config/save's riot branch with empty inputs.
        if (ConfigSaveGuards.TryResolveTextWrite("", out var rid)) cfg.RiotId = rid;
        if (ConfigSaveGuards.TryResolveTextWrite("", out var reg)) cfg.RiotRegion = reg.ToLowerInvariant();

        Assert.Equal("chapy#na1", cfg.RiotId);
        Assert.Equal("na1", cfg.RiotRegion);
    }

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
        if (ConfigSaveGuards.TryResolveTextWrite("", out var rid)) cfg.RiotId = rid;
        if (ConfigSaveGuards.TryResolveTextWrite("", out var rgn)) cfg.RiotRegion = rgn.ToLowerInvariant();
        if (ConfigSaveGuards.TryResolveFolderWrite(@"C:\Users\me\Videos\Ascent", out var a)) cfg.AscentFolder = a;

        Assert.Equal("bye#world", cfg.RiotId);          // identity survived
        Assert.Equal("na1", cfg.RiotRegion);
        Assert.Equal(9999999999, cfg.RiotSessionExpiresAt); // session untouched by config-save
        Assert.Equal(@"C:\Users\me\Videos\Ascent", cfg.AscentFolder);
    }

    [Fact]
    public void RealRegion_IsLowerCasedLikeTheHandler()
    {
        var cfg = new AppConfig { RiotRegion = "na1" };
        if (ConfigSaveGuards.TryResolveTextWrite("EUW1", out var reg)) cfg.RiotRegion = reg.ToLowerInvariant();
        Assert.Equal("euw1", cfg.RiotRegion);
    }

    // ── TryResolveWindowResolution (Settings → window size) ───────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WindowResolution_NullOrBlank_LeavesUnchanged(string? value)
    {
        Assert.False(ConfigSaveGuards.TryResolveWindowResolution(value, out var resolved));
        Assert.Equal("", resolved);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    [InlineData("  Default  ")]
    public void WindowResolution_Default_StoresEmpty(string value)
    {
        // "default" is a REAL choice (returns true) that stores "" — distinct from
        // blank, which is "leave unchanged". This is what lets a user switch BACK
        // to the default size after saving Maximized.
        Assert.True(ConfigSaveGuards.TryResolveWindowResolution(value, out var resolved));
        Assert.Equal("", resolved);
    }

    [Theory]
    [InlineData("maximized")]
    [InlineData("Maximized")]
    public void WindowResolution_Maximized_Resolves(string value)
    {
        Assert.True(ConfigSaveGuards.TryResolveWindowResolution(value, out var resolved));
        Assert.Equal("maximized", resolved);
    }

    [Theory]
    [InlineData("1920x1080", "1920x1080")]
    [InlineData("2560x1440", "2560x1440")]
    [InlineData(" 1920 X 1080 ", "1920x1080")] // normalized: trimmed, lowercase x
    [InlineData("980x640", "980x640")]         // exact window minimum is allowed
    public void WindowResolution_ValidSize_ResolvesNormalized(string value, string expected)
    {
        Assert.True(ConfigSaveGuards.TryResolveWindowResolution(value, out var resolved));
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("800x600")]        // below the window's minWidth (980)
    [InlineData("1920x480")]       // below the window's minHeight (640)
    [InlineData("99999x99999")]    // absurd
    [InlineData("1920")]           // no separator
    [InlineData("1920x")]          // missing height
    [InlineData("axb")]            // not numbers
    [InlineData("1920x1080x60")]   // too many parts
    public void WindowResolution_Invalid_LeavesUnchanged(string value)
    {
        Assert.False(ConfigSaveGuards.TryResolveWindowResolution(value, out var resolved));
        Assert.Equal("", resolved);
    }

    [Fact]
    public void WindowResolution_GarbageSave_DoesNotClobberSavedPreference()
    {
        // Mirror the handler branch: an invalid value must leave a saved
        // "maximized" preference intact.
        var cfg = new AppConfig { WindowResolution = "maximized" };
        if (ConfigSaveGuards.TryResolveWindowResolution("nonsense", out var wr)) cfg.WindowResolution = wr;
        Assert.Equal("maximized", cfg.WindowResolution);

        // ...while a deliberate switch back to Default does reset it.
        if (ConfigSaveGuards.TryResolveWindowResolution("default", out var wr2)) cfg.WindowResolution = wr2;
        Assert.Equal("", cfg.WindowResolution);
    }
}
