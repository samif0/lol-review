using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// v3.10: the VOD player's AUTO lane honours "Auto-fill Timeline Inbox from game
/// events". With it off, the post-game pass's untouched anchors stay out of the
/// inbox; a noted anchor and a saved clip always show.
/// </summary>
public sealed class VodInboxAutoFillTests
{
    private static VodSnapshotBuilder Builder(SidecarWriteScope scope) => new(
        scope.Games,
        scope.Vod,
        new GameEventsRepository(scope.ConnectionFactory),
        scope.Evidence,
        scope.Objectives,
        scope.Config,
        NullLogger<VodSnapshotBuilder>.Instance);

    private static async Task<long> SeedAsync(SidecarWriteScope scope)
    {
        var game = await scope.SeedGameAsync(gameId: 7101);
        // Two untouched auto anchors (what the post-game pass writes) …
        await scope.Evidence.UpsertAsync(Anchor(game.GameId, "TRADE", 120));
        await scope.Evidence.UpsertAsync(Anchor(game.GameId, "TRADE", 480));
        // … one the user noted …
        var noted = await scope.Evidence.UpsertAsync(Anchor(game.GameId, "TRADE", 900));
        await scope.Evidence.UpdateNoteAsync(noted, "walked up without vision");
        // … and one promoted to a saved clip.
        var clipped = await scope.Evidence.UpsertAsync(Anchor(game.GameId, "TRADE", 1200));
        var bookmarkId = await scope.Vod.AddBookmarkAsync(game.GameId, 1200, "clipped", clipStartSeconds: 1190, clipEndSeconds: 1210);
        await scope.Evidence.AttachClipToEvidenceAsync(clipped, bookmarkId, 1190, 1210);
        return game.GameId;
    }

    private static EvidenceUpsert Anchor(long gameId, string token, int t) => new(
        GameId: gameId,
        SourceKind: EvidenceKinds.TimelineRegion,
        SourceId: null,
        SourceKey: PatternConstants.ObjEventSourceKey(token, t),
        StartTimeSeconds: t - PatternConstants.MomentLeadSeconds,
        EndTimeSeconds: t + PatternConstants.MomentTrailSeconds,
        Title: PatternConstants.TokenLabel(token),
        Polarity: EvidencePolarities.Neutral,
        Status: EvidenceStatuses.Evidence);

    [Fact]
    public async Task AutoFillOn_ShowsEveryAnchorInTheAutoLane()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var gameId = await SeedAsync(scope);
        scope.Config.Current.AutoTimelineClippingEnabled = true;

        var vod = await Builder(scope).BuildAsync(gameId);

        Assert.Equal(3, vod.AutoMoments.Count);
        Assert.Single(vod.SavedClips);
    }

    [Fact]
    public async Task AutoFillOff_KeepsOnlyWhatTheUserTouched()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var gameId = await SeedAsync(scope);
        scope.Config.Current.AutoTimelineClippingEnabled = false;

        var vod = await Builder(scope).BuildAsync(gameId);

        var auto = Assert.Single(vod.AutoMoments);
        Assert.Equal("walked up without vision", auto.Note);
        Assert.Single(vod.SavedClips);
        // The anchors themselves are still in the DB for the Patterns detectors.
        Assert.Equal(4, (await scope.Evidence.GetForGameAsync(gameId)).Count);
    }
}
