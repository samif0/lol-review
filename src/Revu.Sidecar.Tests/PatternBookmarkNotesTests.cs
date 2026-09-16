using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class PatternBookmarkNotesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingNotesPreservesTheSavedMomentWithoutCreatingAClip(bool clip)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync(9301);
        var bookmarkId = await scope.Vod.AddBookmarkAsync(game.GameId, 100, "Original",
            clipStartSeconds: clip ? 90 : null, clipEndSeconds: clip ? 115 : null,
            clipPath: clip ? "existing-clip.mp4" : "");
        if (clip)
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(game.GameId, EvidenceKinds.Clip,
                bookmarkId, $"clip:{bookmarkId}", 90, 115, "Original", Note: "Original"));

        Assert.True(await PatternBookmarkNotes.SaveAsync(scope.Vod, scope.Evidence, game.GameId, bookmarkId, "Revised lesson"));
        var bookmark = Assert.Single(await scope.Vod.GetBookmarksAsync(game.GameId));
        Assert.Equal("Revised lesson", bookmark.Note);
        Assert.Equal(clip ? "existing-clip.mp4" : "", bookmark.ClipPath);
        Assert.Equal(clip ? 90 : (int?)null, bookmark.ClipStartSeconds);
        var evidence = await scope.Evidence.GetForGameAsync(game.GameId);
        if (clip) Assert.Equal("Revised lesson", Assert.Single(evidence).Note);
        else Assert.Empty(evidence);
    }

    [Fact]
    public async Task SavingAnotherGamesBookmarkIsRejected()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync(9301);
        var other = await scope.SeedGameAsync(9302);
        var bookmarkId = await scope.Vod.AddBookmarkAsync(game.GameId, 100, "Keep me");
        Assert.False(await PatternBookmarkNotes.SaveAsync(scope.Vod, scope.Evidence, other.GameId, bookmarkId, "Wrong game"));
        Assert.Equal("Keep me", Assert.Single(await scope.Vod.GetBookmarksAsync(game.GameId)).Note);
        Assert.Empty(await scope.Vod.GetBookmarksAsync(other.GameId));
    }
}
