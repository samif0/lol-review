using Revu.Core.Data;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// P-041 end-to-end contracts for the /api/settings/scan-vods path, built on the
/// REAL WriteSqliteConnectionFactory (the graph the endpoint runs) instead of the
/// test-only factory — because the field failure ("SQLite Error 14" on every
/// scan) lived exactly in that seam.
/// </summary>
public sealed class ScanVodsPathTests : IDisposable
{
    private readonly string _root;

    public ScanVodsPathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record ScanHarness(
        WriteSqliteConnectionFactory Factory,
        GameRepository Games,
        VodRepository Vods,
        VodService Service,
        string AscentFolder,
        string DbPath);

    private ScanHarness BuildHarness(string? dbFileName = "revu.db")
    {
        var dbPath = Path.Combine(_root, "data", dbFileName!);
        var ascent = Path.Combine(_root, "Ascent");
        Directory.CreateDirectory(ascent);

        var factory = new WriteSqliteConnectionFactory(
            NullLogger<WriteSqliteConnectionFactory>.Instance, NullLoggerFactory.Instance, dbPath);
        var games = new GameRepository(factory, new NoopBackupService());
        var vods = new VodRepository(factory);
        var service = new VodService(
            games, vods,
            new TestConfigService(new AppConfig { AscentFolder = ascent }),
            NullLogger<VodService>.Instance);

        return new ScanHarness(factory, games, vods, service, ascent, dbPath);
    }

    private static string CreateRecording(string folder, string fileName, DateTime start, int durationSeconds)
    {
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTime(path, start.AddSeconds(durationSeconds + 20));
        return path;
    }

    private static long ToUnixSeconds(DateTime localTime) =>
        new DateTimeOffset(localTime).ToUnixTimeSeconds();

    /// <summary>
    /// The endpoint's DB-failure catch filter — the SAME shared predicate
    /// Program.cs uses (SqliteOpenHealth.IndicatesDatabaseUnavailable), so this
    /// suite tests the real classification, not a copy that can drift.
    /// </summary>
    private static bool EndpointCatchesAsDbFailure(Exception ex) =>
        SqliteOpenHealth.IndicatesDatabaseUnavailable(ex);

    // ── The exact field flow: fresh install → set folder → scan ─────────────

    [Fact]
    public async Task FreshInstall_FirstEverWriteIsTheScan_RecoversDbAndMatches()
    {
        var h = BuildHarness();

        // The DB does not exist yet (startup creation "failed" — never ran here).
        Assert.False(File.Exists(h.DbPath));

        var start = new DateTime(2026, 7, 10, 20, 36, 0, DateTimeKind.Local);
        CreateRecording(h.AscentFolder, "07-10-2026-20-36.mp4", start, 1917);

        // Saving the game is itself a write → triggers never-created recovery.
        var game = TestGameStatsFactory.Create(
            gameId: 42, timestamp: ToUnixSeconds(start.AddSeconds(14)), durationSeconds: 1917);
        await h.Games.SaveAsync(game);

        var matched = await h.Service.AutoMatchRecordingsAsync();

        Assert.Equal(1, matched);
        var vod = await h.Vods.GetVodAsync(42);
        Assert.NotNull(vod);
        Assert.EndsWith("07-10-2026-20-36.mp4", vod!.FilePath);
    }

    [Fact]
    public async Task FreshInstall_ScanWithNoGames_SucceedsWithZeroMatches()
    {
        var h = BuildHarness();
        var start = new DateTime(2026, 7, 10, 18, 0, 0, DateTimeKind.Local);
        CreateRecording(h.AscentFolder, "07-10-2026-18-00.mp4", start, 1800);

        // First DB touch inside AutoMatch recovers the never-created DB, then the
        // scan completes normally: recordings found, nothing to match.
        var matched = await h.Service.AutoMatchRecordingsAsync();
        var recordings = await h.Service.FindRecordingsAsync();

        Assert.Equal(0, matched);
        Assert.Single(recordings);
        Assert.True(File.Exists(h.DbPath));
    }

