using Revu.Core.Data;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// 3.14: foreign keys are off, so every delete path must remove the clip_narrations row
/// explicitly, and the clip delete hands back the narration files for cleanup.
/// </summary>
public sealed partial class VodRepositoryClipDeleteTests
{
    [Fact]
    public async Task DeleteClipFullAsync_ReturnsNarrationPaths_AndRemovesTheNarrationRow()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var narrations = new ClipNarrationRepository(scope.ConnectionFactory);
        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var bm = await scope.Vod.AddBookmarkAsync(gameId, 100, "clip", clipStartSeconds: 90, clipEndSeconds: 130,
            clipPath: @"C:\clips\a.mp4");
        await narrations.UpsertAsync(ClipNarrationRepositoryTests.Record(bm, gameId,
            audio: @"C:\Revu\Narration\x.webm", narrated: @"C:\clips\narrated\a_narrated.mp4"));

        var info = await scope.Vod.DeleteClipFullAsync(bm);

        Assert.Equal(@"C:\Revu\Narration\x.webm", info!.NarrationAudioPath);
        Assert.Equal(@"C:\clips\narrated\a_narrated.mp4", info.NarratedClipPath);
        Assert.Null(await narrations.GetAsync(bm));

        // A clip without a narration reports empty narration paths.
        var plain = await scope.Vod.AddBookmarkAsync(gameId, 300, "plain", clipPath: @"C:\clips\b.mp4");
        var plainInfo = await scope.Vod.DeleteClipFullAsync(plain);
        Assert.Equal("", plainInfo!.NarrationAudioPath);
        Assert.Equal("", plainInfo.NarratedClipPath);
    }

    [Fact]
    public async Task DeleteBookmarkAsync_AndDeleteAllBookmarksAsync_RemoveNarrationRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var narrations = new ClipNarrationRepository(scope.ConnectionFactory);
        var gameId = await SeedAsync(scope, 31_001);
        var otherGame = await SeedAsync(scope, 31_002);
        var one = await scope.Vod.AddBookmarkAsync(gameId, 100, "one", clipPath: @"C:\clips\1.mp4");
        var two = await scope.Vod.AddBookmarkAsync(gameId, 200, "two", clipPath: @"C:\clips\2.mp4");
        var three = await scope.Vod.AddBookmarkAsync(gameId, 300, "three", clipPath: @"C:\clips\3.mp4");
        var other = await scope.Vod.AddBookmarkAsync(otherGame, 50, "other", clipPath: @"C:\clips\o.mp4");
        foreach (var (id, g) in new[] { (one, gameId), (two, gameId), (three, gameId), (other, otherGame) })
            await narrations.UpsertAsync(ClipNarrationRepositoryTests.Record(id, g));

        await scope.Vod.DeleteBookmarkAsync(one);
        Assert.Null(await narrations.GetAsync(one));
        Assert.NotNull(await narrations.GetAsync(two));

        await scope.Vod.DeleteAllBookmarksAsync(gameId);
        Assert.Empty(await narrations.GetForGameAsync(gameId));
        Assert.Empty(await scope.Vod.GetBookmarksAsync(gameId));
        Assert.NotNull(await narrations.GetAsync(other));
        Assert.Empty(await narrations.DeleteOrphansAsync());
    }

    [Fact]
    public async Task GameDelete_RemovesNarrationRows_AndDeletesTheirFilesBehindTheGuards()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var narrations = new ClipNarrationRepository(scope.ConnectionFactory);
        var gameId = await SeedAsync(scope, 32_001);
        var keepGame = await SeedAsync(scope, 32_002);

        var work = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", "gamedel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "narrated"));
        Directory.CreateDirectory(AppDataPaths.NarrationDirectory);
        var clip = Path.Combine(work, "a.mp4");
        var narrated = Path.Combine(work, "narrated", "a_narrated_1.mp4");
        var audio = Path.Combine(AppDataPaths.NarrationDirectory, Guid.NewGuid().ToString("D") + ".webm");
        var outsideAudio = Path.Combine(work, "not-in-narration-dir.webm");
        foreach (var f in new[] { clip, narrated, audio, outsideAudio }) await File.WriteAllTextAsync(f, "x");

        var bm = await scope.Vod.AddBookmarkAsync(gameId, 100, "clip", clipPath: clip);
        var bm2 = await scope.Vod.AddBookmarkAsync(gameId, 200, "clip 2", clipPath: Path.Combine(work, "b.mp4"));
        var keep = await scope.Vod.AddBookmarkAsync(keepGame, 100, "keep", clipPath: Path.Combine(work, "k.mp4"));
        await narrations.UpsertAsync(ClipNarrationRepositoryTests.Record(bm, gameId, audio: audio, narrated: narrated, source: clip));
        await narrations.UpsertAsync(ClipNarrationRepositoryTests.Record(bm2, gameId, audio: outsideAudio));
        await narrations.UpsertAsync(ClipNarrationRepositoryTests.Record(keep, keepGame));

        await scope.Games.DeleteAsync(gameId);

        Assert.Empty(await narrations.GetForGameAsync(gameId));
        Assert.NotNull(await narrations.GetAsync(keep));
        Assert.False(File.Exists(clip));
        Assert.False(File.Exists(narrated));
        Assert.False(File.Exists(audio));
        // A voice-track path outside the narration folder is never a delete target.
        Assert.True(File.Exists(outsideAudio));
        Directory.Delete(work, recursive: true);
    }

    private static async Task<long> SeedAsync(TestDatabaseScope scope, long gameId)
    {
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId));
        return gameId;
    }
}
