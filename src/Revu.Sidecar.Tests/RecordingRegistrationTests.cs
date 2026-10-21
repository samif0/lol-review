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
        Assert.False(snapshot.IsPartial);
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
    public async Task InterruptedSegmentsWithUnknownClockStaySeparateAndNeverBecomePrimaryVideos()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        var first = fixture.CreateRecording(1001) with { Complete = false, GameTimeAtVideoStart = null };
        var second = fixture.CreateRecording(1001) with { Complete = false, GameTimeAtVideoStart = null };
        Assert.Equal("retained-partial", (await fixture.Service.RegisterAsync(first)).Status);
        Assert.Equal("retained-partial", (await fixture.Service.RegisterAsync(second)).Status);
        await fixture.NewService().ReconcileAsync();
        Assert.Empty(await fixture.Database.Vod.GetAllVodsAsync());
        Assert.True(File.Exists(first.FilePath));
        Assert.False(File.Exists(first.FilePath + RecordingTimeline.Suffix));
        Assert.True(File.Exists(second.FilePath));
        await Assert.ThrowsAsync<RecordingConflictException>(() => fixture.Service.RegisterAsync(first with { Complete = true, GameTimeAtVideoStart = -30.25 }));
    }

    [Fact]
    public async Task AnchoredPartialLinksOnlyAnEmptyMatchAndLabelsPlaybackWithoutChangingTheSchema()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        var request = fixture.CreateRecording(1001) with { Complete = false, GameTimeAtVideoStart = 120 };
        var linked = new List<long>();
        var service = new RecordingRegistrationService(fixture.Root, new(fixture.Database.ConnectionFactory),
            () => Task.CompletedTask, NullLogger<RecordingRegistrationService>.Instance, linked.Add);
        Assert.Equal("linked-partial", (await service.RegisterAsync(request)).Status);
        Assert.Equal("linked-partial", (await fixture.NewService().RegisterAsync(request)).Status);
        Assert.Equal(1001L, Assert.Single(linked));
        Assert.Equal(request.FilePath, (await fixture.Database.Vod.GetVodAsync(1001))!.FilePath);
        Assert.True(RecordingTimeline.ReadTiming(request.FilePath, 1001)!.IsPartial);

        var db = fixture.Database;
        var builder = new VodSnapshotBuilder(db.Games, db.Vod, new GameEventsRepository(db.ConnectionFactory),
            db.Evidence, db.Objectives, db.Config, NullLogger<VodSnapshotBuilder>.Instance);
        var snapshot = await builder.BuildAsync(1001);
        Assert.True(snapshot.HasVod);
        Assert.True(snapshot.IsPartial);
        Assert.Equal(120, snapshot.GameTimeAtVideoStart);
        var later = fixture.CreateRecording(1001);
        Assert.Equal("retained-existing", (await service.RegisterAsync(later)).Status);
        Assert.Equal(request.FilePath, (await fixture.Database.Vod.GetVodAsync(1001))!.FilePath);
        Assert.False(File.Exists(later.FilePath + RecordingTimeline.Suffix));
    }

    [Fact]
    public async Task AnchoredPartialPendingReceiptReconcilesExactMatchAfterRestart()
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.CreateRecording(2002) with { Complete = false };
        Assert.Equal("pending-match", (await fixture.Service.RegisterAsync(request)).Status);
        await fixture.Database.SeedGameAsync(1001);
        await fixture.NewService().ReconcileAsync(1001);
        Assert.Empty(await fixture.Database.Vod.GetAllVodsAsync());
        await fixture.Database.SeedGameAsync(2002);
        await fixture.NewService().ReconcileAsync(2002);
        Assert.Equal("linked-partial", (await fixture.Service.GetAsync(request.SessionId))!.Status);
        Assert.Equal(request.FilePath, (await fixture.Database.Vod.GetVodAsync(2002))!.FilePath);
        Assert.True(RecordingTimeline.ReadTiming(request.FilePath, 2002)!.IsPartial);
    }

    [Fact]
    public async Task ReconnectSegmentsPendingTogetherLinkTheOneShowingMostOfTheMatch()
    {
        using var fixture = await Fixture.CreateAsync();
        // The earlier segment's receipt is listed first; coverage, not listing order, decides.
        var early = fixture.CreateRecording(3003, Guid.Parse("00000000-0000-4000-8000-000000000001"))
            with { Complete = false, GameTimeAtVideoStart = -40, DurationSeconds = 760 };
        var later = fixture.CreateRecording(3003, Guid.Parse("ffffffff-ffff-4fff-bfff-ffffffffffff"))
            with { Complete = false, GameTimeAtVideoStart = 750, DurationSeconds = 1350 };
        Assert.Equal("pending-match", (await fixture.Service.RegisterAsync(early)).Status);
        Assert.Equal("pending-match", (await fixture.Service.RegisterAsync(later)).Status);
        await fixture.Database.SeedGameAsync(3003);
        await fixture.NewService().ReconcileAsync(3003);
        Assert.Equal(later.FilePath, (await fixture.Database.Vod.GetVodAsync(3003))!.FilePath);
        Assert.Equal("linked-partial", (await fixture.Service.GetAsync(later.SessionId))!.Status);
        Assert.Equal("retained-existing", (await fixture.Service.GetAsync(early.SessionId))!.Status);
        Assert.True(File.Exists(early.FilePath));
        Assert.False(File.Exists(early.FilePath + RecordingTimeline.Suffix));
    }

    [Fact]
    public async Task LaterWiderReconnectSegmentReplacesOnlyRevusOwnShorterPartial()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(3003);
        var linked = new List<long>();
        var service = new RecordingRegistrationService(fixture.Root, new(fixture.Database.ConnectionFactory),
            () => Task.CompletedTask, NullLogger<RecordingRegistrationService>.Instance, linked.Add);
        var early = fixture.CreateRecording(3003) with { Complete = false, GameTimeAtVideoStart = -40, DurationSeconds = 760 };
        var later = fixture.CreateRecording(3003) with { Complete = false, GameTimeAtVideoStart = 750, DurationSeconds = 1350 };
        var shorter = fixture.CreateRecording(3003) with { Complete = false, GameTimeAtVideoStart = 900, DurationSeconds = 600 };
        Assert.Equal("linked-partial", (await service.RegisterAsync(early)).Status);
        Assert.Equal("linked-partial", (await service.RegisterAsync(later)).Status);
        var vod = (await fixture.Database.Vod.GetVodAsync(3003))!;
        Assert.Equal(later.FilePath, vod.FilePath);
        Assert.Equal(1350, vod.DurationSeconds);
        Assert.Equal("superseded", (await service.GetAsync(early.SessionId))!.Status);
        Assert.Equal(750, RecordingTimeline.ReadTiming(later.FilePath, 3003)!.GameTimeAtVideoStart);
        Assert.True(File.Exists(early.FilePath) && File.Exists(early.FilePath + RecordingTimeline.Suffix));
        Assert.Equal(new long[] { 3003, 3003 }, linked);
        // Exact retries stay idempotent and a narrower segment never takes the match back.
        Assert.Equal("superseded", (await service.RegisterAsync(early)).Status);
        Assert.Equal("retained-existing", (await service.RegisterAsync(shorter)).Status);
        Assert.Equal(later.FilePath, (await fixture.Database.Vod.GetVodAsync(3003))!.FilePath);
    }

    [Fact]
    public async Task WiderSegmentNeverReplacesACompleteOrUserLinkedVideo()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        await fixture.Database.SeedGameAsync(1002);
        var complete = fixture.CreateRecording(1001) with { DurationSeconds = 600 };
        Assert.Equal("linked", (await fixture.Service.RegisterAsync(complete)).Status);
        var wider = fixture.CreateRecording(1001) with { Complete = false, GameTimeAtVideoStart = 0, DurationSeconds = 1800 };
        Assert.Equal("retained-existing", (await fixture.Service.RegisterAsync(wider)).Status);
        Assert.Equal(complete.FilePath, (await fixture.Database.Vod.GetVodAsync(1001))!.FilePath);

        await fixture.Database.Vod.LinkVodAsync(1002, "existing-user-video.mp4");
        var user = fixture.CreateRecording(1002) with { Complete = false, GameTimeAtVideoStart = 0, DurationSeconds = 1800 };
        Assert.Equal("retained-existing", (await fixture.Service.RegisterAsync(user)).Status);
        Assert.Equal("existing-user-video.mp4", (await fixture.Database.Vod.GetVodAsync(1002))!.FilePath);
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
    public async Task PartialNeverReplacesExistingVodOrPublishesTimingForIt()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        await fixture.Database.Vod.LinkVodAsync(1001, "existing-user-video.mp4");
        var request = fixture.CreateRecording(1001) with { Complete = false };
        Assert.Equal("retained-existing", (await fixture.Service.RegisterAsync(request)).Status);
        Assert.Equal("existing-user-video.mp4", (await fixture.Database.Vod.GetVodAsync(1001))!.FilePath);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconciliationChecksFileAgainBeforeLinking(bool complete)
    {
        using var fixture = await Fixture.CreateAsync();
        var request = fixture.CreateRecording(1001) with { Complete = complete };
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
    [InlineData(true, "linked")]
    [InlineData(false, "linked-partial")]
    public async Task RelayoutUpdatesOnlyTheSizeOfALinkedReceiptAndItsVideoRow(bool complete, string status)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        var request = fixture.CreateRecording(1001) with { Complete = complete, GameTimeAtVideoStart = complete ? -30.25 : 120 };
        Assert.Equal(status, (await fixture.Service.RegisterAsync(request)).Status);
        var timing = File.ReadAllText(request.FilePath + RecordingTimeline.Suffix);
        File.WriteAllBytes(request.FilePath, new byte[48]); // the same path, rewritten in place

        var backups = 0;
        var result = await fixture.NewService(() => { backups++; return Task.CompletedTask; }).UpdateFileSizeAsync(request.SessionId, 48);
        Assert.Equal(status, result!.Status);
        Assert.Equal(1, backups); // the guarded backup precedes the database write, like every other write
        Assert.Equal(request.SessionId, result.SessionId);
        var vod = (await fixture.Database.Vod.GetVodAsync(1001))!;
        Assert.Equal((request.FilePath, 48L), (vod.FilePath, vod.FileSize));
        Assert.Equal(timing, File.ReadAllText(request.FilePath + RecordingTimeline.Suffix));
        // The durable receipt now describes the rewritten file: its exact details replay idempotently
        // from a fresh service, and a repeated relayout is a no-op.
        Assert.Equal(status, (await fixture.NewService().RegisterAsync(request with { FileSize = 48 })).Status);
        Assert.Equal(status, (await fixture.NewService().UpdateFileSizeAsync(request.SessionId, 48))!.Status);
        await Assert.ThrowsAsync<RecordingConflictException>(() => fixture.Service.RegisterAsync(request with { FileSize = 48, DurationSeconds = 11 }));
        Assert.Single(await fixture.Database.Vod.GetAllVodsAsync());
    }

    [Fact]
    public async Task RelayoutRejectsUnlinkedReceiptsMismatchedSizesAndUnknownSessions()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Database.SeedGameAsync(1001);
        await fixture.Database.Vod.LinkVodAsync(1001, "existing-user-video.mp4");
        var backups = 0;
        var guarded = fixture.NewService(() => { backups++; return Task.CompletedTask; });
        var pending = fixture.CreateRecording(2002);
        var retainedPartial = fixture.CreateRecording(2002) with { Complete = false, GameTimeAtVideoStart = null };
        var retainedExisting = fixture.CreateRecording(1001);
        Assert.Equal("pending-match", (await fixture.Service.RegisterAsync(pending)).Status);
        Assert.Equal("retained-partial", (await fixture.Service.RegisterAsync(retainedPartial)).Status);
        Assert.Equal("retained-existing", (await fixture.Service.RegisterAsync(retainedExisting)).Status);
        foreach (var request in new[] { pending, retainedPartial, retainedExisting })
        {
            File.WriteAllBytes(request.FilePath, new byte[48]);
            await Assert.ThrowsAsync<RecordingConflictException>(() => guarded.UpdateFileSizeAsync(request.SessionId, 48));
            File.WriteAllBytes(request.FilePath, new byte[32]);
        }
        Assert.Equal("pending-match", (await fixture.Service.GetAsync(pending.SessionId))!.Status);
        Assert.Equal("existing-user-video.mp4", (await fixture.Database.Vod.GetVodAsync(1001))!.FilePath);

        await fixture.Database.SeedGameAsync(3003);
        var linked = fixture.CreateRecording(3003);
        Assert.Equal("linked", (await fixture.Service.RegisterAsync(linked)).Status);
        // The file on disk must already have the reported size; nothing changes otherwise.
        await Assert.ThrowsAsync<InvalidDataException>(() => guarded.UpdateFileSizeAsync(linked.SessionId, 48));
        await Assert.ThrowsAsync<ArgumentException>(() => guarded.UpdateFileSizeAsync(linked.SessionId, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => guarded.UpdateFileSizeAsync(Guid.Empty, 32));
        Assert.Equal(0, backups); // a refused relayout never writes, so it needs no backup
        Assert.Equal(32, (await fixture.Database.Vod.GetVodAsync(3003))!.FileSize);
        Assert.Equal("linked", (await fixture.NewService().RegisterAsync(linked)).Status);
        Assert.Null(await fixture.Service.UpdateFileSizeAsync(Guid.NewGuid(), 32));
    }

    [Theory]
    [InlineData("POST", "/api/recording/register")]
    [InlineData("POST", "/api/recording/relayout")]
    [InlineData("POST", "/api/recording/prune")]
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
        public RecordingRegistrationService NewService(Func<Task>? backup = null) => new(Root, new(Database.ConnectionFactory),
            backup ?? (() => Task.CompletedTask), NullLogger<RecordingRegistrationService>.Instance);
        public RecordingRegistration CreateRecording(long gameId, Guid? sessionId = null)
        {
            var id = sessionId ?? Guid.NewGuid();
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
