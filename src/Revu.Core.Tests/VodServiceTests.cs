using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class VodServiceTests
{
    private static readonly DateTime Start = new(2026, 9, 11, 17, 25, 0, DateTimeKind.Local);
    private static long Unix(DateTime date) => new DateTimeOffset(date).ToUnixTimeSeconds();
    private static GameStats Game(long id, DateTime? start = null, int duration = 1800)
    {
        var game = TestGameStatsFactory.Create(id);
        game.Timestamp = Unix(start ?? Start); game.GameDuration = duration;
        return game;
    }
    private static VodService Service(TestDatabaseScope db, TestConfigService config) =>
        new(db.Games, db.Vod, config, NullLogger<VodService>.Instance);

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(-61, 9, false)]
    [InlineData(-120, 30, false)]
    [InlineData(0, 0, true)]
    public void RecognizedRecordingWindowSupportsLoadingVarianceAndLegacyEndTime(int startDelta, int endDelta, bool legacyEnd)
    {
        using var db = new TestDatabaseScope();
        var game = Game(1001);
        if (legacyEnd) game.Timestamp += game.GameDuration;
        var recording = new VodRecordingInfo("ascent.mp4", "ascent.mp4", 4,
            Unix(Start.AddSeconds(1800 + endDelta)), Unix(Start.AddSeconds(startDelta)), "");
        Assert.Equal(recording.Path, Service(db, new()).MatchRecordingToGame(game, [recording]));
    }

    [Fact]
    public void UnknownNamesShortAndUnrelatedRecordingsAreNotGuessedFromModificationTime()
    {
        using var db = new TestDatabaseScope();
        var service = Service(db, new());
        var valid = new VodRecordingInfo("full.mp4", "full.mp4", 4, Unix(Start.AddMinutes(30)), Unix(Start), "");
        Assert.Null(service.MatchRecordingToGame(Game(1001), [valid with { StartTs = null }]));
        Assert.Null(service.MatchRecordingToGame(Game(1001), [valid with { Mtime = Unix(Start.AddMinutes(3)) }]));
        Assert.Null(service.MatchRecordingToGame(Game(1001), [valid with { StartTs = Unix(Start.AddHours(-4)) }]));
    }

    [Fact]
    public async Task ScanLinksFinishedNestedVideosOnceAndPreservesExistingRowsAndMedia()
    {
        using var db = new TestDatabaseScope(); await db.InitializeAsync();
        using var folder = new MediaFolder();
        var file = folder.Video("nested/09-11-2026-17-25.mp4");
        await db.Games.SaveAsync(Game(1001));
        await db.Games.SaveAsync(Game(1002, Start.AddHours(2)));
        await db.Vod.LinkVodAsync(1002, "missing-user-vod.mp4", 32, 1700);
        var existing = await db.Vod.GetVodAsync(1002);
        var config = new TestConfigService(new AppConfig { AscentFolder = folder.Root });
        var service = Service(db, config);
        var result = await service.ScanAsync();
        Assert.True(result.Ok); Assert.Equal(1, result.Matched); Assert.Equal(1, result.RecordingCount);
        Assert.Equal(new long[] { 1001 }, result.LinkedGameIds);
        Assert.Equal(file, (await db.Vod.GetVodAsync(1001))!.FilePath);
        Assert.Equal(0, RecordingTimeline.ReadGameTimeAtVideoStart(file, 1001));
        Assert.False(File.Exists(file + RecordingTimeline.Suffix));
        Assert.Equal(existing, await db.Vod.GetVodAsync(1002));
        Assert.Equal(0, (await service.ScanAsync()).Matched);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(file));
        config.Current.AscentFolder = "";
        Assert.False((await service.ScanAsync()).Ok);
        Assert.Empty(await service.FindRecordingsAsync());
        Assert.Equal(2, (await db.Vod.GetAllVodsAsync()).Count);
    }

    [WindowsFact]
    public async Task BusyRecentEmptyAndChangingFilesWaitUntilFinalized()
    {
        using var db = new TestDatabaseScope(); await db.InitializeAsync();
        using var folder = new MediaFolder();
        var file = folder.Video("09-11-2026-17-25.mp4");
        var empty = folder.Video("empty.mp4"); File.WriteAllBytes(empty, []);
        await db.Games.SaveAsync(Game(1001));
        var service = Service(db, new(new AppConfig { AscentFolder = folder.Root }));
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            var result = await service.ScanAsync();
            Assert.Equal(2, result.RecordingCount); Assert.Equal(0, result.Matched);
            Assert.Contains("still settling", result.Message);
        }
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        Assert.Equal(0, (await service.ScanAsync()).Matched);
        File.SetLastWriteTime(file, Start.AddMinutes(30));
        var observing = service.FindRecordingsAsync();
        await Task.Delay(50);
        File.AppendAllText(file, "changes");
        Assert.False((await observing).Single(r => r.Path == file).IsReady);
        File.SetLastWriteTime(file, Start.AddMinutes(30));
        Assert.Equal(1, (await service.ScanAsync()).Matched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    public async Task DuplicateOfOccupiedPreviousMatchNeverSpillsIntoNextMatch(int finalizationDelay)
    {
        using var db = new TestDatabaseScope(); await db.InitializeAsync();
        using var folder = new MediaFolder();
        var a = new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Local);
        folder.Video("09-11-2026-10-00.mp4", a.AddMinutes(20).AddSeconds(finalizationDelay));
        await db.Games.SaveAsync(Game(1001, a, 1200));
        await db.Games.SaveAsync(Game(1002, a.AddMinutes(21), 1260));
        await db.Vod.LinkVodAsync(1001, "native-primary.mp4");
        var result = await Service(db, new(new AppConfig { AscentFolder = folder.Root })).ScanAsync();
        Assert.Equal(0, result.Matched);
        Assert.Null(await db.Vod.GetVodAsync(1002));
        Assert.Equal("native-primary.mp4", (await db.Vod.GetVodAsync(1001))!.FilePath);
    }

    [Fact]
    public async Task ConcurrentScannersCannotReplacePrimaryOrReuseCaseInsensitiveFileOwnership()
    {
        using var db = new TestDatabaseScope(); await db.InitializeAsync();
        using var folder = new MediaFolder();
        var file = folder.Video("09-11-2026-17-25.mp4");
        await db.Games.SaveAsync(Game(1001));
        await db.Games.SaveAsync(Game(1002, Start.AddHours(4)));
        var config = new TestConfigService(new AppConfig { AscentFolder = folder.Root });
        var results = await Task.WhenAll(Service(db, config).ScanAsync(), Service(db, config).ScanAsync());
        Assert.Equal(1, results.Sum(r => r.Matched));
        Assert.False(await db.Vod.TryLinkUnownedVodAsync(1002, file.ToUpperInvariant()));
        Assert.False(await db.Vod.TryLinkUnownedVodAsync(1001, "another-file.mp4"));
        Assert.False(await db.Vod.TryLinkUnownedVodAsync(9999, "missing-game.mp4"));
        Assert.Single(await db.Vod.GetAllVodsAsync());
    }

    [Fact]
    public async Task ReservedNativeIdentityCannotBeLinkedAndMalformedHistoricalPathsDoNotAbortScan()
    {
        using var db = new TestDatabaseScope(); await db.InitializeAsync();
        using var folder = new MediaFolder();
        folder.Video("09-11-2026-17-25.mp4");
        await db.Games.SaveAsync(Game(1001)); await db.Games.SaveAsync(Game(1002, Start.AddHours(4)));
        await db.Vod.LinkVodAsync(1002, "\0malformed");
        var service = Service(db, new(new AppConfig { AscentFolder = folder.Root }));
        Assert.Equal(0, (await service.ScanAsync(reservedGameIds: new HashSet<long> { 1001 })).Matched);
        Assert.Null(await db.Vod.GetVodAsync(1001));
        Assert.Equal(1, (await service.ScanAsync()).Matched);
    }

    [WindowsFact]
    public async Task DirectoryJunctionsAreSkippedWithoutFollowingLoops()
    {
        using var db = new TestDatabaseScope(); using var folder = new MediaFolder();
        var video = folder.Video("09-11-2026-17-25.mp4");
        var junction = Path.Combine(folder.Root, "loop");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{junction}\" \"{folder.Root}\""
        })!;
        await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        var service = Service(db, new(new AppConfig { AscentFolder = folder.Root }));
        try
        {
            Assert.Equal(video, Assert.Single(await service.FindRecordingsAsync()).Path);
            Assert.Empty(await service.FindRecordingsAsync(junction));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task CancelledScanDoesNotLinkAnything()
    {
        using var db = new TestDatabaseScope(); await db.InitializeAsync();
        using var folder = new MediaFolder(); folder.Video("09-11-2026-17-25.mp4");
        await db.Games.SaveAsync(Game(1001));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(db, new(new AppConfig { AscentFolder = folder.Root })).ScanAsync(stop.Token));
        Assert.Empty(await db.Vod.GetAllVodsAsync());
    }

    private sealed class MediaFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Revu.Ascent.Tests", Guid.NewGuid().ToString("N"));
        public MediaFolder() => Directory.CreateDirectory(Root);
        public string Video(string name, DateTime? end = null)
        {
            var file = Path.GetFullPath(Path.Combine(Root, name)); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, [1, 2, 3, 4]); File.SetLastWriteTime(file, end ?? Start.AddMinutes(30));
            return file;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