    // ── DB-failure classification: what the endpoint's catch must see ────────

    [Fact]
    public async Task VanishedDb_SurfacesAsDatabaseUnavailable_WhichTheEndpointCatches()
    {
        var h = BuildHarness();
        await h.Games.SaveAsync(TestGameStatsFactory.Create(gameId: 1)); // creates DB

        var factory2 = new WriteSqliteConnectionFactory(
            NullLogger<WriteSqliteConnectionFactory>.Instance, NullLoggerFactory.Instance, h.DbPath);
        var service2 = new VodService(
            new GameRepository(factory2, new NoopBackupService()),
            new VodRepository(factory2),
            new TestConfigService(new AppConfig { AscentFolder = h.AscentFolder }),
            NullLogger<VodService>.Instance);
        CreateRecording(h.AscentFolder, "07-11-2026-10-00.mp4",
            new DateTime(2026, 7, 11, 10, 0, 0, DateTimeKind.Local), 1800);

        SqliteConnection.ClearAllPools();
        File.Delete(h.DbPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(h.DbPath + suffix)) File.Delete(h.DbPath + suffix);
        }

        var ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(
            () => service2.AutoMatchRecordingsAsync());

        Assert.True(EndpointCatchesAsDbFailure(ex));
        Assert.Contains("missing", ex.Reason);
        // The user-facing text the endpoint would compose is actionable:
        var text = VodScanMessages.DatabaseFailure(1, ex.Reason);
        Assert.Contains("Found 1 recording(s)", text);
        Assert.Contains("missing", text);
        Assert.DoesNotContain("SQLite Error 14", text);
    }

    [Fact]
    public void RawSqliteCantOpen_IsAlsoCaughtByTheEndpointFilter()
    {
        // Belt-and-suspenders: if a future code path throws the raw
        // SqliteException instead of the factory's wrapped one, the endpoint's
        // filter must still route it to the actionable-message branch.
        var raw = new SqliteException("unable to open database file", 14);
        Assert.True(EndpointCatchesAsDbFailure(raw));

        var readOnly = new SqliteException("attempt to write a readonly database", 8);
        Assert.True(EndpointCatchesAsDbFailure(readOnly));

        // ...but ordinary scan failures must NOT be re-labelled as DB failures.
        Assert.False(EndpointCatchesAsDbFailure(new SqliteException("no such table: vod_files", 1)));
        Assert.False(EndpointCatchesAsDbFailure(new IOException("folder unreadable")));
    }

    [Fact]
    public async Task ScanWithNoAscentFolderConfigured_ReturnsZero_WithoutTouchingTheDb()
    {
        // No folder set: AutoMatch must early-return before any DB access — even
        // a completely broken DB path can't produce the SQLite Error 14 here.
        var h = BuildHarness();
        var service = new VodService(
            h.Games, h.Vods,
            new TestConfigService(new AppConfig { AscentFolder = "" }),
            NullLogger<VodService>.Instance);

        Assert.Equal(0, await service.AutoMatchRecordingsAsync());
        Assert.False(File.Exists(h.DbPath)); // no write happened, so no recovery ran
    }

    // ── AutoMatch correctness (previously untested write leg of the scan) ────

    [Fact]
    public async Task AutoMatch_AssignsEachGameItsOwnRecording_AndRescanIsIdempotent()
    {
        var h = BuildHarness();

        var startA = new DateTime(2026, 7, 9, 18, 0, 0, DateTimeKind.Local);
        var startB = new DateTime(2026, 7, 9, 21, 30, 0, DateTimeKind.Local);
        CreateRecording(h.AscentFolder, "07-09-2026-18-00.mp4", startA, 1800);
        CreateRecording(h.AscentFolder, "07-09-2026-21-30.mp4", startB, 2100);

        await h.Games.SaveAsync(TestGameStatsFactory.Create(
            gameId: 101, timestamp: ToUnixSeconds(startA.AddSeconds(20)), durationSeconds: 1800));
        await h.Games.SaveAsync(TestGameStatsFactory.Create(
            gameId: 102, champion: "Sivir", timestamp: ToUnixSeconds(startB.AddSeconds(20)), durationSeconds: 2100));

        var matched = await h.Service.AutoMatchRecordingsAsync();
        Assert.Equal(2, matched);

        var vodA = await h.Vods.GetVodAsync(101);
        var vodB = await h.Vods.GetVodAsync(102);
        Assert.NotNull(vodA);
        Assert.NotNull(vodB);
        Assert.EndsWith("07-09-2026-18-00.mp4", vodA!.FilePath);
        Assert.EndsWith("07-09-2026-21-30.mp4", vodB!.FilePath);

        // Second scan: same folder, same games — nothing new to link, no
        // double-link of already-used paths.
        Assert.Equal(0, await h.Service.AutoMatchRecordingsAsync());
    }

    [Fact]
    public async Task AutoMatch_NeverStealsARecordingAlreadyLinkedToAnotherGame()
    {
        var h = BuildHarness();
        var start = new DateTime(2026, 7, 8, 20, 0, 0, DateTimeKind.Local);
        var path = CreateRecording(h.AscentFolder, "07-08-2026-20-00.mp4", start, 1800);

        await h.Games.SaveAsync(TestGameStatsFactory.Create(
            gameId: 201, timestamp: ToUnixSeconds(start.AddSeconds(10)), durationSeconds: 1800));
        await h.Vods.LinkVodAsync(201, path, new FileInfo(path).Length);

        // A second game inside the same window must not take the linked file.
        await h.Games.SaveAsync(TestGameStatsFactory.Create(
            gameId: 202, champion: "Jinx", timestamp: ToUnixSeconds(start.AddSeconds(60)), durationSeconds: 1700));

        Assert.Equal(0, await h.Service.AutoMatchRecordingsAsync());
        Assert.Null(await h.Vods.GetVodAsync(202));
    }

    // ── Scan robustness at scale (186-recording folders are the norm) ────────

    [Fact]
    public async Task Scan_HandlesLargeFolders_SubdirectoriesAndJunkNames()
    {
        var h = BuildHarness();
        var start = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local);

        // 200 parseable recordings spread across the root and a subfolder.
        for (var i = 0; i < 200; i++)
        {
            var folder = i % 3 == 0
                ? Path.Combine(h.AscentFolder, "sub")
                : h.AscentFolder;
            Directory.CreateDirectory(folder);
            var ts = start.AddMinutes(-40 * i);
            CreateRecording(folder, $"{ts:MM-dd-yyyy-HH-mm}.mp4", ts, 1800);
        }

        // Junk that must not break enumeration: unparseable names, non-video
        // files, and a video locked exclusively by another process (mid-encode).
        CreateRecording(h.AscentFolder, "totally random name.mp4", start.AddDays(1), 60);
        File.WriteAllText(Path.Combine(h.AscentFolder, "notes.txt"), "not a video");
        var locked = Path.Combine(h.AscentFolder, "encoding in progress.mkv");
        File.WriteAllBytes(locked, [9, 9, 9]);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var recordings = await h.Service.FindRecordingsAsync();

        // 200 parseable + the unparseable-name video + the locked video; the txt
        // file is excluded by extension.
        Assert.Equal(202, recordings.Count);
        Assert.DoesNotContain(recordings, r => r.Name == "notes.txt");
        // Newest-first ordering by start-ts/mtime holds across subfolders.
        var keys = recordings.Select(r => r.StartTs ?? r.Mtime).ToList();
        Assert.Equal(keys.OrderByDescending(k => k).ToList(), keys);
    }
}
