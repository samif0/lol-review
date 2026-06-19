using Revu.Core.Constants;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// Data-quality fix (brief 2026-06-19-10): the same champion lands in
/// games.champion_name under two spellings ("Kai'Sa" vs "Kaisa", differing only
/// by an apostrophe the LCU/EOG payloads aren't consistent about). Any
/// per-champion analytic then double-counts the most-played champion as two.
/// These tests pin the normalize-on-read aggregation that collapses the variants
/// into a single champion carrying the canonical Data Dragon display name.
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
    // Champions not in the repair map keep their (trimmed) spelling.
    [InlineData("Ahri", "Ahri")]
    [InlineData("  Sivir  ", "Sivir")]
    public void CanonicalChampionName_RepairsKnownVariants(string input, string expected)
    {
        Assert.Equal(expected, GameConstants.CanonicalChampionName(input));
    }

    /// <summary>
    /// The headline assertion the brief asks for: a per-champion view must show a
    /// SINGLE Kai'Sa row whose count is the sum of both stored spellings.
    /// </summary>
    [Fact]
    public async Task GetChampionStatsAsync_CollapsesKaiSaSpellingVariantsIntoOneChampion()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        IGameAnalyticsQuery analytics = scope.Games;

        // 3 games stored as "Kai'Sa", 2 as "Kaisa" — same champion, two spellings.
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(3001, champion: "Kai'Sa", win: true, timestamp: 3001));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(3002, champion: "Kai'Sa", win: true, timestamp: 3002));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(3003, champion: "Kai'Sa", win: false, timestamp: 3003));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(3004, champion: "Kaisa", win: true, timestamp: 3004));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(3005, champion: "Kaisa", win: false, timestamp: 3005));
        // A distinct champion stays its own row.
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(3006, champion: "Sivir", win: true, timestamp: 3006));

        var stats = await analytics.GetChampionStatsAsync();

        // Exactly one Kai'Sa row, under the canonical display spelling, no "Kaisa".
        var kaisa = Assert.Single(stats, row => GameConstants.NormalizeChampionKey(row.ChampionName) == "kaisa");
        Assert.Equal("Kai'Sa", kaisa.ChampionName);
        Assert.Equal(5, kaisa.GamesPlayed);
        Assert.Equal(3, kaisa.Wins);
        Assert.Equal(60.0, kaisa.Winrate); // 3 wins / 5 games

        // The split is gone: no bare "Kaisa" row survives the merge.
        Assert.DoesNotContain(stats, row => row.ChampionName == "Kaisa");

        // The unrelated champion is untouched.
        var sivir = Assert.Single(stats, row => row.ChampionName == "Sivir");
        Assert.Equal(1, sivir.GamesPlayed);
    }

    /// <summary>
    /// The pre-existing case-only grouping must keep working — "Ahri" and "ahri"
    /// were already collapsing via OrdinalIgnoreCase; the normalized key preserves
    /// that while also folding punctuation.
    /// </summary>
    [Fact]
    public async Task GetChampionStatsAsync_StillCollapsesCaseOnlyVariants()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        IGameAnalyticsQuery analytics = scope.Games;

        await scope.Games.SaveAsync(TestGameStatsFactory.Create(4001, champion: "Ahri", win: true, timestamp: 4001));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(4002, champion: "ahri", win: false, timestamp: 4002));

        var stats = await analytics.GetChampionStatsAsync();

        var ahri = Assert.Single(stats, row => GameConstants.NormalizeChampionKey(row.ChampionName) == "ahri");
        Assert.Equal(2, ahri.GamesPlayed);
        Assert.Equal(1, ahri.Wins);
    }
}
