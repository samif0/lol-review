using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class TimelineInferenceServiceTests
{
    [Fact]
    public void Infer_CreatesObjectiveFightAroundDragon()
    {
        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 590 },
            new() { EventType = GameEvent.EventTypes.Assist, GameTimeS = 596 },
            new() { EventType = GameEvent.EventTypes.Dragon, GameTimeS = 604 },
        };

        var regions = TimelineInferenceService.Infer(events);

        var region = Assert.Single(regions);
        Assert.Equal("Won Dragon fight", region.Name);
        Assert.True(region.StartTimeSeconds <= 590);
        Assert.True(region.EndTimeSeconds >= 604);
        Assert.Contains("within 30s", region.Tooltip);
    }

    [Fact]
    public void Infer_ClassifiesCombatClusters()
    {
        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 900 },
            new() { EventType = GameEvent.EventTypes.Death, GameTimeS = 906 },
            new() { EventType = GameEvent.EventTypes.Assist, GameTimeS = 980 },
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 984 },
            new() { EventType = GameEvent.EventTypes.Assist, GameTimeS = 990 },
        };

        var regions = TimelineInferenceService.Infer(events);

        Assert.Collection(
            regions,
            first => Assert.Equal("2v2 skirmish", first.Name),
            second => Assert.Equal("Won 3v3 skirmish", second.Name));
    }

    [Fact]
    public void Infer_PrefersObjectiveFightOverOverlappingCombatCluster()
    {
        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Death, GameTimeS = 500 },
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 510 },
            new() { EventType = GameEvent.EventTypes.Dragon, GameTimeS = 512 },
            new() { EventType = GameEvent.EventTypes.Assist, GameTimeS = 520 },
        };

        var regions = TimelineInferenceService.Infer(events);

        var region = Assert.Single(regions);
        Assert.Equal("Dragon fight", region.Name);
    }

    [Fact]
    public void Infer_CreatesDeathBeforeMajorObjectiveRegion()
    {
        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Death, GameTimeS = 840 },
            new() { EventType = GameEvent.EventTypes.Baron, GameTimeS = 900 },
        };

        var regions = TimelineInferenceService.Infer(events);

        var region = Assert.Single(regions);
        Assert.Equal("Death before Baron", region.Name);
        Assert.Contains("60s before", region.Tooltip);
    }

    [Fact]
    public void Infer_PromotesEarlyFirstKillAndDeath()
    {
        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 210 },
            new() { EventType = GameEvent.EventTypes.Death, GameTimeS = 330 },
            new() { EventType = GameEvent.EventTypes.Dragon, GameTimeS = 900 },
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 905 },
            new() { EventType = GameEvent.EventTypes.Assist, GameTimeS = 910 },
        };

        var regions = TimelineInferenceService.Infer(events);

        Assert.Contains(regions, region => region.Name == "First kill");
        Assert.Contains(regions, region => region.Name == "First death");
        Assert.Contains(regions, region => region.Name == "Won Dragon fight");
    }

    [Fact]
    public void Infer_PrefersFirstBloodOverDuplicateFirstKillCluster()
    {
        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 180 },
            new() { EventType = GameEvent.EventTypes.FirstBlood, GameTimeS = 180 },
        };

        var regions = TimelineInferenceService.Infer(events);

        var region = Assert.Single(regions);
        Assert.Equal("First Blood", region.Name);
    }

    [Fact]
    public void Infer_LabelsLoneDeathAsDeathNotIsolatedDeath()
    {
        // F3: a lone death must be titled just "Death" — never "Isolated death".
        // We can't prove isolation from the kill-feed (no positions), so the old
        // "Isolated death" guess is gone. The first death promotes to "First
        // death"; a LATER isolated death is the single-cluster case under test.
        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Death, GameTimeS = 300 },  // → "First death"
            new() { EventType = GameEvent.EventTypes.Death, GameTimeS = 1500 }, // → "Death" (isolated)
        };

        var regions = TimelineInferenceService.Infer(events);

        Assert.DoesNotContain(regions, r => r.Name == "Isolated death");
        Assert.Contains(regions, r => r.Name == "Death" && r.StartTimeSeconds <= 1500 && r.EndTimeSeconds >= 1500);
    }

    [Fact]
    public void Infer_LabelsLoneKillAsPick()
    {
        // A lone kill (no surrounding teamfight) is still a "Pick" — the events
        // DO support that. First kill promotes to "First kill"; a later isolated
        // kill is the single-cluster "Pick".
        var events = new List<GameEvent>
        {
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 300 },  // → "First kill"
            new() { EventType = GameEvent.EventTypes.Kill, GameTimeS = 1500 }, // → "Pick" (isolated)
        };

        var regions = TimelineInferenceService.Infer(events);

        Assert.Contains(regions, r => r.Name == "Pick" && r.StartTimeSeconds <= 1500 && r.EndTimeSeconds >= 1500);
    }
}
