using System.Text.Json;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// Pure unit tests for <see cref="JungleGankClassifier"/> — stamps laning-phase DEATH
/// events as jungle ganks when the enemy jungler (killer or assister) was on the kill.
/// </summary>
public sealed class JungleGankClassifierTests
{
    private static GameEvent Death(int t, string killer, params string[] assisters) => new()
    {
        EventType = GameEvent.EventTypes.Death,
        GameTimeS = t,
        Details = JsonSerializer.Serialize(new { killer, assisters }),
    };

    private static bool IsGank(GameEvent e)
    {
        using var doc = JsonDocument.Parse(e.Details);
        return doc.RootElement.TryGetProperty("jungle_gank", out var v)
            && v.ValueKind == JsonValueKind.True;
    }

    [Fact]
    public void Flags_WhenJunglerIsTheKiller_InLaningPhase()
    {
        var death = Death(300, killer: "LeeSinMain", "TopLaner");
        var stamped = JungleGankClassifier.Stamp(new[] { death }, new[] { "LeeSinMain" });

        Assert.Equal(1, stamped);
        Assert.True(IsGank(death));
        using var doc = JsonDocument.Parse(death.Details);
        Assert.Equal("jungle", doc.RootElement.GetProperty("killed_by_role").GetString());
        // Original keys preserved.
        Assert.Equal("LeeSinMain", doc.RootElement.GetProperty("killer").GetString());
    }

    [Fact]
    public void Flags_WhenJunglerIsAnAssister()
    {
        // Laner got the kill, but the jungler assisted — still a gank by the user's rule.
        var death = Death(420, killer: "MidLaner", "LeeSinMain");
        var stamped = JungleGankClassifier.Stamp(new[] { death }, new[] { "LeeSinMain" });

        Assert.Equal(1, stamped);
        Assert.True(IsGank(death));
    }

    [Fact]
    public void DoesNotFlag_AfterLaningPhase()
    {
        // 14:01 — past the laning-phase cutoff, so a jungler kill is a mid-game rotation,
        // not a lane gank.
        var death = Death(JungleGankClassifier.LaningPhaseEndSeconds + 1, killer: "LeeSinMain");
        var stamped = JungleGankClassifier.Stamp(new[] { death }, new[] { "LeeSinMain" });

        Assert.Equal(0, stamped);
        Assert.False(IsGank(death));
    }

    [Fact]
    public void Flags_ExactlyAtLaningPhaseCutoff()
    {
        var death = Death(JungleGankClassifier.LaningPhaseEndSeconds, killer: "LeeSinMain");
        var stamped = JungleGankClassifier.Stamp(new[] { death }, new[] { "LeeSinMain" });
        Assert.Equal(1, stamped);
    }

    [Fact]
    public void DoesNotFlag_WhenKillerAndAssistersAreNotTheJungler()
    {
        var death = Death(300, killer: "MidLaner", "TopLaner");
        var stamped = JungleGankClassifier.Stamp(new[] { death }, new[] { "LeeSinMain" });

        Assert.Equal(0, stamped);
        Assert.False(IsGank(death));
    }

    [Fact]
    public void NoOp_WhenJunglerUnknown()
    {
        // Empty jungler set ⇒ don't guess. A jungler-shaped death stays unflagged.
        var death = Death(300, killer: "LeeSinMain");
        var stamped = JungleGankClassifier.Stamp(new[] { death }, System.Array.Empty<string>());

        Assert.Equal(0, stamped);
        Assert.False(IsGank(death));
    }

    [Fact]
    public void IgnoresNonDeathEvents()
    {
        var kill = new GameEvent
        {
            EventType = GameEvent.EventTypes.Kill,
            GameTimeS = 300,
            Details = JsonSerializer.Serialize(new { victim = "LeeSinMain" }),
        };
        var stamped = JungleGankClassifier.Stamp(new[] { kill }, new[] { "LeeSinMain" });
        Assert.Equal(0, stamped);
    }

    [Fact]
    public void MatchesNamesCaseInsensitively()
    {
        var death = Death(300, killer: "leesinmain");
        var stamped = JungleGankClassifier.Stamp(new[] { death }, new[] { "LeeSinMain" });
        Assert.Equal(1, stamped);
    }

    [Fact]
    public void MatchesTaggedRiotIdAgainstBareGameName_BothDirections()
    {
        // Kill-feed carries a bare game name; jungler set has the tagged Riot ID.
        var death1 = Death(300, killer: "Faker");
        Assert.Equal(1, JungleGankClassifier.Stamp(new[] { death1 }, new[] { "Faker#NA1" }));

        // …and the reverse: kill-feed has the tag, jungler set is bare.
        var death2 = Death(300, killer: "Faker#NA1");
        Assert.Equal(1, JungleGankClassifier.Stamp(new[] { death2 }, new[] { "Faker" }));
    }
}
