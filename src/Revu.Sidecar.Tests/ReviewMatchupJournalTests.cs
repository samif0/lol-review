using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class ReviewMatchupJournalTests
{
    private static ReviewSnapshotBuilder Builder(SidecarWriteScope scope, IMatchupsRepository matchups) => new(
        scope.Games, scope.Games, scope.Objectives, scope.Prompts, scope.SessionLog,
        scope.Evidence, new GameEventsRepository(scope.ConnectionFactory),
        scope.DeathClassifications, scope.MatchupNotes, scope.ConceptTags, scope.Vod,
        scope.Config, scope.ReviewDrafts, NullLogger<ReviewSnapshotBuilder>.Instance, matchups: matchups);

    [Fact]
    public async Task OptedInReview_OffersPreparationWithoutCreatingACardOnRead()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync();
        scope.Config.Current.AutoMatchupNotesEnabled = true;
        var matchups = new MatchupsRepository(scope.ConnectionFactory);

        var snapshot = await Builder(scope, matchups).BuildAsync(game.GameId);

        Assert.True(snapshot.Subject!.MatchupJournal!.Enabled);
        Assert.Null(snapshot.Subject.MatchupJournal.Card);
        Assert.Empty(await matchups.GetAllAsync());
    }

    [Fact]
    public async Task LinkedCard_IsForSelectedGameAndRemainsAvailableWithSettingOff()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync(gameId: 4201);
        var other = await scope.SeedGameAsync(gameId: 4202);
        var matchups = new MatchupsRepository(scope.ConnectionFactory);
        var linkedId = await matchups.CreateAsync("mid", ["Ahri"], ["Syndra"],
            prior: "Respect the stun", observed: "Trade after she misses it", gameId: game.GameId);
        await matchups.CreateAsync("top", ["Garen"], ["Darius"], observed: "Another game", gameId: other.GameId);

        var snapshot = await Builder(scope, matchups).BuildAsync(game.GameId);

        Assert.False(snapshot.Subject!.MatchupJournal!.Enabled);
        var card = Assert.IsType<MatchupCardDto>(snapshot.Subject.MatchupJournal.Card);
        Assert.Equal(linkedId, card.Id);
        Assert.Equal(game.GameId, card.GameId);
        Assert.Equal("Respect the stun", card.Prior);
        Assert.Equal("Trade after she misses it", card.Observed);
        Assert.Equal(2, (await matchups.GetAllAsync()).Count);

        await matchups.UpdateNotesAsync(linkedId, prior: null, observed: "Wait for her stun, then trade");
        card = (await Builder(scope, matchups).BuildAsync(game.GameId)).Subject!.MatchupJournal!.Card!;
        Assert.Equal("Respect the stun", card.Prior);
        Assert.Equal("Wait for her stun, then trade", card.Observed);
    }

    [Fact]
    public async Task DefaultPreference_DoesNotInviteAutomaticNotePreparation()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var game = await scope.SeedGameAsync();
        var matchups = new MatchupsRepository(scope.ConnectionFactory);

        var snapshot = await Builder(scope, matchups).BuildAsync(game.GameId);

        Assert.False(snapshot.Subject!.MatchupJournal!.Enabled);
        Assert.Null(snapshot.Subject.MatchupJournal.Card);
        Assert.Empty(await matchups.GetAllAsync());
    }
}
