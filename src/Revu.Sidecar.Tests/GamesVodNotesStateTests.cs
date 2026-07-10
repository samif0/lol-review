using Revu.Core.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Contract tests for RVU-002: the VOD view must distinguish a VOD-linked game
/// that has at least one timestamped note from one whose recording sits on disk
/// but was never annotated (zero vod_bookmarks). The distinction is carried by
/// <see cref="GamesRowDto.VodStateText"/> ("VOD linked" vs "VOD linked - no
/// notes") and the <see cref="GamesRowDto.HasNotes"/> flag.
/// </summary>
public sealed class GamesVodNotesStateTests
{
    private static GamesSnapshotBuilder BuildBuilder(SidecarWriteScope scope) =>
        new GamesSnapshotBuilder(
            scope.Games,
            scope.Vod,
            scope.Objectives,
            scope.Config,
            NullLogger<GamesSnapshotBuilder>.Instance);

    // A real file on disk so GetVodPathsAsync + File.Exists mark the row hasVod.
    private static async Task<string> LinkRealVodAsync(SidecarWriteScope scope, long gameId)
    {
        var vodPath = Path.Combine(Path.GetTempPath(), $"revu-vodnotes-{Guid.NewGuid():N}.mp4");
        await File.WriteAllTextAsync(vodPath, "not really a video");
        await scope.Vod.LinkVodAsync(gameId, vodPath, fileSize: 10, durationSeconds: 1800);
        return vodPath;
    }

    [Fact]
    public async Task VodLinkedButNeverAnnotated_ReportsNoNotesState()
    {
        var scope = new SidecarWriteScope();
        using (scope)
        {
            await scope.InitializeAsync();
            var game = await scope.SeedGameAsync(gameId: 5001);
            var vodPath = await LinkRealVodAsync(scope, game.GameId);
            // No AddBookmarkAsync: the recording exists but was never annotated.

            var snapshot = await BuildBuilder(scope).BuildAsync(view: "vod");

            var row = Assert.Single(snapshot.Items, r => r.GameId == game.GameId);
            Assert.True(row.HasVod);
            Assert.False(row.HasNotes);
            Assert.Equal("VOD linked - no notes", row.VodStateText);

            File.Delete(vodPath);
        }
    }

    [Fact]
    public async Task VodLinkedWithAtLeastOneBookmark_ReportsPlainVodLinkedState()
    {
        var scope = new SidecarWriteScope();
        using (scope)
        {
            await scope.InitializeAsync();
            var game = await scope.SeedGameAsync(gameId: 5002);
            var vodPath = await LinkRealVodAsync(scope, game.GameId);
            await scope.Vod.AddBookmarkAsync(game.GameId, gameTimeSeconds: 600, note: "a note");

            var snapshot = await BuildBuilder(scope).BuildAsync(view: "vod");

            var row = Assert.Single(snapshot.Items, r => r.GameId == game.GameId);
            Assert.True(row.HasVod);
            Assert.True(row.HasNotes);
            Assert.Equal("VOD linked", row.VodStateText);

            File.Delete(vodPath);
        }
    }
}
