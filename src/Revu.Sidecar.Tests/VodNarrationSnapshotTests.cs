using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// get_vod (C6): bookmarks and evidence-backed saved-clip rows (keyed by shareBookmarkId)
/// carry clipPath (only when the file is on disk) and narration (null without a row).
/// </summary>
public sealed class VodNarrationSnapshotTests : IDisposable
{
    private const long GameId = 7301;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", "vodnarr-" + Guid.NewGuid().ToString("N"));

    public VodNarrationSnapshotTests() => Directory.CreateDirectory(Path.Combine(_dir, "narrated"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static VodSnapshotBuilder Builder(SidecarWriteScope scope) => new(
        scope.Games,
        scope.Vod,
        new GameEventsRepository(scope.ConnectionFactory),
        scope.Evidence,
        scope.Objectives,
        scope.Config,
        NullLogger<VodSnapshotBuilder>.Instance,
        narrations: new ClipNarrationRepository(scope.ConnectionFactory));

    private async Task<long> SavedClipAsync(SidecarWriteScope scope, string clipPath, int start)
    {
        return await ClipPersistence.PersistAsync(scope.Vod, scope.Objectives, scope.Evidence,
            gameId: GameId, startS: start, endS: start + 30, clipPath: clipPath, note: "clip", quality: "", objectiveId: null);
    }

    [Fact]
    public async Task SavedClipRowsAndBookmarks_ExposeNarrationAndClipPath_WhenTheFilesExist()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(GameId);
        var clip = Path.Combine(_dir, "a.mp4");
        var narrated = Path.Combine(_dir, "narrated", "a_narrated.mp4");
        await File.WriteAllTextAsync(clip, "clip");
        await File.WriteAllTextAsync(narrated, "render");
        var bm = await SavedClipAsync(scope, clip, 100);
        var narrations = new ClipNarrationRepository(scope.ConnectionFactory);
        await narrations.UpsertAsync(new ClipNarrationRecord(bm, GameId, Guid.NewGuid().ToString("D"), "v.webm", narrated, clip,
            -120, 28_000, 0.8, 1.0, true, TranscriptStatuses.Pending, 0, "", "", "", "", 0, 0));
        var gen = (await narrations.GetAsync(bm))!.TranscriptGeneration;
        await narrations.TryClaimTranscriptAsync(bm, gen);
        var doc = TranscriptDocument.Normalize(new[] { new TranscriptSegment(1, 2.5, "ward first") }, "en", 30);
        await narrations.TrySetTranscriptAsync(bm, gen, TranscriptStatuses.Ready, "en", doc.ToJson(), "");

        var vod = await Builder(scope).BuildAsync(GameId);

        var row = Assert.Single(vod.SavedClips);
        Assert.Equal(bm, row.ShareBookmarkId);
        Assert.Equal(clip, row.ClipPath);
        Assert.NotNull(row.Narration);
        Assert.Equal(narrated, row.Narration!.NarratedClipPath);
        Assert.Equal(-120, row.Narration.OffsetMs);
        Assert.Equal("ready", row.Narration.TranscriptStatus);
        Assert.Equal("ward first", row.Narration.Transcript!.Segments.Single().Text);

        var bookmark = Assert.Single(vod.Bookmarks);
        Assert.Equal(clip, bookmark.ClipPath);
        Assert.Equal(narrated, bookmark.Narration!.NarratedClipPath);

        // Wire shape: camelCase, the C2 transcript nested as-is.
        var json = JsonSerializer.SerializeToElement(row, SidecarJson.CreateOptions());
        Assert.Equal(clip, json.GetProperty("clipPath").GetString());
        var narration = json.GetProperty("narration");
        Assert.Equal(bm, narration.GetProperty("bookmarkId").GetInt64());
        Assert.Equal(1, narration.GetProperty("transcript").GetProperty("version").GetInt32());
        Assert.Equal(2.5, narration.GetProperty("transcript").GetProperty("segments")[0].GetProperty("end").GetDouble());
    }

    [Fact]
    public async Task MissingFiles_GiveANullClipPath_AndAnEmptyNarratedPath()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(GameId);
        var bm = await SavedClipAsync(scope, Path.Combine(_dir, "gone.mp4"), 200);
        await new ClipNarrationRepository(scope.ConnectionFactory).UpsertAsync(new ClipNarrationRecord(bm, GameId,
            Guid.NewGuid().ToString("D"), "v.webm", Path.Combine(_dir, "narrated", "gone.mp4"), "", 0, 28_000, 0.8, 1.0, true,
            TranscriptStatuses.NeedsLogin, 0, "", "", "", "", 0, 0));

        var vod = await Builder(scope).BuildAsync(GameId);

        var row = Assert.Single(vod.SavedClips);
        Assert.Null(row.ClipPath);
        Assert.Equal("", row.Narration!.NarratedClipPath);
        Assert.Null(row.Narration.Transcript);
        Assert.Null(Assert.Single(vod.Bookmarks).ClipPath);
    }

    [Fact]
    public async Task ClipWithoutANarrationRow_HasANullNarration()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(GameId);
        var clip = Path.Combine(_dir, "plain.mp4");
        await File.WriteAllTextAsync(clip, "clip");
        await SavedClipAsync(scope, clip, 300);
        // A note bookmark (no clip) never exposes a path.
        await scope.Vod.AddBookmarkAsync(GameId, 50, "note only");

        var vod = await Builder(scope).BuildAsync(GameId);

        var row = Assert.Single(vod.SavedClips);
        Assert.Equal(clip, row.ClipPath);
        Assert.Null(row.Narration);
        Assert.All(vod.Bookmarks, b => Assert.Null(b.Narration));
        Assert.Null(vod.Bookmarks.Single(b => !b.HasClip).ClipPath);
    }
}
