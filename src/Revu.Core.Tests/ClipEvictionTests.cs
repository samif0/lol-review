using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Clips-folder eviction with the 3.14 protected set: the file just written, narration
/// sources and renders, shared clips and pinned files are never deleted; narrated renders
/// count toward the limit but are never candidates.
/// </summary>
public sealed class ClipEvictionTests : IDisposable
{
    private const long Mb = 1024 * 1024;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "evict-" + Guid.NewGuid().ToString("N"));

    public ClipEvictionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Every top-level file in the folder counts as a Revu clip unless listed in NotRevu.
    private sealed class FakeGuard(string dir, params string[] protectedPaths) : IClipRetentionGuard
    {
        public HashSet<string> Paths { get; } = new(protectedPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NotRevu { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlySet<string>> GetProtectedPathsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(Paths);
        public Task<IReadOnlySet<string>> GetRevuClipPathsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(Directory.EnumerateFiles(dir).Select(Path.GetFullPath)
                .Where(p => !NotRevu.Contains(p)).ToHashSet(StringComparer.OrdinalIgnoreCase));
        public IDisposable Pin(string path) => throw new NotSupportedException();
    }

    private FakeGuard Guard(params string[] protectedPaths) => new(_dir, protectedPaths);

    private sealed class ThrowingGuard : IClipRetentionGuard
    {
        public Task<IReadOnlySet<string>> GetProtectedPathsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("db locked");
        public Task<IReadOnlySet<string>> GetRevuClipPathsAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("db locked");
        public IDisposable Pin(string path) => throw new NotSupportedException();
    }

    private string Clip(string name, long bytes, int ageMinutes, string? subdir = null)
    {
        var dir = subdir is null ? _dir : Path.Combine(_dir, subdir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-ageMinutes));
        return path;
    }

    private static ClipService Service(IClipRetentionGuard? guard) =>
        new(new TestConfigService(), NullLogger<ClipService>.Instance, guard);

    [Fact]
    public async Task JustWrittenFile_IsNeverTheVictim()
    {
        var older = Clip("older.mp4", 2 * Mb, ageMinutes: 60);
        var fresh = Clip("fresh.mp4", 2 * Mb, ageMinutes: 0);
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddMinutes(-600)); // oldest on disk, still exempt

        await Service(Guard()).EnforceFolderSizeLimitAsync(_dir, 3 * Mb, justWritten: fresh);

        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(older));
    }

    [Fact]
    public async Task JustWrittenFile_ThatAloneExceedsTheLimit_DeletesNothing()
    {
        var older = Clip("older.mp4", 2 * Mb, ageMinutes: 60);
        var fresh = Clip("fresh.mp4", 5 * Mb, ageMinutes: 0);

        await Service(Guard()).EnforceFolderSizeLimitAsync(_dir, 3 * Mb, justWritten: fresh);

        // Deleting older could never get the folder under 3 MB, so it is kept.
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(older));
    }

    [Fact]
    public async Task SharedClipsAndRendersFillingTheLimit_NeverCostTheUnprotectedClips()
    {
        // A heavy sharer upgrading from 3.13: shared clips (protected) plus a narrated render
        // already exceed the cap. Every plain clip must survive.
        var sharedA = Clip("shared-a.mp4", 3 * Mb, ageMinutes: 900);
        var sharedB = Clip("shared-b.mp4", 2 * Mb, ageMinutes: 800);
        var render = Clip("x_narrated_1.mp4", 1 * Mb, ageMinutes: 700, subdir: ClipService.NarratedFolderName);
        var plainOld = Clip("plain-old.mp4", 1 * Mb, ageMinutes: 600);
        var plainNew = Clip("plain-new.mp4", 1 * Mb, ageMinutes: 10);

        await Service(Guard(sharedA, sharedB)).EnforceFolderSizeLimitAsync(_dir, 5 * Mb);

        Assert.True(File.Exists(sharedA));
        Assert.True(File.Exists(sharedB));
        Assert.True(File.Exists(render));
        Assert.True(File.Exists(plainOld));
        Assert.True(File.Exists(plainNew));
    }

    [Fact]
    public async Task ProtectedBytesUnderTheLimit_EvictOnlyTheUnprotectedExcess()
    {
        var shared = Clip("shared.mp4", 3 * Mb, ageMinutes: 900);
        var plainOld = Clip("plain-old.mp4", 1 * Mb, ageMinutes: 600);
        var plainMid = Clip("plain-mid.mp4", 1 * Mb, ageMinutes: 300);
        var plainNew = Clip("plain-new.mp4", 1 * Mb, ageMinutes: 10);

        await Service(Guard(shared)).EnforceFolderSizeLimitAsync(_dir, 5 * Mb);

        Assert.True(File.Exists(shared));
        Assert.False(File.Exists(plainOld));
        Assert.True(File.Exists(plainMid));
        Assert.True(File.Exists(plainNew));
    }

    [Fact]
    public async Task ProtectedSourcesAndSharedClips_AreNeverDeleted()
    {
        var narratedSource = Clip("source.mp4", 2 * Mb, ageMinutes: 300);
        var shared = Clip("shared.webm", 2 * Mb, ageMinutes: 200);
        var plain = Clip("plain.mp4", 2 * Mb, ageMinutes: 100);

        await Service(Guard(narratedSource, shared)).EnforceFolderSizeLimitAsync(_dir, 5 * Mb);

        Assert.True(File.Exists(narratedSource));
        Assert.True(File.Exists(shared));
        Assert.False(File.Exists(plain));
    }

    [Fact]
    public async Task NarratedRenders_CountTowardTheLimit_ButAreNeverDeleted()
    {
        var render = Clip("a_narrated_1.mp4", 4 * Mb, ageMinutes: 999, subdir: ClipService.NarratedFolderName);
        var oldClip = Clip("old.mp4", 1 * Mb, ageMinutes: 50);
        var newClip = Clip("new.mp4", 1 * Mb, ageMinutes: 10);

        // Top level alone (2 MB) fits in 5 MB; with the 4 MB render it does not.
        await Service(Guard()).EnforceFolderSizeLimitAsync(_dir, 5 * Mb);

        Assert.True(File.Exists(render));
        Assert.False(File.Exists(oldClip));
        Assert.True(File.Exists(newClip));
    }

    [Fact]
    public async Task OldestUnprotectedFileGoesFirst_AndEvictionStopsUnderTheLimit()
    {
        var oldest = Clip("1.mp4", 1 * Mb, ageMinutes: 400);
        var middle = Clip("2.mkv", 1 * Mb, ageMinutes: 300);
        var newer = Clip("3.mp4", 1 * Mb, ageMinutes: 200);
        var notAClip = Clip("notes.txt", 3 * Mb, ageMinutes: 999);

        await Service(Guard()).EnforceFolderSizeLimitAsync(_dir, 2 * Mb);

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(notAClip));
    }

    [Fact]
    public async Task WhenEverythingIsProtected_NothingIsDeleted()
    {
        var a = Clip("a.mp4", 2 * Mb, ageMinutes: 30);
        var b = Clip("b.mp4", 2 * Mb, ageMinutes: 20);

        await Service(Guard(a)).EnforceFolderSizeLimitAsync(_dir, 1 * Mb, justWritten: b);

        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task UnavailableProtectedSet_DeletesNothing()
    {
        var a = Clip("a.mp4", 3 * Mb, ageMinutes: 30);

        await Service(new ThrowingGuard()).EnforceFolderSizeLimitAsync(_dir, 1 * Mb);

        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task NoRetentionGuard_DeletesNothing()
    {
        var a = Clip("a.mp4", 3 * Mb, ageMinutes: 30);

        await Service(null).EnforceFolderSizeLimitAsync(_dir, 1 * Mb);

        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task AVideoRevuDidNotCreate_IsNeverDeleted_EvenWhenOldest()
    {
        // H5: the clips folder pointed at a recordings folder. The full-game recording is
        // the oldest file; only Revu's own clips may go.
        var recording = Clip("2026-07-01 ranked.mp4", 3 * Mb, ageMinutes: 9000);
        var oldClip = Clip("old-clip.mp4", 1 * Mb, ageMinutes: 600);
        var newClip = Clip("new-clip.mp4", 1 * Mb, ageMinutes: 10);
        var guard = Guard();
        guard.NotRevu.Add(recording);

        await Service(guard).EnforceFolderSizeLimitAsync(_dir, 4 * Mb);

        Assert.True(File.Exists(recording));
        Assert.False(File.Exists(oldClip));
        Assert.True(File.Exists(newClip));
    }

    [Fact]
    public async Task ForeignVideosFillingTheLimit_DeleteNothing()
    {
        var recording = Clip("recording.mkv", 5 * Mb, ageMinutes: 9000);
        var clip = Clip("clip.mp4", 1 * Mb, ageMinutes: 60);
        var guard = Guard();
        guard.NotRevu.Add(recording);

        await Service(guard).EnforceFolderSizeLimitAsync(_dir, 4 * Mb);

        Assert.True(File.Exists(recording));
        Assert.True(File.Exists(clip));
    }

    [Fact]
    public void UsageMeter_MeasuresExactlyWhatEvictionWeighs()
    {
        Clip("a.mp4", 1 * Mb, ageMinutes: 10);
        Clip("b.webm", 2 * Mb, ageMinutes: 10);
        Clip("notes.txt", 5 * Mb, ageMinutes: 10);
        Clip("r_narrated_1.mp4", 1 * Mb, ageMinutes: 10, subdir: ClipService.NarratedFolderName);
        Clip("deep.mp4", 7 * Mb, ageMinutes: 10, subdir: "nested");

        Assert.Equal(4 * Mb, ClipService.MeasureFolderBytes(_dir));
        Assert.Equal(0, ClipService.MeasureFolderBytes(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public async Task RetentionGuard_UnionsNarrationsSharesAndRefcountedPins()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var narrations = new ClipNarrationRepository(scope.ConnectionFactory);
        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var source = Path.Combine(_dir, "source.mp4");
        var render = Path.Combine(_dir, "narrated", "source_narrated_1.mp4");
        var sharedClip = Path.Combine(_dir, "shared.mp4");
        var bm = await scope.Vod.AddBookmarkAsync(gameId, 10, "n", clipPath: source);
        var shared = await scope.Vod.AddBookmarkAsync(gameId, 20, "s", clipPath: sharedClip);
        await scope.Vod.AddBookmarkAsync(gameId, 30, "plain", clipPath: Path.Combine(_dir, "plain.mp4"));
        await scope.Vod.SetBookmarkShareUrlAsync(shared, "https://revu.lol/abc1234");
        await narrations.UpsertAsync(ClipNarrationRepositoryTests.Record(bm, gameId, narrated: render, source: source));
        var guard = new ClipRetentionGuard(narrations, scope.Vod);
        var pinned = Path.Combine(_dir, "pinned.mp4");
        Assert.Contains(Path.Combine(_dir, "plain.mp4"), await guard.GetRevuClipPathsAsync());

        var first = guard.Pin(pinned);
        var second = guard.Pin(pinned.ToUpperInvariant());
        var set = await guard.GetProtectedPathsAsync();
        Assert.Contains(source, set);
        Assert.Contains(render, set);
        Assert.Contains(sharedClip.ToUpperInvariant(), set);
        Assert.Contains(pinned, set);
        Assert.DoesNotContain(Path.Combine(_dir, "plain.mp4"), set);

        first.Dispose();
        first.Dispose();
        Assert.Contains(pinned, await guard.GetProtectedPathsAsync());
        second.Dispose();
        Assert.DoesNotContain(pinned, await guard.GetProtectedPathsAsync());
    }

    [Fact]
    public async Task RetentionGuard_ProtectsKeptClipsAndLinkedRecordings_ButNotUntouchedOrAutoClips()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var narrations = new ClipNarrationRepository(scope.ConnectionFactory);
        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateWithPhasesAsync(
            "Wave control", "", "primary", "", "", practicePre: false, practiceIn: true, practicePost: false);
        string P(string name) => Path.Combine(_dir, name);

        await scope.Vod.AddBookmarkAsync(gameId, 10, "rated", clipPath: P("rated.mp4"), quality: "good");
        await scope.Vod.AddBookmarkAsync(gameId, 20, "tagged", clipPath: P("tagged.mp4"), objectiveId: objectiveId);
        await scope.Vod.AddBookmarkAsync(gameId, 30, "plain", clipPath: P("plain.mp4"));
        var saved = await scope.Vod.AddBookmarkAsync(gameId, 40, "saved", clipPath: P("saved.mp4"));
        await ClipEvidence(scope, gameId, saved, $"clip:{saved}", EvidenceStatuses.Evidence, null);
        var auto = await scope.Vod.AddBookmarkAsync(gameId, 50, "auto", clipPath: P("auto.mp4"), objectiveId: objectiveId);
        await ClipEvidence(scope, gameId, auto, $"autoclip:{gameId}:7", EvidenceStatuses.NeedsReview, objectiveId);
        await scope.Vod.LinkVodAsync(gameId, P("full-game.mp4"));

        var guard = new ClipRetentionGuard(narrations, scope.Vod);
        var kept = await guard.GetProtectedPathsAsync();
        var revu = await guard.GetRevuClipPathsAsync();

        Assert.Contains(P("rated.mp4"), kept);
        Assert.Contains(P("tagged.mp4"), kept);
        Assert.Contains(P("saved.mp4"), kept);
        Assert.Contains(P("full-game.mp4"), kept);
        Assert.DoesNotContain(P("plain.mp4"), kept);
        Assert.DoesNotContain(P("auto.mp4"), kept);
        Assert.Equal(5, revu.Count);
        Assert.DoesNotContain(P("full-game.mp4"), revu);
    }

    private static Task ClipEvidence(TestDatabaseScope scope, long gameId, long bookmarkId, string sourceKey,
        string status, long? objectiveId) =>
        scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId, SourceKind: EvidenceKinds.Clip, SourceId: bookmarkId, SourceKey: sourceKey,
            StartTimeSeconds: 0, EndTimeSeconds: 10, Title: "t", Note: "n", ObjectiveId: objectiveId,
            PromptId: null, Polarity: EvidencePolarities.Neutral, Status: status));
}
