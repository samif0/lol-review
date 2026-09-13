using System.Runtime.InteropServices;
using Revu.Core.Services;
using Revu.Core.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class RecordingRegistrationTests
{
    [Fact]
    public async Task CompleteRecordingLinksExactMatchAndExactRetryIsIdempotent()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        var request = fixture.CreateRecording(1001);
        Assert.Equal("linked", (await fixture.Service.RegisterAsync(request)).Status);
        Assert.Equal("linked", (await fixture.Service.RegisterAsync(request)).Status);
        var vod = Assert.Single(await fixture.Database.Vod.GetAllVodsAsync());
        Assert.Equal(request.FilePath, vod.FilePath);
        Assert.Equal(1001, vod.GameId);
        Assert.Equal(-30.25, RecordingTimeline.ReadGameTimeAtVideoStart(request.FilePath, request.GameId));
        Assert.Equal(request.SessionId, (await fixture.Service.GetAsync(request.SessionId))!.SessionId);
    }

    [Fact]
    public async Task VodSnapshotExposesOffsetWhileBookmarkTimesRemainOnGameClock()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        var request = fixture.CreateRecording(1001);
        await fixture.Service.RegisterAsync(request);
        await fixture.Database.Vod.AddBookmarkAsync(1001, 600, "game time");
        var db = fixture.Database;
        var builder = new VodSnapshotBuilder(db.Games, db.Vod, new GameEventsRepository(db.ConnectionFactory),
            db.Evidence, db.Objectives, db.Config, NullLogger<VodSnapshotBuilder>.Instance);
        var snapshot = await builder.BuildAsync(1001);
        Assert.True(snapshot.HasVod);
        Assert.Equal(-30.25, snapshot.GameTimeAtVideoStart);
        Assert.Equal(600, Assert.Single(snapshot.Bookmarks).GameTimeSeconds);
        File.WriteAllText(request.FilePath + RecordingTimeline.Suffix, "broken");
        Assert.False((await builder.BuildAsync(1001)).HasVod);
    }

    [Fact]
    public async Task CompleteRecordingRequiresAnchorAndPartialAllowsUnknownAnchor()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.CreateRecording(1001);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RegisterAsync(request with { GameTimeAtVideoStart = null }));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RegisterAsync(request with { GameTimeAtVideoStart = -601 }));
        Assert.Equal("retained-partial", (await fixture.Service.RegisterAsync(request with { Complete = false, GameTimeAtVideoStart = null })).Status);
    }

    [Fact]
    public async Task MissingMatchPersistsAndNewServiceReconcilesOnlyThatIdentity()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        var request = fixture.CreateRecording(2002);
        Assert.Equal("pending-match", (await fixture.Service.RegisterAsync(request)).Status);
        Assert.Empty(await fixture.Database.Vod.GetAllVodsAsync());
        Assert.False(File.Exists(request.FilePath + RecordingTimeline.Suffix));
        var restarted = fixture.NewService();
        await restarted.ReconcileAsync(1001);
        Assert.Equal("pending-match", (await restarted.GetAsync(request.SessionId))!.Status);
        await fixture.Database.SeedGameAsync(2002);
        await restarted.ReconcileAsync();
        Assert.Equal("linked", (await restarted.GetAsync(request.SessionId))!.Status);
        Assert.Null(await fixture.Database.Vod.GetVodAsync(1001));
        Assert.Equal(request.FilePath, (await fixture.Database.Vod.GetVodAsync(2002))!.FilePath);
    }

    [Fact]
    public async Task InterruptedSegmentsStaySeparateAndNeverBecomePrimaryVideos()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        var first = fixture.CreateRecording(1001) with { Complete = false };
        var second = fixture.CreateRecording(1001) with { Complete = false };
        Assert.Equal("retained-partial", (await fixture.Service.RegisterAsync(first)).Status);
        Assert.Equal("retained-partial", (await fixture.Service.RegisterAsync(second)).Status);
        await fixture.NewService().ReconcileAsync();
        Assert.Empty(await fixture.Database.Vod.GetAllVodsAsync());
        Assert.True(File.Exists(first.FilePath));
        Assert.False(File.Exists(first.FilePath + RecordingTimeline.Suffix));
        Assert.True(File.Exists(second.FilePath));
        await Assert.ThrowsAsync<RecordingConflictException>(() => fixture.Service.RegisterAsync(first with { Complete = true }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalScanRespectsPersistedNativeReceiptEvenWhenNativeMediaIsTemporarilyUnavailable(bool mediaUnavailable)
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.CreateRecording(2002);
        Assert.Equal("pending-match", (await fixture.Service.RegisterAsync(request)).Status);
        var start = new DateTime(2026, 9, 11, 17, 25, 0, DateTimeKind.Local);
        await fixture.Database.SeedGameAsync(2002, timestamp: new DateTimeOffset(start).ToUnixTimeSeconds());
        var external = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "Ascent"); Directory.CreateDirectory(external);
        var video = Path.Combine(external, "09-11-2026-17-25.mp4"); File.WriteAllBytes(video, new byte[32]);
        File.SetLastWriteTime(video, start.AddMinutes(30));
        fixture.Database.Config.Current.AscentFolder = external;
        if (mediaUnavailable) File.WriteAllBytes(request.FilePath, []);
        var scan = new VodService(fixture.Database.Games, fixture.Database.Vod, fixture.Database.Config, NullLogger<VodService>.Instance);
        var result = await fixture.Service.RunExternalScanAsync(reserved => scan.ScanAsync(reservedGameIds: reserved));
        Assert.Equal(0, result.Matched);
        if (mediaUnavailable)
        {
            Assert.Null(await fixture.Database.Vod.GetVodAsync(2002));
            Assert.Equal("pending-match", (await fixture.Service.GetAsync(request.SessionId))!.Status);
            File.WriteAllBytes(request.FilePath, new byte[32]);
            await fixture.Service.ReconcileAsync();
        }
        Assert.Equal(request.FilePath, (await fixture.Database.Vod.GetVodAsync(2002))!.FilePath);
        Assert.True(File.Exists(video));
    }

    [Fact]
    public async Task ExternalScanHoldsRegistrationBarrierUntilItsWritesFinish()
    {
        using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var external = fixture.Service.RunExternalScanAsync(async _ =>
        {
            entered.SetResult(); await release.Task; return new(true, 0, 0, "done");
        });
        await entered.Task;
        var pending = fixture.Service.RegisterAsync(fixture.CreateRecording(1001));
        Assert.False(pending.IsCompleted);
        release.SetResult();
        await external;
        Assert.Equal("pending-match", (await pending).Status);
    }

    [Fact]
    public async Task ExistingUserVodIsNeverOverwritten()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        await fixture.Database.Vod.LinkVodAsync(1001, "existing-user-video.mp4");
        var request = fixture.CreateRecording(1001);
        Assert.Equal("retained-existing", (await fixture.Service.RegisterAsync(request)).Status);
        Assert.Equal("existing-user-video.mp4", (await fixture.Database.Vod.GetVodAsync(1001))!.FilePath);
        Assert.True(File.Exists(request.FilePath));
        Assert.False(File.Exists(request.FilePath + RecordingTimeline.Suffix));
    }

    [Fact]
    public async Task ConflictingSessionAndFileOwnershipAreRejected()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.CreateRecording(1001);
        await fixture.Service.RegisterAsync(request);
        await Assert.ThrowsAsync<RecordingConflictException>(() => fixture.Service.RegisterAsync(request with { GameId = 1002 }));
        await Assert.ThrowsAsync<RecordingConflictException>(() => fixture.Service.RegisterAsync(request with { SessionId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<RecordingConflictException>(() => fixture.Service.RegisterAsync(request with { DurationSeconds = 13 }));
        await Assert.ThrowsAsync<RecordingConflictException>(() => fixture.Service.RegisterAsync(request with { GameTimeAtVideoStart = 0 }));
    }

    [Fact]
    public async Task VideoAlreadyOwnedByDifferentMatchIsRejectedWithoutOverwriting()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        await fixture.Database.SeedGameAsync(1002);
        var request = fixture.CreateRecording(1002);
        await fixture.Database.Vod.LinkVodAsync(1001, request.FilePath);
        await Assert.ThrowsAsync<RecordingConflictException>(() => fixture.Service.RegisterAsync(request));
        Assert.Null(await fixture.Database.Vod.GetVodAsync(1002));
    }

    [Fact]
    public async Task DuplicateConcurrentRegistrationsAndCompetingVideosNeverReplaceFirstVideo()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        var first = fixture.CreateRecording(1001);
        var second = fixture.CreateRecording(1001);
        var results = await Task.WhenAll(fixture.Service.RegisterAsync(first), fixture.Service.RegisterAsync(first),
            fixture.Service.RegisterAsync(second));
        Assert.Equal(2, results.Count(result => result.Status == "linked"));
        Assert.Single(results, result => result.Status == "retained-existing");
        Assert.Equal(first.FilePath, (await fixture.Database.Vod.GetVodAsync(1001))!.FilePath);
    }

    [Fact]
    public async Task ReconciliationChecksFileAgainBeforeLinking()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.CreateRecording(1001);
        await fixture.Service.RegisterAsync(request);
        await fixture.Database.SeedGameAsync(1001);
        File.WriteAllBytes(request.FilePath, []);
        await fixture.NewService().ReconcileAsync();
        Assert.Null(await fixture.Database.Vod.GetVodAsync(1001));
        Assert.Equal("pending-match", (await fixture.Service.GetAsync(request.SessionId))!.Status);
    }

    [Fact]
    public async Task PathsOutsideRootIncludingDotTraversalAndPrefixSiblingAreRejected()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.CreateRecording(1001);
        foreach (var path in new[] { Path.Combine(fixture.Root, "..", "escape.mp4"),
            fixture.Root + "-sibling\\video.mp4", "relative.mp4", request.FilePath + ":alternate.mp4" })
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.RegisterAsync(request with { FilePath = path }));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".registrations")));
    }

    [Fact]
    public async Task MissingEmptyWrongSizeAndOpenForWritingFilesAreRejected()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.CreateRecording(1001);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RegisterAsync(request with { FileSize = 100 }));
        using (var writer = new FileStream(request.FilePath, FileMode.Open, FileAccess.Write, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => fixture.Service.RegisterAsync(request));
        File.WriteAllBytes(request.FilePath, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RegisterAsync(request));
        File.Delete(request.FilePath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Service.RegisterAsync(request));
    }

    [WindowsFact]
    public async Task HardLinkedMediaIsRejected()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.CreateRecording(1001);
        var link = Path.Combine(fixture.Root, "linked.mp4");
        Assert.True(CreateHardLink(link, request.FilePath, IntPtr.Zero));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RegisterAsync(request with { FilePath = link }));
    }

    [WindowsFact]
    public async Task DirectoryJunctionCannotEscapeRecordingOwnership()
    {
        using var fixture = await Fixture.CreateAsync();
        var outside = Directory.GetParent(fixture.Root)!.FullName;
        var outsideFile = Path.Combine(outside, "outside.mp4");
        File.WriteAllBytes(outsideFile, new byte[32]);
        var junction = Path.Combine(fixture.Root, "alias");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{junction}\" \"{outside}\"" };
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        try
        {
            var request = fixture.CreateRecording(1001) with { FilePath = Path.Combine(junction, "outside.mp4") };
            await Assert.ThrowsAsync<IOException>(() => fixture.Service.RegisterAsync(request));
            Assert.Equal(32, new FileInfo(outsideFile).Length);
        }
        finally { Directory.Delete(junction); }
    }

    [Theory]
    [InlineData("POST", "/api/recording/register")]
    [InlineData("GET", "/api/recording/session/01234567-89ab-cdef-0123-456789abcdef")]
    public void IsolatedHostDoesNotAcceptRecordingRegistrationOrInspectProductionReceipts(string method, string route) =>
        Assert.False(IsolatedHostPolicy.Allows(method, route));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Revu.Recording.Tests", Guid.NewGuid().ToString("N"), "Revu", "Recordings");
        public SidecarWriteScope Database { get; } = new();
        public RecordingRegistrationService Service { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            await fixture.Database.InitializeAsync();
            fixture.Service = fixture.NewService();
            return fixture;
        }
        public RecordingRegistrationService NewService() => new(Root, new(Database.ConnectionFactory),
            () => Task.CompletedTask, NullLogger<RecordingRegistrationService>.Instance);
        public RecordingRegistration CreateRecording(long gameId)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(Root, id + ".mp4");
            File.WriteAllBytes(path, new byte[32]);
            // Bytes are deliberately synthetic: this suite validates persistence and file
            // ownership. Encoder/container validation belongs to the native host tests.
            return new(id, gameId, path, 32, 10, DateTimeOffset.UtcNow.AddSeconds(-10), true, -30.25);
        }
        public void Dispose()
        {
            Database.Dispose();
            var testRoot = Directory.GetParent(Directory.GetParent(Root)!.FullName)!.FullName;
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
