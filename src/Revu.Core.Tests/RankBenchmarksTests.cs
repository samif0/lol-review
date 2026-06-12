using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class RankBenchmarksTests
{
    [Fact]
    public void Table_IsCompleteForEveryRoleAndRank()
    {
        foreach (var role in RankBenchmarks.Roles)
        {
            foreach (var rank in RankBenchmarks.Ranks)
            {
                var entry = RankBenchmarks.Get(role, rank);
                Assert.NotNull(entry);
                Assert.True(entry!.CsPerMin > 0);
                Assert.True(entry.Deaths > 0);
                Assert.True(entry.VisionScore > 0);
                Assert.InRange(entry.KillParticipation, 1, 100);
            }
        }
    }

    [Fact]
    public void HigherRanks_HaveMonotonicallyBetterCsAndDeaths()
    {
        foreach (var role in RankBenchmarks.Roles)
        {
            RankBenchmarks.Entry? previous = null;
            foreach (var rank in RankBenchmarks.Ranks)
            {
                var entry = RankBenchmarks.Get(role, rank)!;
                if (previous is not null)
                {
                    Assert.True(entry.CsPerMin >= previous.CsPerMin,
                        $"{role} {rank}: CS/min should not decrease with rank");
                    Assert.True(entry.Deaths <= previous.Deaths,
                        $"{role} {rank}: deaths should not increase with rank");
                }
                previous = entry;
            }
        }
    }

    [Theory]
    [InlineData("MIDDLE", "MID")]
    [InlineData("BOTTOM", "ADC")]
    [InlineData("UTILITY", "SUPPORT")]
    [InlineData("JUNGLE", "JUNGLE")]
    [InlineData("TOP", "TOP")]
    [InlineData("", "")]
    [InlineData("ARAM", "")]
    public void NormalizeRole_MapsRiotPositions(string position, string expected)
    {
        Assert.Equal(expected, RankBenchmarks.NormalizeRole(position));
    }

    [Theory]
    [InlineData("GOLD", "GOLD")]
    [InlineData("gold", "GOLD")]
    [InlineData("EMERALD", "EMERALD")]
    [InlineData("MASTER", "MASTER+")]
    [InlineData("GRANDMASTER", "MASTER+")]
    [InlineData("CHALLENGER", "MASTER+")]
    [InlineData("UNRANKED", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FromRiotTier_MapsLeagueTiersToBenchmarkRanks(string? tier, string expected)
    {
        Assert.Equal(expected, RankBenchmarks.FromRiotTier(tier));
    }

    [Fact]
    public void NormalizeRank_DefaultsToGoldAndNextRankCaps()
    {
        Assert.Equal("GOLD", RankBenchmarks.NormalizeRank(""));
        Assert.Equal("GOLD", RankBenchmarks.NormalizeRank(null));
        Assert.Equal("GOLD", RankBenchmarks.NormalizeRank("challenjour"));
        Assert.Equal("DIAMOND", RankBenchmarks.NormalizeRank("diamond"));

        Assert.Equal("PLATINUM", RankBenchmarks.NextRank("GOLD"));
        Assert.Equal("MASTER+", RankBenchmarks.NextRank("MASTER+"));
    }
}
