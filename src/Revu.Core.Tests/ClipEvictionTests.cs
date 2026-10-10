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

    private sealed class FakeGuard(params string[] protectedPaths) : IClipRetentionGuard
    {
        public HashSet<string> Paths { get; } = new(protectedPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlySet<string>> GetProtectedPathsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(Paths);
        public IDisposable Pin(string path) => throw new NotSupportedException();
    }

    private sealed class ThrowingGuard : IClipRetentionGuard
    {
        public Task<IReadOnlySet<string>> GetProtectedPathsAsync(CancellationToken ct = default) =>
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

        await Service(new FakeGuard()).EnforceFolderSizeLimitAsync(_dir, 3 * Mb, justWritten: fresh);

        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(older));
    }

    [Fact]
    public async Task JustWrittenFile_ThatAloneExceedsTheLimit_DeletesNothing()
    {
        var older = Clip("older.mp4", 2 * Mb, ageMinutes: 60);
        var fresh = Clip("fresh.mp4", 5 * Mb, ageMinutes: 0);

        await Service(new FakeGuard()).EnforceFolderSizeLimitAsync(_dir, 3 * Mb, justWritten: fresh);

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

        await Service(new FakeGuard(sharedA, sharedB)).EnforceFolderSizeLimitAsync(_dir, 5 * Mb);

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

        await Service(new FakeGuard(shared)).EnforceFolderSizeLimitAsync(_dir, 5 * Mb);

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

        await Service(new FakeGuard(narratedSource, shared)).EnforceFolderSizeLimitAsync(_dir, 5 * Mb);

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
        await Service(new FakeGuard()).EnforceFolderSizeLimitAsync(_dir, 5 * Mb);

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

        await Service(new FakeGuard()).EnforceFolderSizeLimitAsync(_dir, 2 * Mb);

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

        await Service(new FakeGuard(a)).EnforceFolderSizeLimitAsync(_dir, 1 * Mb, justWritten: b);

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
}
