using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

public sealed class ObjectiveFocusPhasesTests
{
    [Theory]
    [InlineData("Improve wave management", ObjectiveFocusPhases.Laning)]
    [InlineData("Last hit under tower", ObjectiveFocusPhases.Laning)]
    [InlineData("Win my lane matchup 2v2", ObjectiveFocusPhases.Laning)]
    [InlineData("Trade efficiently in the early game", ObjectiveFocusPhases.Laning)]
    [InlineData("Freeze the wave when ahead", ObjectiveFocusPhases.Laning)]
    public void Infer_DetectsLaning(string title, string expected)
    {
        Assert.Equal(expected, ObjectiveFocusPhases.Infer(title, ""));
    }

    [Theory]
    [InlineData("Set up vision before Baron", ObjectiveFocusPhases.MidLate)]
    [InlineData("Rotate to objectives after recall", ObjectiveFocusPhases.MidLate)]
    [InlineData("Split push the side lane", ObjectiveFocusPhases.MidLate)]
    [InlineData("Control dragon soul point", ObjectiveFocusPhases.MidLate)]
    public void Infer_DetectsMidLate(string title, string expected)
    {
        Assert.Equal(expected, ObjectiveFocusPhases.Infer(title, ""));
    }

    [Theory]
    [InlineData("Improve teamfight positioning", ObjectiveFocusPhases.Teamfight)]
    [InlineData("Peel for the backline in fights", ObjectiveFocusPhases.Teamfight)]
    [InlineData("Better target selection in skirmishes", ObjectiveFocusPhases.Teamfight)]
    [InlineData("Track enemy cooldowns before engaging", ObjectiveFocusPhases.Teamfight)]
    public void Infer_DetectsTeamfight(string title, string expected)
    {
        Assert.Equal(expected, ObjectiveFocusPhases.Infer(title, ""));
    }

    [Fact]
    public void Infer_ReturnsAnyForUnmatched()
    {
        Assert.Equal(ObjectiveFocusPhases.Any, ObjectiveFocusPhases.Infer("Stay positive and have fun", ""));
        Assert.Equal(ObjectiveFocusPhases.Any, ObjectiveFocusPhases.Infer("", ""));
    }

    [Fact]
    public void Resolve_ExplicitTagWinsOverInference()
    {
        // Title screams laning, but an explicit Teamfight tag must override.
        var resolved = ObjectiveFocusPhases.Resolve(
            ObjectiveFocusPhases.Teamfight, "Last hit under tower in lane", "");
        Assert.Equal(ObjectiveFocusPhases.Teamfight, resolved);
    }

    [Fact]
    public void Resolve_AutoTagFallsBackToInference()
    {
        var resolved = ObjectiveFocusPhases.Resolve(
            ObjectiveFocusPhases.Auto, "Improve wave management", "");
        Assert.Equal(ObjectiveFocusPhases.Laning, resolved);
    }

    [Fact]
    public void Resolve_NeverReturnsAutoForUnmatched()
    {
        // Unmatched + no tag → Any (never the empty Auto sentinel), so the
        // objective isn't hidden from every clip.
        var resolved = ObjectiveFocusPhases.Resolve(ObjectiveFocusPhases.Auto, "Have fun", "");
        Assert.Equal(ObjectiveFocusPhases.Any, resolved);
    }

    [Theory]
    // Laning matches the first 14 minutes only.
    [InlineData(ObjectiveFocusPhases.Laning, 300, true)]
    [InlineData(ObjectiveFocusPhases.Laning, 840, false)]   // exactly 14:00 → post-lane
    [InlineData(ObjectiveFocusPhases.Laning, 1500, false)]
    // Teamfight / mid-late match after lane phase.
    [InlineData(ObjectiveFocusPhases.Teamfight, 300, false)]
    [InlineData(ObjectiveFocusPhases.Teamfight, 1500, true)]
    [InlineData(ObjectiveFocusPhases.MidLate, 1500, true)]
    [InlineData(ObjectiveFocusPhases.MidLate, 300, false)]
    // Any matches everything.
    [InlineData(ObjectiveFocusPhases.Any, 60, true)]
    [InlineData(ObjectiveFocusPhases.Any, 2000, true)]
    public void MatchesClipTime_GatesByPhaseWindow(string phase, int clipTimeS, bool expected)
    {
        Assert.Equal(expected, ObjectiveFocusPhases.MatchesClipTime(phase, clipTimeS, gameDurationS: 2400));
    }

    [Fact]
    public void Index_RoundTrips()
    {
        foreach (var phase in new[]
        {
            ObjectiveFocusPhases.Auto, ObjectiveFocusPhases.Laning,
            ObjectiveFocusPhases.MidLate, ObjectiveFocusPhases.Teamfight, ObjectiveFocusPhases.Any,
        })
        {
            var idx = ObjectiveFocusPhases.ToIndex(phase);
            Assert.Equal(phase, ObjectiveFocusPhases.FromIndex(idx));
        }
    }
}
