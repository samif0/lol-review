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
    [InlineData("BOTTOM", "Kai'Sa+Nautilus vs Tristana+Renata")]
    [InlineData("adc", "Kai'Sa+Nautilus vs Tristana+Renata")]
    [InlineData("UTILITY", "Nautilus+Kai'Sa vs Renata+Tristana")]
    [InlineData("supp", "Nautilus+Kai'Sa vs Renata+Tristana")]
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
}
