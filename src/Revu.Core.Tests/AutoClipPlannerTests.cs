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
}
