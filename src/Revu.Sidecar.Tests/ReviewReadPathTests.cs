using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// The review page reads through ReviewSnapshotBuilder (GET /api/review). These pin
/// the read rules the retired Core review loader used to carry: past notes for the
/// same matchup, and every active objective whatever phase it is practiced in.
/// </summary>
public sealed class ReviewReadPathTests
{
    private static ReviewSnapshotBuilder Builder(SidecarWriteScope scope) => new(
        scope.Games, scope.Games, scope.Objectives, scope.Prompts, scope.SessionLog,
        scope.Evidence, new GameEventsRepository(scope.ConnectionFactory),
        scope.DeathClassifications, scope.MatchupNotes, scope.ConceptTags, scope.Vod,
        scope.Config, scope.ReviewDrafts, NullLogger<ReviewSnapshotBuilder>.Instance);

    [Fact]
    public async Task Review_ListsPastNotesForTheSameMatchup_ButNotThisGamesOwn()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(gameId: 2001, champion: "Ahri", timestamp: 1_710_000_000);
        await scope.SeedGameAsync(gameId: 2002, champion: "Ahri", timestamp: 1_710_000_600);
        await scope.Games.UpdateEnemyLanerAsync(2001, "Syndra");
        await scope.Games.UpdateEnemyLanerAsync(2002, "Syndra");
        await scope.MatchupNotes.UpsertForGameAsync(2001, "Ahri", "Syndra", "Respect level 1 spacing");
        await scope.MatchupNotes.UpsertForGameAsync(2002, "Ahri", "Syndra", "This game's own note");

        var snapshot = await Builder(scope).BuildAsync(2002);

        var history = Assert.Single(snapshot.Subject!.MatchupHistory);
        Assert.Equal("Respect level 1 spacing", history.Note);
        Assert.True(snapshot.Subject.HasMatchupHistory);
    }

    [Fact]
    public async Task Review_ListsEveryActiveObjective_WhateverItsPhase()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.SeedGameAsync(gameId: 4101, champion: "Orianna");
        var pre = await scope.Objectives.CreateAsync("Set loading-screen plan", "prep", phase: ObjectivePhases.PreGame);
        var inGame = await scope.Objectives.CreateAsync("Punish cooldowns", "lane", phase: ObjectivePhases.InGame);
        var post = await scope.Objectives.CreateAsync("Write one review takeaway", "review", phase: ObjectivePhases.PostGame);

        var snapshot = await Builder(scope).BuildAsync(4101);

        var ids = snapshot.Subject!.Objectives.Select(o => o.Id).ToArray();
        Assert.Contains(pre, ids);
        Assert.Contains(inGame, ids);
        Assert.Contains(post, ids);
    }
}
