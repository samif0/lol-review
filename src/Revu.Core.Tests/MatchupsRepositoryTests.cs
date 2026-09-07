using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>v3.9 (schema v15): the matchup journal's persistence contract —
/// JSON champion lists round-trip canonicalized, validation is the journal's
/// lane convention (1v1 top/mid, 2v2 elsewhere), inline note edits leave the
/// other note alone, and deleting a game detaches its card instead of dropping it.</summary>
public sealed class MatchupsRepositoryTests
{
    // games.game_id is a real FOREIGN KEY target on this DB, so a linked card
    // needs its game on record first — exactly as production only ever links
    // the card to a game it just read.
    private static async Task SeedGameAsync(TestDatabaseScope scope, long gameId, string champion = "Ahri") =>
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: champion));

    [Fact]
    public async Task Create_RoundTrips_CanonicalizesNamesAndTrimsNotes()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedGameAsync(scope, 5001, champion: "Kai'Sa");

        var id = await scope.Matchups.CreateAsync(
            " Bot ",
            ["Kaisa", " Nautilus "],
            ["Tristana", "Renata"],
            prior: "  Respect the W flip. ",
            observed: null,
            gameId: 5001,
            createdAt: 1_800_000_000);

        var card = await scope.Matchups.GetAsync(id);

        Assert.NotNull(card);
        Assert.Equal(id, card!.Id);
        Assert.Equal("bot", card.Lane);
        Assert.Equal(new[] { "Kai'Sa", "Nautilus" }, card.AllyChamps);
        Assert.Equal(new[] { "Tristana", "Renata Glasc" }, card.EnemyChamps);
        Assert.Equal("Respect the W flip.", card.Prior);
        Assert.Equal("", card.Observed);
        Assert.Equal(5001, card.GameId);
        Assert.Equal(1_800_000_000, card.CreatedAt);
    }

    [Fact]
    public async Task Create_WithoutGame_StoresNullGameId_AndStampsNow()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var id = await scope.Matchups.CreateAsync("top", ["Aatrox"], ["Sett"]);
        var card = await scope.Matchups.GetAsync(id);

        Assert.NotNull(card);
        Assert.Null(card!.GameId);
        Assert.InRange(card.CreatedAt, before, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData("", "Aatrox", "Sett")]                 // no lane
    [InlineData("toplane", "Aatrox", "Sett")]          // not a lane
    [InlineData("top", "", "Sett")]                    // no ally champion
    [InlineData("top", "Aatrox", "  ")]                // blank enemy champion
    [InlineData("top", "Aatrox,Garen", "Sett")]        // 1v1 lane with two allies
    [InlineData("mid", "Ahri", "Syndra,Graves")]       // 1v1 lane with two enemies
    [InlineData("bot", "Kai'Sa,Nautilus,Ahri", "Tristana")] // more than two on a 2v2 lane
    public async Task Create_RejectsInvalidInput_AndWritesNothing(string lane, string ally, string enemy)
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Matchups.CreateAsync(lane, ally.Split(','), enemy.Split(',')));

        Assert.Empty(await scope.Matchups.GetAllAsync());
    }

    [Fact]
    public async Task Create_RejectsOverlongNote_AndChampionName()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var longNote = new string('x', MatchupsRepository.MaxNoteLength + 1);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Matchups.CreateAsync("top", ["Aatrox"], ["Sett"], prior: longNote));

        var longName = new string('a', MatchupsRepository.MaxChampionNameLength + 1);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Matchups.CreateAsync("top", [longName], ["Sett"]));

        Assert.Empty(await scope.Matchups.GetAllAsync());
    }

    [Fact]
    public async Task GetAll_IsNewestFirst()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var oldest = await scope.Matchups.CreateAsync("top", ["Aatrox"], ["Sett"], createdAt: 100);
        var newest = await scope.Matchups.CreateAsync("mid", ["Ahri"], ["Syndra"], createdAt: 300);
        var middle = await scope.Matchups.CreateAsync("bot", ["Kai'Sa"], ["Tristana"], createdAt: 200);

        var all = await scope.Matchups.GetAllAsync();

        Assert.Equal(new[] { newest, middle, oldest }, all.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task UpdateNotes_NullLeavesTheOtherNoteUnchanged()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var id = await scope.Matchups.CreateAsync("jungle", ["Lee Sin", "Ahri"], ["Graves", "Syndra"], prior: "Invade at 3:15.");

        Assert.True(await scope.Matchups.UpdateNotesAsync(id, prior: null, observed: "  They invaded first. "));
        var afterObserved = await scope.Matchups.GetAsync(id);
        Assert.Equal("Invade at 3:15.", afterObserved!.Prior);
        Assert.Equal("They invaded first.", afterObserved.Observed);

        Assert.True(await scope.Matchups.UpdateNotesAsync(id, prior: "Ward their raptors.", observed: null));
        var afterPrior = await scope.Matchups.GetAsync(id);
        Assert.Equal("Ward their raptors.", afterPrior!.Prior);
        Assert.Equal("They invaded first.", afterPrior.Observed);

        // Both null: nothing changes, but the card is still reported present.
        Assert.True(await scope.Matchups.UpdateNotesAsync(id, null, null));
        Assert.False(await scope.Matchups.UpdateNotesAsync(id + 1000, "x", null));
    }

    [Fact]
    public async Task Update_ReplacesLaneAndChampions_AndEnforcesTheLaneConvention()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedGameAsync(scope, 42, champion: "Kai'Sa");
        var id = await scope.Matchups.CreateAsync("bot", ["Kai'Sa", "Nautilus"], ["Tristana", "Renata Glasc"], gameId: 42);

        Assert.True(await scope.Matchups.UpdateAsync(id, "top", ["Aatrox"], ["Sett"], "Short trades.", "Held."));
        var card = await scope.Matchups.GetAsync(id);
        Assert.Equal("top", card!.Lane);
        Assert.Equal(new[] { "Aatrox" }, card.AllyChamps);
        Assert.Equal(new[] { "Sett" }, card.EnemyChamps);
        Assert.Equal("Short trades.", card.Prior);
        Assert.Equal("Held.", card.Observed);
        Assert.Equal(42, card.GameId); // the game link is not part of an edit

        // A 1v1 lane cannot carry the old 2v2 lists.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scope.Matchups.UpdateAsync(id, "top", ["Aatrox", "Garen"], ["Sett"], "", ""));
        Assert.Equal(new[] { "Aatrox" }, (await scope.Matchups.GetAsync(id))!.AllyChamps);

        Assert.False(await scope.Matchups.UpdateAsync(id + 1000, "top", ["Aatrox"], ["Sett"], "", ""));
    }

    [Fact]
    public async Task Delete_RemovesTheCard()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var id = await scope.Matchups.CreateAsync("mid", ["Ahri"], ["Syndra"]);

        Assert.True(await scope.Matchups.DeleteAsync(id));
        Assert.Null(await scope.Matchups.GetAsync(id));
        Assert.False(await scope.Matchups.DeleteAsync(id));
    }

    [Fact]
    public async Task GetForGame_ReturnsTheNewestLinkedCard()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedGameAsync(scope, 77);
        await SeedGameAsync(scope, 78);
        await scope.Matchups.CreateAsync("mid", ["Ahri"], ["Syndra"], gameId: 77, createdAt: 100);
        var newer = await scope.Matchups.CreateAsync("mid", ["Ahri"], ["Syndra"], gameId: 77, createdAt: 200);
        await scope.Matchups.CreateAsync("mid", ["Ahri"], ["Zed"], gameId: 78, createdAt: 300);

        var card = await scope.Matchups.GetForGameAsync(77);

        Assert.NotNull(card);
        Assert.Equal(newer, card!.Id);
        Assert.Null(await scope.Matchups.GetForGameAsync(79));
    }

    [Fact]
    public async Task DeletingTheGame_DetachesTheCard_InsteadOfDroppingIt()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        const long gameId = 7001;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: "Ahri"));
        var id = await scope.Matchups.CreateAsync("mid", ["Ahri"], ["Syndra"], prior: "Dodge E with W.", gameId: gameId);

        await scope.Games.DeleteAsync(gameId);

        var card = await scope.Matchups.GetAsync(id);
        Assert.NotNull(card);
        Assert.Null(card!.GameId);
        Assert.Equal("Dodge E with W.", card.Prior);
        Assert.Null(await scope.Games.GetAsync(gameId));
    }
}
