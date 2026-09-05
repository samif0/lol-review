using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// The shared fight set every read path uses (<see cref="TeamfightClustering"/>) and
/// the resolver / auto-clipper / materializer behaviour on top of STORED post-game
/// TEAMFIGHT rows: stored rows first, synthetic clusters as the safety net, fights the
/// player was not in tie but never clip.
/// </summary>
public sealed class TeamfightClusteringTests
{
    private static GameEvent Ev(int id, string type, int t, string details = "{}") =>
        new() { Id = id, EventType = type, GameTimeS = t, Details = details };

    private static GameEvent Fight(int id, int startS, int endS, string self = "in", string verdict = "down", string numbers = "2v3") =>
        Ev(id, "TEAMFIGHT", startS,
            $$"""{ "detected": true, "start_s": {{startS}}, "end_s": {{endS}}, "self": "{{self}}", "numbers": "{{numbers}}", "verdict": "{{verdict}}" }""");

    // ── TeamfightClustering ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_WithoutStoredRows_IsTodaysSyntheticRule()
    {
        var events = new[] { Ev(1, "KILL", 600), Ev(2, "DEATH", 606), Ev(3, "ASSIST", 612), Ev(4, "KILL", 1200) };

        var spans = TeamfightClustering.Resolve(events);

        var s = Assert.Single(spans);
        Assert.Null(s.Stored);
        Assert.Equal(600, s.StartS);
        Assert.Equal(612, s.EndS);
        Assert.Equal(3, s.Members.Count);
        Assert.True(s.IsOwn);
    }

    [Fact]
    public void Resolve_PrefersStoredOwnRow_AndAnchorsTheSpanOnTheFirstMember()
    {
        // The fight started with an ally's death at 595 (server clock); the player's
        // first event is at 600 — the clip/anchor key stays at 600 as it was before.
        var events = new[]
        {
            Ev(1, "KILL", 600), Ev(2, "DEATH", 606), Ev(3, "ASSIST", 612),
            Fight(9, startS: 595, endS: 614),
        };

        var s = Assert.Single(TeamfightClustering.Resolve(events));

        Assert.Same(events[3], s.Stored);
        Assert.Equal(600, s.StartS);
        Assert.Equal(614, s.EndS);
        Assert.Equal(595, s.FightStartS);
        Assert.Equal(614, s.FightEndS);
        Assert.Equal(new[] { 1, 2, 3 }, s.Members.Select(m => m.Id).ToArray());
        Assert.Equal("2v3", s.Numbers);
        Assert.Equal("down", s.Verdict);
    }

    [Fact]
    public void Resolve_AssignsEdgeEvents_WithinOneGapOfTheWindow_ToTheNearestFight()
    {
        var events = new[]
        {
            Fight(9, startS: 600, endS: 610),
            Fight(10, startS: 660, endS: 670),
            Ev(1, "DEATH", 597),   // 3 s before fight A
            Ev(2, "KILL", 623),    // 13 s after fight A, 37 s before B → A
            Ev(3, "KILL", 640),    // 30 s from both → nobody
            Ev(4, "ASSIST", 655),  // 5 s before B
        };

        var spans = TeamfightClustering.Resolve(events);

        Assert.Equal(new[] { 1, 2 }, spans[0].Members.Select(m => m.Id).ToArray());
        Assert.Equal(new[] { 4 }, spans[1].Members.Select(m => m.Id).ToArray());
        Assert.Equal(597, spans[0].StartS);
        Assert.Equal(623, spans[0].EndS);
    }

    [Fact]
    public void Resolve_AwayRow_TakesNoMembers_AndSyntheticClusterOverIt_Survives()
    {
        var events = new[]
        {
            Fight(9, startS: 600, endS: 612, self: "away", numbers: "3v3", verdict: "even"),
            Ev(1, "KILL", 601), Ev(2, "ASSIST", 605), Ev(3, "KILL", 611),
        };

        var spans = TeamfightClustering.Resolve(events);

        Assert.Equal(2, spans.Count);
        var away = Assert.Single(spans, s => s.Stored is not null);
        Assert.Empty(away.Members);
        Assert.False(away.IsOwn);
        var synthetic = Assert.Single(spans, s => s.Stored is null);
        Assert.Equal(3, synthetic.Members.Count);
    }

