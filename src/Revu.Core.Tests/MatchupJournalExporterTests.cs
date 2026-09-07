using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>The matchup journal's Markdown export: H2 per lane in fixed order,
/// H3 per matchup key (newest group first), Prior / Observed H4s and a date per
/// card, plus the lane / last-N filters the page's export controls send.</summary>
public sealed class MatchupJournalExporterTests
{
    // Mid-day UTC stamps so the local calendar date is stable in any zone.
    private const long T1 = 1_757_073_600; // 2025-09-05 12:00 UTC
    private const long T2 = T1 + 86_400;
    private const long T3 = T1 + 2 * 86_400;
    private const long T4 = T1 + 3 * 86_400;

    private static MatchupCard Card(long id, string lane, string ally, string enemy, long createdAt, string prior = "", string observed = "") =>
        new(id, lane, ally.Split(','), enemy.Split(','), prior, observed, null, createdAt);

    private static readonly DateTimeOffset ExportedAt = new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_GroupsByLaneThenMatchup_NewestFirst_WithTheRequiredHeadings()
    {
        var cards = new[]
        {
            Card(1, "top", "Aatrox", "Sett", T1, prior: "Short trades before 3.", observed: "He built Doran's shield; trades were even."),
            Card(2, "bot", "Kai'Sa,Nautilus", "Tristana,Renata Glasc", T2, prior: "Respect the W flip."),
            Card(3, "bot", "Kai'Sa,Nautilus", "Tristana,Renata Glasc", T4, prior: "Play for level 2 hook.", observed: "Got the hook, won lane."),
            Card(4, "bot", "Jinx,Lulu", "Draven,Thresh", T3, prior: "Never walk up alone."),
            Card(5, "mid", "Ahri", "Syndra", T1, observed: "Dodged most Qs."),
        };

        var md = MatchupJournalExporter.Build(cards, ExportedAt);

        Assert.StartsWith("# Matchup Journal", md);
        Assert.Contains("5 cards · exported 2026-09-07 10:00", md);

        // Lanes in fixed order regardless of card recency.
        var top = md.IndexOf("## Top", StringComparison.Ordinal);
        var mid = md.IndexOf("## Mid", StringComparison.Ordinal);
        var bot = md.IndexOf("## Bot", StringComparison.Ordinal);
        Assert.True(top > 0 && mid > top && bot > mid, md);
        Assert.DoesNotContain("## Jungle", md);
        Assert.DoesNotContain("## Support", md);

        // Within bot: the group with the newest card first, its cards newest first.
        var kaisa = md.IndexOf("### Kai'Sa + Nautilus vs Tristana + Renata Glasc", StringComparison.Ordinal);
        var jinx = md.IndexOf("### Jinx + Lulu vs Draven + Thresh", StringComparison.Ordinal);
        Assert.True(kaisa > bot && jinx > kaisa, md);
        var newer = md.IndexOf("Play for level 2 hook.", StringComparison.Ordinal);
        var older = md.IndexOf("Respect the W flip.", StringComparison.Ordinal);
        Assert.True(newer > kaisa && older > newer && older < jinx, md);

        // Per card: bold date, then Prior / Observed H4s; unwritten notes are marked.
        Assert.Contains($"**{MatchupJournalExporter.DateText(T4)}**", md);
        Assert.Contains("#### Prior", md);
        Assert.Contains("#### Observed", md);
        Assert.Contains("_(not written yet)_", md);
        Assert.Equal(5, CountOf(md, "#### Prior"));
        Assert.Equal(5, CountOf(md, "#### Observed"));
        Assert.Equal(3, CountOf(md, "\n## "));
        Assert.Equal(4, CountOf(md, "\n### "));
    }

    [Fact]
    public void Build_SpellingVariantsOfTheSameMatchup_ShareOneHeading()
    {
        var cards = new[]
        {
            Card(1, "bot", "Kaisa,Nautilus", "Tristana,Renata", T1),
            Card(2, "bot", "Kai'Sa,Nautilus", "Tristana,Renata Glasc", T2),
        };

        var md = MatchupJournalExporter.Build(cards, ExportedAt);

        Assert.Equal(1, CountOf(md, "\n### "));
        Assert.Contains("### Kai'Sa + Nautilus vs Tristana + Renata Glasc", md);
    }

    [Fact]
    public void Build_Empty_SaysSo()
    {
        var md = MatchupJournalExporter.Build([], ExportedAt);

        Assert.StartsWith("# Matchup Journal", md);
        Assert.Contains("_No cards._", md);
        Assert.DoesNotContain("## ", md);
    }

    [Fact]
    public void Filter_ByLane_IsCaseInsensitive_AndKeepsNewestFirst()
    {
        var cards = new[]
        {
            Card(1, "top", "Aatrox", "Sett", T1),
            Card(2, "bot", "Kai'Sa,Nautilus", "Tristana,Renata Glasc", T2),
            Card(3, "bot", "Jinx,Lulu", "Draven,Thresh", T3),
        };

        var bot = MatchupJournalExporter.Filter(cards, "BOT", null);

        Assert.Equal(new long[] { 3, 2 }, bot.Select(c => c.Id).ToArray());
        Assert.Equal(3, MatchupJournalExporter.Filter(cards, "", null).Count);
        Assert.Equal(3, MatchupJournalExporter.Filter(cards, null, 0).Count);
        Assert.Empty(MatchupJournalExporter.Filter(cards, "jungle", null));
    }

    [Fact]
    public void Filter_LastN_TakesTheNewestAfterTheLaneFilter()
    {
        var cards = new[]
        {
            Card(1, "top", "Aatrox", "Sett", T4),
            Card(2, "bot", "Kai'Sa,Nautilus", "Tristana,Renata Glasc", T1),
            Card(3, "bot", "Jinx,Lulu", "Draven,Thresh", T3),
            Card(4, "bot", "Jinx,Lulu", "Draven,Thresh", T2),
        };

        Assert.Equal(new long[] { 1, 3 }, MatchupJournalExporter.Filter(cards, null, 2).Select(c => c.Id).ToArray());
        Assert.Equal(new long[] { 3, 4 }, MatchupJournalExporter.Filter(cards, "bot", 2).Select(c => c.Id).ToArray());
        Assert.Equal(4, MatchupJournalExporter.Filter(cards, null, 99).Count);
    }

    [Fact]
    public void DateText_UsesTheLocalCalendarDate_AndIsEmptyWhenUnset()
    {
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", MatchupJournalExporter.DateText(T1));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(T1).LocalDateTime.ToString("yyyy-MM-dd"), MatchupJournalExporter.DateText(T1));
        Assert.Equal("", MatchupJournalExporter.DateText(0));
        Assert.Equal("", MatchupJournalExporter.CreatedAtText(0));
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
