using Revu.Core.Constants;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// Data-quality fix (brief 2026-06-19-10): the same champion lands in
/// games.champion_name under two spellings ("Kai'Sa" vs "Kaisa", differing only
/// by an apostrophe the LCU/EOG payloads aren't consistent about). Any
/// per-champion analytic then double-counts the most-played champion as two.
/// These tests pin the normalization key and canonical Data Dragon display name
/// that every per-champion read uses to collapse the variants.
/// </summary>
public sealed class ChampionNameNormalizationTests
{
    [Theory]
    [InlineData("Kai'Sa", "kaisa")]
    [InlineData("Kaisa", "kaisa")]
    [InlineData("KAISA", "kaisa")]
    [InlineData("kai'sa", "kaisa")]
    [InlineData("Cho'Gath", "chogath")]
    [InlineData("Nunu & Willump", "nunuwillump")]
    [InlineData("Dr. Mundo", "drmundo")]
    [InlineData("  Kai'Sa  ", "kaisa")]
    public void NormalizeChampionKey_CollapsesPunctuationAndCase(string input, string expectedKey)
    {
        Assert.Equal(expectedKey, GameConstants.NormalizeChampionKey(input));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizeChampionKey_BlankInputReturnsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, GameConstants.NormalizeChampionKey(input));
    }

    [Theory]
    // Both spellings of the same champion resolve to the Data Dragon display name.
    [InlineData("Kaisa", "Kai'Sa")]
    [InlineData("Kai'Sa", "Kai'Sa")]
    [InlineData("KHAZIX", "Kha'Zix")]
    // Space-joined Match-V5 id forms (what the backfill writes into the
    // participant map) resolve to the spaced display name.
    [InlineData("LeeSin", "Lee Sin")]
    [InlineData("Lee Sin", "Lee Sin")]
    [InlineData("TwistedFate", "Twisted Fate")]
    [InlineData("JarvanIV", "Jarvan IV")]
    [InlineData("MissFortune", "Miss Fortune")]
    [InlineData("AurelionSol", "Aurelion Sol")]
    [InlineData("KSante", "K'Sante")]
    // Renamed / shortened ids resolve to the in-game display name.
    [InlineData("MonkeyKing", "Wukong")]
    [InlineData("Wukong", "Wukong")]
    [InlineData("Nunu", "Nunu & Willump")]
    [InlineData("Renata", "Renata Glasc")]
    [InlineData("RenataGlasc", "Renata Glasc")]
    // Champions not in the repair map keep their (trimmed) spelling.
    [InlineData("Ahri", "Ahri")]
    [InlineData("  Sivir  ", "Sivir")]
    public void CanonicalChampionName_RepairsKnownVariants(string input, string expected)
    {
        Assert.Equal(expected, GameConstants.CanonicalChampionName(input));
    }
}
