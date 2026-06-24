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
