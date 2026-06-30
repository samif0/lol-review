using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// Tests for <see cref="VodRepository.DeleteClipFullAsync"/> — the DB side of true clip
/// deletion: it must drop the clip bookmark AND any evidence ledger row that referenced
/// it, and hand back the on-disk path + share URL for the caller's file/remote cleanup.
/// </summary>
public sealed class VodRepositoryClipDeleteTests
{
    [Fact]
    public async Task DeleteClipFullAsync_RemovesBookmarkAndEvidence_AndReturnsPathAndUrl()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);

        const string clipPath = @"C:\clips\Ahri_03-10_45_20260624_120000.mp4";
        var bmId = await scope.Vod.AddBookmarkAsync(
            gameId, gameTimeSeconds: 190, note: "nice trade",
            clipStartSeconds: 160, clipEndSeconds: 205, clipPath: clipPath);
        await scope.Vod.SetBookmarkShareUrlAsync(bmId, "https://revu.lol/abc1234");

        // An evidence ledger row that points at the clip (source_kind='clip').
        var evId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: bmId,
            SourceKey: $"clip-{bmId}",
            StartTimeSeconds: 160,
            EndTimeSeconds: 205,
            Title: "nice trade"));
        Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));

        var info = await scope.Vod.DeleteClipFullAsync(bmId);

        Assert.NotNull(info);
        Assert.Equal(clipPath, info!.ClipPath);
        Assert.Equal("https://revu.lol/abc1234", info.ShareUrl);

        // Bookmark gone.
        Assert.Empty(await scope.Vod.GetBookmarksAsync(gameId));
        // Evidence tie gone (even when including dismissed).
        Assert.Empty(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
    }

    [Fact]
    public async Task DeleteClipFullAsync_ReturnsNull_ForUnknownBookmark()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var info = await scope.Vod.DeleteClipFullAsync(999999);
        Assert.Null(info);
    }

    [Fact]
    public async Task DeleteClipFullAsync_LeavesOtherClipsAndTheirEvidenceIntact()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);

        var keepBm = await scope.Vod.AddBookmarkAsync(gameId, 100, "keep",
            clipStartSeconds: 80, clipEndSeconds: 120, clipPath: @"C:\clips\keep.mp4");
        var keepEv = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            gameId, EvidenceKinds.Clip, keepBm, $"clip-{keepBm}", 80, 120, "keep"));

        var dropBm = await scope.Vod.AddBookmarkAsync(gameId, 300, "drop",
            clipStartSeconds: 280, clipEndSeconds: 320, clipPath: @"C:\clips\drop.mp4");
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            gameId, EvidenceKinds.Clip, dropBm, $"clip-{dropBm}", 280, 320, "drop"));

        await scope.Vod.DeleteClipFullAsync(dropBm);

        // Only the kept clip's bookmark + evidence remain.
        var bms = await scope.Vod.GetBookmarksAsync(gameId);
        Assert.Single(bms);
        Assert.Equal(keepBm, bms[0].Id);

        var ev = await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true);
        Assert.Single(ev);
        Assert.Equal(keepEv, ev[0].Id);
    }
}
