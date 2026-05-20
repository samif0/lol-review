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
}
