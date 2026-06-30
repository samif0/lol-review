using Revu.Core.Constants;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Pure tests for <see cref="AutoClipPlanner"/> — buffer math, dedupe, min-gap, and
/// the per-call cap, with no DB or ffmpeg.
/// </summary>
public sealed class AutoClipPlannerTests
{
    private static GameEvent Ev(int id, string type, int t) =>
        new() { Id = id, EventType = type, GameTimeS = t, Details = "{}" };

    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>();

    [Fact]
    public void BufferMath_AppliesPreAndPostRoll()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("DEATH", 1L, "Deaths") });
        var events = new[] { Ev(1, "DEATH", 600) };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, gameDurationS: 1800, NoKeys, out _);

        var clip = Assert.Single(clips);
        Assert.Equal(600 - GameConstants.AutoClipPreRollS, clip.StartS); // 570
        Assert.Equal(600 + GameConstants.AutoClipPostRollS, clip.EndS);  // 615
    }

    [Fact]
    public void StartClampsToZero_NearGameStart()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("DEATH", 1L, "Deaths") });
        var clips = AutoClipPlanner.SelectClips(100, new[] { Ev(1, "DEATH", 10) }, resolver, null, 1800, NoKeys, out _);
        Assert.Equal(0, Assert.Single(clips).StartS);
        Assert.Equal(10 + GameConstants.AutoClipPostRollS, clips[0].EndS);
    }

    [Fact]
    public void EndClampsToGameDuration_NearGameEnd()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("BARON", 1L, "Baron") });
        var clips = AutoClipPlanner.SelectClips(100, new[] { Ev(1, "BARON", 1795) }, resolver, null, gameDurationS: 1800, NoKeys, out _);
        Assert.Equal(1800, Assert.Single(clips).EndS);
    }

    [Fact]
    public void Dedupe_SkipsEventsAlreadyClipped()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("DEATH", 1L, "Deaths") });
        var events = new[] { Ev(1, "DEATH", 300), Ev(2, "DEATH", 900) };
        // Event 1 already has an autoclip evidence row.
        var existing = new HashSet<string> { AutoClipPlanner.SourceKey(100, 1) };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, 1800, existing, out _);

        var clip = Assert.Single(clips);
        Assert.Equal(2, clip.EventId);     // only the not-yet-clipped event 2 survives
    }

    [Fact]
    public void MinGap_CollapsesOverlappingWindows()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("DEATH", 1L, "Deaths") });
        // Two deaths only 5s apart → second is within the min-gap of the first kept start.
        var events = new[] { Ev(1, "DEATH", 600), Ev(2, "DEATH", 605), Ev(3, "DEATH", 900) };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, 1800, NoKeys, out _);

        Assert.Equal(2, clips.Count);      // event 2 collapsed into event 1's window
        Assert.Equal(1, clips[0].EventId);
        Assert.Equal(3, clips[1].EventId);
    }

    [Fact]
    public void Cap_LimitsClipsPerCall_AndReportsSkipped()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("DEATH", 1L, "Deaths") });
        // Spread well beyond min-gap so all are independently eligible; exceed the cap.
        var n = GameConstants.AutoClipMaxPerCall + 3;
        var events = Enumerable.Range(0, n)
            .Select(i => Ev(i + 1, "DEATH", 100 + i * 100))
            .ToArray();

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, 6000, NoKeys, out var skippedByCap);

        Assert.Equal(GameConstants.AutoClipMaxPerCall, clips.Count);
        Assert.Equal(3, skippedByCap);
    }

    [Fact]
    public void ObjectiveFilter_RestrictsToFramedObjective()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[]
        {
            ("DEATH", 1L, "Deaths"),
            ("DRAGON", 2L, "Dragons"),
        });
        var events = new[] { Ev(1, "DEATH", 300), Ev(2, "DRAGON", 900) };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, objectiveId: 2L, 1800, NoKeys, out _);

        var clip = Assert.Single(clips);
        Assert.Equal(2, clip.EventId);
        Assert.Equal(2L, clip.ObjectiveId);
    }

    [Fact]
    public void NoActiveObjectives_ProducesNothing()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(System.Array.Empty<(string, long, string)>());
        var clips = AutoClipPlanner.SelectClips(100, new[] { Ev(1, "DEATH", 600) }, resolver, null, 1800, NoKeys, out _);
        Assert.Empty(clips);
    }

    // ── Teamfight grouping ────────────────────────────────────────────────────────
    // A TEAMFIGHT-only objective must clip ONE moment per fight (spanning the cluster),
    // not one per kill/death/assist inside it.

    [Fact]
    public void Teamfight_ClipsOnePerFight_NotPerCombatEvent()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("TEAMFIGHT", 9L, "Win fights") });
        // Five combat events within 14s of each other → one cluster spanning 600..624.
        var events = new[]
        {
            Ev(1, "KILL", 600),
            Ev(2, "DEATH", 606),
            Ev(3, "ASSIST", 612),
            Ev(4, "KILL", 618),
            Ev(5, "DEATH", 624),
        };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, gameDurationS: 1800, NoKeys, out _);

        var clip = Assert.Single(clips);                       // ONE clip, not five
        Assert.Equal(9L, clip.ObjectiveId);
        Assert.Equal(600 - GameConstants.AutoClipPreRollS, clip.StartS); // fight start − preroll = 570
        Assert.Equal(624 + GameConstants.AutoClipPostRollS, clip.EndS);  // fight end + postroll = 639
        Assert.StartsWith("autoclip-tf:", clip.SourceKey);
    }

    [Fact]
    public void TwoSeparateFights_ClipTwice()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("TEAMFIGHT", 9L, "Win fights") });
        var events = new[]
        {
            // Fight A ~600
            Ev(1, "KILL", 600), Ev(2, "DEATH", 606), Ev(3, "ASSIST", 612),
            // Fight B ~1200 (far past min-gap)
            Ev(4, "KILL", 1200), Ev(5, "DEATH", 1206), Ev(6, "ASSIST", 1212),
        };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, 1800, NoKeys, out _);

        Assert.Equal(2, clips.Count);
        Assert.All(clips, c => Assert.StartsWith("autoclip-tf:", c.SourceKey));
    }

    [Fact]
    public void TeamfightPlusDeath_DoesNotDoubleClipInFightDeaths_ButClipsLoneDeath()
    {
        // Objective tracks BOTH teamfight AND death. The deaths inside the fight are
        // covered by the one fight clip (not re-clipped); a death OUTSIDE any fight is
        // still clipped on its own.
        var resolver = ObjectiveEventTieResolver.FromTies(new[]
        {
            ("TEAMFIGHT", 5L, "Fights + deaths"),
            ("DEATH", 5L, "Fights + deaths"),
        });
        var events = new[]
        {
            // A fight at ~600 (3 combat events, includes a death at 606).
            Ev(1, "KILL", 600), Ev(2, "DEATH", 606), Ev(3, "ASSIST", 612),
            // A lone death at 1200, not part of any cluster.
            Ev(4, "DEATH", 1200),
        };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, 1800, NoKeys, out _);

        // One fight clip + one lone-death clip = 2 (NOT a fight clip + 3 in-fight events).
        Assert.Equal(2, clips.Count);
        var fight = clips.Single(c => c.SourceKey.StartsWith("autoclip-tf:"));
        Assert.Equal(570, fight.StartS);
        var lone = clips.Single(c => !c.SourceKey.StartsWith("autoclip-tf:"));
        Assert.Equal(4, lone.EventId);
        Assert.Equal(1200, lone.EventTimeS);
    }

    [Fact]
    public void TeamfightObjective_DoesNotClipInFightDeath_WhenItOnlyTracksTeamfight()
    {
        // Objective tracks ONLY teamfight. The in-fight death must NOT produce its own
        // clip (the bug: per-event clipping of cluster members).
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("TEAMFIGHT", 7L, "Fights") });
        var events = new[] { Ev(1, "KILL", 600), Ev(2, "DEATH", 606), Ev(3, "ASSIST", 612) };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, 1800, NoKeys, out _);

        var clip = Assert.Single(clips);
        Assert.StartsWith("autoclip-tf:", clip.SourceKey); // the fight, not the death
    }

    // ── Min-gap must not swallow DISTINCT moments (review regression guards) ─────────
    // Every clip subtracts the 30s pre-roll, so two distinct moments whose ANCHORS are
    // only ~15-19s apart used to collapse on start-proximity even though their windows
    // barely overlap. Min-gap now keys on END coverage, and a teamfight clip never
    // collapses into a neighbor.

    [Fact]
    public void TwoNearbyButDistinctFights_BothClip()
    {
        var resolver = ObjectiveEventTieResolver.FromTies(new[] { ("TEAMFIGHT", 9L, "Fights") });
        // Two SEPARATE clusters (gap 40-11=29s > the 14s teamfight gap) whose buffered
        // starts (0 and 10) are within the 20s min-gap — must NOT collapse.
        var events = new[]
        {
            Ev(1, "KILL", 5), Ev(2, "DEATH", 8), Ev(3, "ASSIST", 11),     // fight A 5..11
            Ev(4, "KILL", 40), Ev(5, "DEATH", 43), Ev(6, "ASSIST", 46),   // fight B 40..46
        };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, 1800, NoKeys, out _);

        Assert.Equal(2, clips.Count);
        Assert.All(clips, c => Assert.True(c.IsTeamfight));
    }

    [Fact]
    public void LoneEventJustBeforeAFight_DoesNotSwallowTheFightClip()
    {
        // Objective tracks DEATH and TEAMFIGHT. A lone death at 585 (its own clip) sits
        // just before a fight at 600 — the fight clip (a distinct moment) must survive.
        var resolver = ObjectiveEventTieResolver.FromTies(new[]
        {
            ("DEATH", 5L, "Deaths + fights"),
            ("TEAMFIGHT", 5L, "Deaths + fights"),
        });
        var events = new[]
        {
            Ev(1, "DEATH", 585),                                          // lone death (no cluster)
            Ev(2, "KILL", 600), Ev(3, "DEATH", 606), Ev(4, "ASSIST", 612), // fight 600..612
        };

        var clips = AutoClipPlanner.SelectClips(100, events, resolver, null, 1800, NoKeys, out _);

        Assert.Equal(2, clips.Count);
        Assert.Contains(clips, c => c.IsTeamfight);                       // the fight survived
        Assert.Contains(clips, c => !c.IsTeamfight && c.EventTimeS == 585); // the lone death
    }
}
