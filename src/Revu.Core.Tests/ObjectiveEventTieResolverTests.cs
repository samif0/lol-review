using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Pure unit tests for the shared <see cref="ObjectiveEventTieResolver"/> — the
/// single source of truth for which active objectives a timeline event ties to,
/// used by both the VOD timeline and the on-demand auto-clipper.
/// </summary>
public sealed class ObjectiveEventTieResolverTests
{
    private static GameEvent Ev(int id, string type, int t, string details = "{}") =>
        new() { Id = id, EventType = type, GameTimeS = t, Details = details };

    [Fact]
    public void TokenMatch_TiesEventToObjectiveTrackingThatToken()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("DEATH", 7L, "Review every death") });
        var events = new[] { Ev(1, "DEATH", 600), Ev(2, "KILL", 700) };

        var ties = resolver.ResolveForGame(events);

        Assert.Single(ties[1]);            // the death ties to objective 7
        Assert.Equal(7L, ties[1][0].ObjectiveId);
        Assert.Empty(ties[2]);             // the kill is untracked
    }

    [Fact]
    public void PerSpellToken_ParsedFromDetails()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("SPELL_FLASH", 3L, "Flash usage") });
        var flash = Ev(1, "SUMMONER_SPELL", 500, "{\"spell\":\"Flash\"}");
        var ignite = Ev(2, "SUMMONER_SPELL", 510, "{\"spell\":\"Ignite\"}");

        var ties = resolver.ResolveForGame(new[] { flash, ignite });

        Assert.Single(ties[1]);            // Flash matches SPELL_FLASH
        Assert.Empty(ties[2]);             // Ignite does not
    }

    [Fact]
    public void TeamfightMembership_TiesClusterMembers()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("TEAMFIGHT", 9L, "Win fights") });
        // Three combat events within 14s → a teamfight cluster.
        var events = new[]
        {
            Ev(1, "KILL", 600),
            Ev(2, "DEATH", 605),
            Ev(3, "ASSIST", 612),
            Ev(4, "KILL", 1200), // lone, far away → not a cluster
        };

        var ties = resolver.ResolveForGame(events);

        Assert.Equal(9L, ties[1][0].ObjectiveId);
        Assert.Equal(9L, ties[2][0].ObjectiveId);
        Assert.Equal(9L, ties[3][0].ObjectiveId);
        Assert.Empty(ties[4]);             // isolated kill, no cluster
    }

    [Fact]
    public void UntiedEvents_ReturnEmpty_WhenNoActiveObjectives()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(System.Array.Empty<(string, long, string)>());
        Assert.True(resolver.IsEmpty);

        var ties = resolver.ResolveForGame(new[] { Ev(1, "DEATH", 600) });
        Assert.Empty(ties[1]);
    }

    [Fact]
    public void EventToken_DerivesRawTypeAndSpell()
    {
        Assert.Equal("DEATH", ObjectiveEventTieResolver.EventToken(Ev(1, "DEATH", 1)));
        Assert.Equal("SPELL_SMITE", ObjectiveEventTieResolver.EventToken(Ev(2, "SUMMONER_SPELL", 1, "{\"spell\":\"Smite\"}")));
        // Legacy FLASH row without Details.spell still maps to its spell token.
        Assert.Equal("SPELL_FLASH", ObjectiveEventTieResolver.EventToken(Ev(3, "FLASH", 1)));
    }

    [Fact]
    public void EventTokens_Trade_YieldsKindSpecificThenGeneric()
    {
        // A short trade matches SHORT_TRADE (priority) AND the generic TRADE.
        Assert.Equal(
            new[] { "SHORT_TRADE", "TRADE" },
            ObjectiveEventTieResolver.EventTokens(Ev(1, "TRADE", 1, "{\"kind\":\"short\"}")).ToArray());
        // An extended trade matches EXTENDED_TRADE AND the generic TRADE.
        Assert.Equal(
            new[] { "EXTENDED_TRADE", "TRADE" },
            ObjectiveEventTieResolver.EventTokens(Ev(2, "TRADE", 1, "{\"kind\":\"extended\"}")).ToArray());
        // A trade with no/unknown kind still matches the generic TRADE.
        Assert.Equal(
            new[] { "TRADE" },
            ObjectiveEventTieResolver.EventTokens(Ev(3, "TRADE", 1)).ToArray());
    }

    [Fact]
    public void EventTokens_JungleGankedDeath_YieldsGankThenDeath()
    {
        // A DEATH flagged jungle_gank matches the specific JUNGLE_GANK token (first) AND
        // the plain DEATH token.
        Assert.Equal(
            new[] { "JUNGLE_GANK", "DEATH" },
            ObjectiveEventTieResolver.EventTokens(Ev(1, "DEATH", 300, "{\"jungle_gank\":true}")).ToArray());
        // A plain death matches only DEATH.
        Assert.Equal(
            new[] { "DEATH" },
            ObjectiveEventTieResolver.EventTokens(Ev(2, "DEATH", 300, "{\"killer\":\"X\"}")).ToArray());
    }

    [Fact]
    public void JungleGankToken_TiesGenericDeathAndGankObjectives()
    {
        // One objective tracks any DEATH, another only JUNGLE_GANK.
        var resolver = ObjectiveEventTieResolver.FromTies(new[]
        {
            ("DEATH", 1L, "Review every death"),
            ("JUNGLE_GANK", 2L, "Stop dying to ganks"),
        });
        var plainDeath = Ev(1, "DEATH", 300, "{\"killer\":\"X\"}");
        var gankDeath = Ev(2, "DEATH", 320, "{\"jungle_gank\":true}");

        var ties = resolver.ResolveForGame(new[] { plainDeath, gankDeath });

        // Plain death → only the death objective.
        Assert.Single(ties[1]);
        Assert.Equal(1L, ties[1][0].ObjectiveId);
        // Gank death → BOTH (death + gank), de-duped per objective.
        Assert.Equal(2, ties[2].Count);
        Assert.Contains(ties[2], t => t.ObjectiveId == 1L);
        Assert.Contains(ties[2], t => t.ObjectiveId == 2L);
    }

    [Fact]
    public void TradeKindToken_TiesGenericAndSpecificObjectives()
    {
        // One objective tracks any TRADE, another only EXTENDED_TRADE.
        var resolver = ObjectiveEventTieResolver.FromTies(new[]
        {
            ("TRADE", 1L, "Track all trades"),
            ("EXTENDED_TRADE", 2L, "Stop dragging out trades"),
        });
        var shortTrade = Ev(1, "TRADE", 300, "{\"kind\":\"short\"}");
        var extTrade = Ev(2, "TRADE", 600, "{\"kind\":\"extended\"}");

        var ties = resolver.ResolveForGame(new[] { shortTrade, extTrade });

        // Short trade → only the generic-trade objective.
        Assert.Single(ties[1]);
        Assert.Equal(1L, ties[1][0].ObjectiveId);
        // Extended trade → BOTH (generic + extended-specific), de-duped per objective.
        Assert.Equal(2, ties[2].Count);
        Assert.Contains(ties[2], t => t.ObjectiveId == 1L);
        Assert.Contains(ties[2], t => t.ObjectiveId == 2L);
    }

    [Fact]
    public void ResolveTeamfightClusters_GroupsCombatIntoFights_WithSpanAndMembers()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("TEAMFIGHT", 9L, "Win fights") });
        var events = new[]
        {
            Ev(1, "KILL", 600),
            Ev(2, "DEATH", 606),
            Ev(3, "ASSIST", 612),   // fight A: 600..612, 3 members
            Ev(4, "KILL", 1200),    // lone, far → no cluster
        };

        var clusters = resolver.ResolveTeamfightClusters(events);

        var c = Assert.Single(clusters);
        Assert.Equal(600, c.StartS);
        Assert.Equal(612, c.EndS);
        Assert.Equal(new[] { 1, 2, 3 }, c.MemberEventIds.OrderBy(x => x).ToArray());
        Assert.Contains(c.Objectives, t => t.ObjectiveId == 9L);
    }

    [Fact]
    public void ResolveTeamfightClusters_Empty_WhenNoObjectiveTracksTeamfight()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("DEATH", 1L, "Deaths") });
        var events = new[] { Ev(1, "KILL", 600), Ev(2, "DEATH", 606), Ev(3, "ASSIST", 612) };
        Assert.Empty(resolver.ResolveTeamfightClusters(events));
    }

    [Fact]
    public void TokenTiesForEvent_ExcludesTeamfightMembership()
    {
        // Objective tracks ONLY teamfight; a death inside a fight has NO token tie to it.
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("TEAMFIGHT", 9L, "Fights") });
        Assert.Empty(resolver.TokenTiesForEvent(Ev(1, "DEATH", 606)));

        // Objective tracks DEATH; now the death HAS a token tie.
        var resolver2 = ObjectiveEventTieResolver.FromTies(new[] { ("DEATH", 5L, "Deaths") });
        Assert.Single(resolver2.TokenTiesForEvent(Ev(1, "DEATH", 606)));
    }

    [Fact]
    public void DeDupesObjectivePerEvent_WhenTokenAndTeamfightBothMatch()
    {
        // One objective tracks both DEATH and TEAMFIGHT; a death inside a cluster must
        // not be listed twice for that objective.
        var resolver = ObjectiveEventTieResolver.FromTies(new[]
        {
            ("DEATH", 5L, "Deaths in fights"),
            ("TEAMFIGHT", 5L, "Deaths in fights"),
        });
        var events = new[] { Ev(1, "KILL", 600), Ev(2, "DEATH", 604), Ev(3, "ASSIST", 610) };

        var ties = resolver.ResolveForGame(events);

        Assert.Single(ties[2]);            // death matches via token AND teamfight → one entry
        Assert.Equal(5L, ties[2][0].ObjectiveId);
    }
}
