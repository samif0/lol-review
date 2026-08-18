using System.Text.Json;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Pins the role-aware matchup rules used by the games list, post-game review,
/// and VOD header (all share <see cref="MatchupDisplay"/>). The bot/supp slot
/// keying has regressed before (v2.17.25) and "top is a 1v1" is an explicit
/// product rule — both are locked here against the real production helper.
/// </summary>
public sealed class MatchupDisplayTests
{
    private static string FullMap() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["ownBot"] = "Kai'Sa",   ["ownSupp"] = "Nautilus", ["ownMid"] = "Ahri",   ["ownJg"] = "Lee Sin",  ["ownTop"] = "Aatrox",
        ["enemyBot"] = "Tristana", ["enemySupp"] = "Renata", ["enemyMid"] = "Syndra", ["enemyJg"] = "Graves", ["enemyTop"] = "Sett",
    });

    [Theory]
    // "Renata" is the Match-V5 id form; every emitted name is canonicalized to
    // the display form, so it renders "Renata Glasc".
    [InlineData("BOTTOM", "Kai'Sa+Nautilus vs Tristana+Renata Glasc")]
    [InlineData("adc", "Kai'Sa+Nautilus vs Tristana+Renata Glasc")]
    [InlineData("UTILITY", "Nautilus+Kai'Sa vs Renata Glasc+Tristana")]
    [InlineData("supp", "Nautilus+Kai'Sa vs Renata Glasc+Tristana")]
    [InlineData("MIDDLE", "Ahri+Lee Sin vs Syndra+Graves")]
    [InlineData("JUNGLE", "Lee Sin+Ahri vs Graves+Syndra")]
    public void Build_PairsAdjacentLanes(string role, string expected)
    {
        Assert.Equal(expected, MatchupDisplay.Build("ignored", "ignored", role, FullMap()));
    }

    [Theory]
    [InlineData("TOP")]
    [InlineData("top")]
    public void Build_TopStaysOneVsOne(string role)
    {
        // Top has no adjacent partner → falls back to the passed champ + enemy.
        Assert.Equal("Aatrox vs Sett", MatchupDisplay.Build("Aatrox", "Sett", role, FullMap()));
    }

    [Fact]
    public void Build_NoMap_FallsBackToLaneOnly()
    {
        Assert.Equal("Ahri vs Syndra", MatchupDisplay.Build("Ahri", "Syndra", "MIDDLE", ""));
        Assert.Equal("Ahri", MatchupDisplay.Build("Ahri", "", "MIDDLE", ""));
    }

    [Fact]
    public void Build_MissingPartner_DegradesToSolo()
    {
        // ADC present but no support in the map → "Kai'Sa vs Tristana" (no '+').
        var partial = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownBot"] = "Kai'Sa", ["enemyBot"] = "Tristana",
        });
        Assert.Equal("Kai'Sa vs Tristana", MatchupDisplay.Build("x", "y", "adc", partial));
    }

    [Fact]
    public void Build_BadJson_FallsBackToLaneOnly()
    {
        Assert.Equal("Ahri vs Syndra", MatchupDisplay.Build("Ahri", "Syndra", "MIDDLE", "{not json"));
    }

    [Fact]
    public void LobbyRows_FullMap_AllFiveLanesInScoreboardOrder()
    {
        var rows = MatchupDisplay.LobbyRows("BOTTOM", FullMap());

        Assert.Equal(
            ["TOP", "JG", "MID", "BOT", "SUP"],
            rows.Select(r => r.RoleLabel).ToArray());
        Assert.Equal(
            ["Aatrox vs Sett", "Lee Sin vs Graves", "Ahri vs Syndra", "Kai'Sa vs Tristana", "Nautilus vs Renata Glasc"],
            rows.Select(r => $"{r.Own} vs {r.Enemy}").ToArray());
        // Only the lane the user played is flagged.
        Assert.Equal(["BOT"], rows.Where(r => r.IsUserLane).Select(r => r.RoleLabel).ToArray());
    }

    [Theory]
    [InlineData("TOP", "TOP")]
    [InlineData("JUNGLE", "JG")]
    [InlineData("jg", "JG")]
    [InlineData("MIDDLE", "MID")]
    [InlineData("UTILITY", "SUP")]
    [InlineData("supp", "SUP")]
    public void LobbyRows_FlagsUserLane_ForEveryRoleSpelling(string role, string expectedLabel)
    {
        var rows = MatchupDisplay.LobbyRows(role, FullMap());
        Assert.Equal([expectedLabel], rows.Where(r => r.IsUserLane).Select(r => r.RoleLabel).ToArray());
    }

    [Fact]
    public void LobbyRows_PartialMap_KeepsOneSidedLanesAndOmitsEmptyOnes()
    {
        var partial = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownBot"] = "Kai'Sa", ["enemyBot"] = "Tristana", ["enemyMid"] = "Syndra",
        });

        var rows = MatchupDisplay.LobbyRows("BOTTOM", partial);

        Assert.Equal(["MID", "BOT"], rows.Select(r => r.RoleLabel).ToArray());
        // One-sided mid keeps the known side, empty string for the gap.
        Assert.Equal("", rows[0].Own);
        Assert.Equal("Syndra", rows[0].Enemy);
    }

    [Fact]
    public void LobbyRows_And_Build_CanonicalizeIdFormNamesFromBackfilledMaps()
    {
        // Maps written by the Match-V5 backfill store Riot's id-form names
        // ("LeeSin", "Kaisa", "MonkeyKing"); LCU-captured maps store display
        // names. Both must render identically.
        var idFormMap = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["ownJg"] = "LeeSin", ["enemyJg"] = "MonkeyKing",
            ["ownMid"] = "TwistedFate", ["enemyMid"] = "Syndra",
            ["ownBot"] = "Kaisa", ["enemyBot"] = "MissFortune",
        });

        var rows = MatchupDisplay.LobbyRows("BOTTOM", idFormMap);
        Assert.Equal(
            new[] { "Lee Sin vs Wukong", "Twisted Fate vs Syndra", "Kai'Sa vs Miss Fortune" },
            rows.Select(r => $"{r.Own} vs {r.Enemy}").ToArray());

        // The role-aware heading repairs the same way (mid pairs with jungle).
        Assert.Equal("Twisted Fate+Lee Sin vs Syndra+Wukong",
            MatchupDisplay.Build("x", "y", "MIDDLE", idFormMap));

        // The 1v1 fallback path repairs too.
        Assert.Equal("Kai'Sa vs Miss Fortune",
            MatchupDisplay.Build("Kaisa", "MissFortune", "TOP", ""));
    }

    [Fact]
    public void LobbyRows_NoMapOrBadJson_ReturnsEmpty()
    {
        Assert.Empty(MatchupDisplay.LobbyRows("BOTTOM", ""));
        Assert.Empty(MatchupDisplay.LobbyRows("BOTTOM", "{not json"));
        Assert.Empty(MatchupDisplay.LobbyRows("", "{}"));
    }
}