    [Fact]
    public void Resolve_SyntheticClusterOverlappingAnOwnStoredFight_IsNotDuplicated()
    {
        var events = new[]
        {
            Fight(9, startS: 600, endS: 612),
            Ev(1, "KILL", 601), Ev(2, "ASSIST", 605), Ev(3, "KILL", 611),
            Ev(4, "KILL", 900), Ev(5, "DEATH", 905), Ev(6, "ASSIST", 910), // unpaired late fight
        };

        var spans = TeamfightClustering.Resolve(events);

        Assert.Equal(2, spans.Count);
        Assert.NotNull(spans[0].Stored);
        Assert.Null(spans[1].Stored);
        Assert.Equal(900, spans[1].StartS);
    }

    [Fact]
    public void ReadSpan_IsTypeGated_SoAReviewedTradeWithAWindowIsNeverAFight()
    {
        var trade = Ev(5, "TRADE", 100, "{\"source\":\"reviewed_encounter\",\"start_s\":100,\"end_s\":114,\"kind\":\"extended\"}");

        Assert.False(TeamfightClustering.IsStoredTeamfight(trade));
        Assert.Equal((100, 100), TeamfightClustering.ReadSpan(trade));
        Assert.Empty(TeamfightClustering.Resolve([trade]));
    }

    [Fact]
    public void KeyAnchor_AddsTheEndOnlyWhenAStartIsAlreadyClaimed()
    {
        var claimed = new HashSet<string>();
        var a = new TeamfightSpan(600, 612, 600, 612, [], null, "", "", "");
        var b = new TeamfightSpan(600, 640, 600, 640, [], null, "", "", "");
        var c = new TeamfightSpan(700, 710, 700, 710, [], null, "", "", "");

        Assert.Equal("600", TeamfightClustering.KeyAnchor(a, claimed));
        Assert.Equal("600-640", TeamfightClustering.KeyAnchor(b, claimed));
        Assert.Equal("700", TeamfightClustering.KeyAnchor(c, claimed));
    }

    // ── ObjectiveEventTieResolver on stored rows ────────────────────────────

