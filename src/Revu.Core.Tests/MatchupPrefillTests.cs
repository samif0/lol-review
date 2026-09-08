using System.Text.Json;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// "New card from last game": the lane + champion lists derived from a game's
/// participant map follow the JOURNAL's convention (top/mid 1v1, jungle =
/// jungler + mid, bot/support = adc + support) — not MatchupDisplay's — and
/// degrade to the game row's own champion / enemy laner when the map is thin.
/// </summary>
public sealed class MatchupPrefillTests
{
    private static string FullMap() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["ownTop"] = "Aatrox", ["ownJg"] = "Lee Sin", ["ownMid"] = "Ahri", ["ownBot"] = "Kai'Sa", ["ownSupp"] = "Nautilus",
        ["enemyTop"] = "Sett", ["enemyJg"] = "Graves", ["enemyMid"] = "Syndra", ["enemyBot"] = "Tristana", ["enemySupp"] = "Renata",
    });

    private static GameStats Game(string position, string? map = null, string champion = "Kai'Sa", string enemyLaner = "")
    {
        var game = TestGameStatsFactory.Create(9001, champion: champion);
        game.Position = position;
        game.ParticipantMap = map ?? "";
        game.EnemyLaner = enemyLaner;
        return game;
    }

    [Theory]
    [InlineData("BOTTOM", "bot", "Kai'Sa,Nautilus", "Tristana,Renata Glasc")]
    [InlineData("adc", "bot", "Kai'Sa,Nautilus", "Tristana,Renata Glasc")]
    // Support is the same adc + support pairing, adc listed first on both sides.
    [InlineData("UTILITY", "support", "Kai'Sa,Nautilus", "Tristana,Renata Glasc")]
    [InlineData("supp", "support", "Kai'Sa,Nautilus", "Tristana,Renata Glasc")]
    // Jungle pairs with mid (jungler first).
    [InlineData("JUNGLE", "jungle", "Lee Sin,Ahri", "Graves,Syndra")]
    [InlineData("jg", "jungle", "Lee Sin,Ahri", "Graves,Syndra")]
    // Top and mid are 1v1 — mid does NOT pull the jungler like MatchupDisplay does.
    [InlineData("TOP", "top", "Aatrox", "Sett")]
    [InlineData("MIDDLE", "mid", "Ahri", "Syndra")]
    [InlineData("mid", "mid", "Ahri", "Syndra")]
    public void FromGame_FullMap_FollowsTheJournalPairing(string position, string lane, string ally, string enemy)
    {
        var result = MatchupPrefill.FromGame(Game(position, FullMap()));

        Assert.NotNull(result);
        Assert.Equal(lane, result!.Lane);
        Assert.Equal(ally.Split(','), result.AllyChamps);
        Assert.Equal(enemy.Split(','), result.EnemyChamps);
    }

    [Fact]
    public void FromGame_CanonicalizesIdFormNamesFromBackfilledMaps()
    {
        var map = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownJg"] = "LeeSin", ["ownMid"] = "TwistedFate", ["enemyJg"] = "MonkeyKing", ["enemyMid"] = "Kaisa",
        });

        var result = MatchupPrefill.FromGame(Game("JUNGLE", map, champion: "LeeSin"));

        Assert.NotNull(result);
        Assert.Equal(new[] { "Lee Sin", "Twisted Fate" }, result!.AllyChamps);
        Assert.Equal(new[] { "Wukong", "Kai'Sa" }, result.EnemyChamps);
        Assert.Equal("Lee Sin + Twisted Fate vs Wukong + Kai'Sa", result.Title);
    }

    [Fact]
    public void FromGame_NoMap_FallsBackToChampionAndEnemyLaner()
    {
        var result = MatchupPrefill.FromGame(Game("MIDDLE", map: null, champion: "Ahri", enemyLaner: "Syndra"));

        Assert.NotNull(result);
        Assert.Equal("mid", result!.Lane);
        Assert.Equal(new[] { "Ahri" }, result.AllyChamps);
        Assert.Equal(new[] { "Syndra" }, result.EnemyChamps);
    }

    [Fact]
    public void FromGame_NoMapAndNoEnemyLaner_IsPartial_LaneAndOwnSideOnly()
    {
        var result = MatchupPrefill.FromGame(Game("MIDDLE", map: null, champion: "Ahri", enemyLaner: ""));

        Assert.NotNull(result);
        Assert.False(result!.IsComplete);
        Assert.Equal("mid", result.Lane);
        Assert.Equal(new[] { "Ahri" }, result.AllyChamps);
        Assert.Empty(result.EnemyChamps);
        Assert.Equal("Ahri vs ?", result.Title);
    }

    /// <summary>The user's report: a bot-lane game recovered from the client's
    /// match history by a pre-3.9.2 build — position BOTTOM, a map with only the
    /// solo lanes, no enemy laner. Lane + own side pre-fill; the enemy side is
    /// left for the Match-V5 lookup or the form.</summary>
    [Fact]
    public void FromGame_RecoveredBotGameWithoutBotKeys_IsPartial()
    {
        var map = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownTop"] = "Aatrox", ["ownJg"] = "Lee Sin", ["ownMid"] = "Ahri",
            ["enemyTop"] = "Sett", ["enemyJg"] = "Graves", ["enemyMid"] = "Syndra",
        });

        var result = MatchupPrefill.FromGame(Game("BOTTOM", map, champion: "Miss Fortune", enemyLaner: ""));

        Assert.NotNull(result);
        Assert.False(result!.IsComplete);
        Assert.Equal("bot", result.Lane);
        Assert.Equal(new[] { "Miss Fortune" }, result.AllyChamps);
        Assert.Empty(result.EnemyChamps);
    }

    [Theory]
    [InlineData("adc", "bot")]
    [InlineData("supp", "support")]
    [InlineData("jg", "jungle")]
    [InlineData("TOP", "top")]
    public void FromGame_NoPositionAnywhere_UsesTheFallbackRole(string fallback, string lane)
    {
        var result = MatchupPrefill.FromGame(Game("", map: null, champion: "Kai'Sa", enemyLaner: "Tristana"), fallback);

        Assert.NotNull(result);
        Assert.Equal(lane, result!.Lane);
        Assert.Equal(new[] { "Kai'Sa" }, result.AllyChamps);
        Assert.Equal(new[] { "Tristana" }, result.EnemyChamps);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void FromGame_RowPositionBeatsTheFallbackRole()
    {
        var result = MatchupPrefill.FromGame(Game("MIDDLE", FullMap(), champion: "Ahri"), fallbackPosition: "adc");

        Assert.Equal("mid", result!.Lane);
    }

    [Fact]
    public void FromGame_NoPosition_InfersTheLaneFromTheOwnSlotHoldingThePlayedChampion()
    {
        var result = MatchupPrefill.FromGame(Game("", FullMap(), champion: "Ahri"));

        Assert.NotNull(result);
        Assert.Equal("mid", result!.Lane);
        Assert.Equal(new[] { "Ahri" }, result.AllyChamps);
        Assert.Equal(new[] { "Syndra" }, result.EnemyChamps);
    }

    [Fact]
    public void FromGame_NoLaneAnywhere_IsUnavailable()
    {
        Assert.Null(MatchupPrefill.FromGame(Game("", map: null, champion: "Ahri", enemyLaner: "Syndra")));
        Assert.Null(MatchupPrefill.FromGame(Game("", map: null, champion: "Ahri", enemyLaner: "Syndra"), fallbackPosition: ""));
        Assert.Null(MatchupPrefill.FromGame(Game("", map: null, champion: "Ahri", enemyLaner: "Syndra"), fallbackPosition: "fill"));
        Assert.Null(MatchupPrefill.FromGame(null));
    }

    [Fact]
    public void FromGame_PartialMap_FillsTheMissingOwnSlotFromTheGameRow()
    {
        // The player's own ADC slot is missing from the map; champion_name fills
        // the primary slot ahead of the support partner the map does carry.
        var map = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownSupp"] = "Nautilus", ["enemyBot"] = "Tristana", ["enemySupp"] = "Renata",
        });

        var result = MatchupPrefill.FromGame(Game("BOTTOM", map, champion: "Kai'Sa"));

        Assert.NotNull(result);
        Assert.Equal(new[] { "Kai'Sa", "Nautilus" }, result!.AllyChamps);
        Assert.Equal(new[] { "Tristana", "Renata Glasc" }, result.EnemyChamps);
    }

    [Fact]
    public void FromGame_SupportWithMissingOwnSlot_KeepsAdcFirst()
    {
        var map = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownBot"] = "Kai'Sa", ["enemyBot"] = "Tristana",
        });

        var result = MatchupPrefill.FromGame(Game("UTILITY", map, champion: "Nautilus", enemyLaner: "Renata Glasc"));

        Assert.NotNull(result);
        Assert.Equal("support", result!.Lane);
        Assert.Equal(new[] { "Kai'Sa", "Nautilus" }, result.AllyChamps);
        // enemy_laner is the support's direct opponent → second (support) slot.
        Assert.Equal(new[] { "Tristana", "Renata Glasc" }, result.EnemyChamps);
    }

    [Fact]
    public void FromGame_FallbackNeverDuplicatesAChampionAlreadyInTheMap()
    {
        var map = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownBot"] = "Kaisa", ["enemyBot"] = "Tristana",
        });

        var result = MatchupPrefill.FromGame(Game("BOTTOM", map, champion: "Kai'Sa", enemyLaner: "Tristana"));

        Assert.NotNull(result);
        Assert.Equal(new[] { "Kai'Sa" }, result!.AllyChamps);
        Assert.Equal(new[] { "Tristana" }, result.EnemyChamps);
    }

    [Fact]
    public void FromGame_BadMapJson_FallsBackToTheGameRow()
    {
        var result = MatchupPrefill.FromGame(Game("TOP", "{not json", champion: "Aatrox", enemyLaner: "Sett"));

        Assert.NotNull(result);
        Assert.Equal("top", result!.Lane);
        Assert.Equal(new[] { "Aatrox" }, result.AllyChamps);
        Assert.Equal(new[] { "Sett" }, result.EnemyChamps);
    }
}