    [Fact]
    public void Resolver_StoredOwnFight_TiesTeamfightAndVerdictTrackers_MembersIncludeTheRow()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[]
        {
            ("TEAMFIGHT", 1L, "Fights"),
            ("OUTNUMBERED_TEAMFIGHT", 2L, "Avoid bad fights"),
            ("NUMBERS_UP_TEAMFIGHT", 3L, "Take good fights"),
        });
        var events = new[] { Fight(9, 600, 612, verdict: "down"), Ev(1, "KILL", 601), Ev(2, "DEATH", 608) };

        var c = Assert.Single(resolver.ResolveTeamfightClusters(events));

        Assert.Equal(9, c.StoredEventId);
        Assert.Equal(new[] { 9, 1, 2 }, c.MemberEventIds.ToArray());
        Assert.Equal(new[] { 1L, 2L }, c.Objectives.Select(o => o.ObjectiveId).ToArray()); // TEAMFIGHT trackers first, no NUMBERS_UP
        Assert.Equal("2v3", c.Numbers);
        Assert.Equal("in", c.Self);
        Assert.Equal(600, c.FightStartS);

        // The member events tie through the fight; the row itself carries its tokens.
        var ties = resolver.ResolveForGame(events);
        Assert.Contains(ties[1], t => t.ObjectiveId == 2L);
        Assert.Equal(new[] { "OUTNUMBERED_TEAMFIGHT", "TEAMFIGHT" }, ObjectiveEventTieResolver.EventTokens(events[0]));
    }

    [Fact]
    public void Resolver_VerdictOnlyObjective_TiesOnlyMatchingStoredFights_NotSyntheticOnes()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("OUTNUMBERED_TEAMFIGHT", 2L, "Avoid bad fights") });
        var events = new[]
        {
            Fight(9, 600, 612, verdict: "down"),
            Fight(10, 700, 712, verdict: "up", numbers: "4v2"),
            Ev(1, "KILL", 900), Ev(2, "ASSIST", 905), Ev(3, "KILL", 910), // synthetic, no verdict
        };

        var clusters = resolver.ResolveTeamfightClusters(events);

        var c = Assert.Single(clusters);
        Assert.Equal(9, c.StoredEventId);
    }

    [Fact]
    public void Resolver_AwayFight_TiesAbsentTrackersOnly_AndNoMembers()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[]
        {
            ("TEAMFIGHT", 1L, "Fights"),
            ("ABSENT_TEAMFIGHT", 4L, "Fights I skipped"),
        });
        var away = Fight(9, 600, 612, self: "away", numbers: "3v3", verdict: "even");
        var events = new[] { away, Ev(1, "KILL", 601) };

        var c = Assert.Single(resolver.ResolveTeamfightClusters(events));

        Assert.Equal(new[] { 9 }, c.MemberEventIds.ToArray());
        Assert.Equal(new[] { 4L }, c.Objectives.Select(o => o.ObjectiveId).ToArray());
        Assert.Equal("away", c.Self);
        Assert.Equal(new[] { "ABSENT_TEAMFIGHT" }, ObjectiveEventTieResolver.EventTokens(away));
        Assert.Empty(ObjectiveEventTieResolver.EventTokens(Fight(11, 800, 810, self: "")));
    }

    [Fact]
    public void Resolver_NoFightTokenTracked_ReturnsNoClusters_EvenWithStoredRows()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("DEATH", 1L, "Deaths") });
        Assert.Empty(resolver.ResolveTeamfightClusters([Fight(9, 600, 612), Ev(1, "DEATH", 601)]));
    }

    // ── AutoClipPlanner on stored rows ──────────────────────────────────────

    [Fact]
    public void Planner_StoredOwnFight_ClipsOnce_AndNeverAgainOnceOnDisk()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("TEAMFIGHT", 1L, "Fights"), ("DEATH", 1L, "Fights") });
        var events = new[] { Fight(9, 600, 612), Ev(1, "KILL", 600), Ev(2, "DEATH", 606), Ev(3, "ASSIST", 612) };

        var first = AutoClipPlanner.SelectClips(7L, events, resolver, null, 1800, new HashSet<string>(), out _);
        var clip = Assert.Single(first);
        Assert.True(clip.IsTeamfight);
        Assert.Equal(9, clip.EventId);
        Assert.Equal("autoclip-tf:7:600", clip.SourceKey);

        // Second press: the fight key is on disk; nothing else (not the row, not the
        // in-fight death) may be planned.
        var second = AutoClipPlanner.SelectClips(7L, events, resolver, null, 1800,
            new HashSet<string> { clip.SourceKey }, out _);
        Assert.Empty(second);
    }

    [Fact]
    public void Planner_AwayFight_IsNeverClipped()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("ABSENT_TEAMFIGHT", 4L, "Skipped fights") });
        var events = new[] { Fight(9, 600, 612, self: "away", numbers: "3v3", verdict: "even"), Ev(1, "KILL", 900) };

        Assert.Empty(AutoClipPlanner.SelectClips(7L, events, resolver, null, 1800, new HashSet<string>(), out _));
    }

    [Fact]
    public void Planner_TwoFightsStartingTheSameSecond_GetDistinctKeys()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("TEAMFIGHT", 1L, "Fights") });
        var events = new[] { Fight(9, 600, 612), Fight(10, 600, 640, numbers: "3v3", verdict: "even") };

        var clips = AutoClipPlanner.SelectClips(7L, events, resolver, null, 1800, new HashSet<string>(), out _);

        Assert.Equal(2, clips.Count);
        Assert.Equal(new[] { "autoclip-tf:7:600", "autoclip-tf:7:600-640" }, clips.Select(c => c.SourceKey).ToArray());
    }

    [Fact]
    public void Planner_VerdictOnlyObjective_ClipsOnlyItsFights()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("OUTNUMBERED_TEAMFIGHT", 2L, "Avoid bad fights") });
        var events = new[] { Fight(9, 600, 612, verdict: "down"), Fight(10, 900, 912, verdict: "up", numbers: "4v2") };

        var clip = Assert.Single(AutoClipPlanner.SelectClips(7L, events, resolver, 2L, 1800, new HashSet<string>(), out _));
        Assert.Equal(600, clip.EventTimeS);
        Assert.Equal(2L, clip.ObjectiveId);
    }
}
