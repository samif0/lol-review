using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

public sealed class ObjectiveCriteriaTests
{
    [Theory]
    [InlineData("cs_per_min", ">=", 7.0, 7.4, true)]
    [InlineData("cs_per_min", ">=", 7.0, 6.1, false)]
    [InlineData("deaths", "<=", 3.0, 2, true)]
    [InlineData("deaths", "<=", 3.0, 5, false)]
    public void Evaluate_ComparesAgainstThreshold(string metric, string op, double threshold, double actual, bool expected)
    {
        var stats = new GameStats
        {
            CsPerMin = metric == "cs_per_min" ? actual : 0,
            Deaths = metric == "deaths" ? (int)actual : 0,
        };

        var result = ObjectiveCriteria.Evaluate(metric, op, threshold, stats);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void Evaluate_ReturnsNullForUnknownMetricOrEmpty()
    {
        var stats = new GameStats { CsPerMin = 8 };

        Assert.Null(ObjectiveCriteria.Evaluate("", ">=", 5, stats));
        Assert.Null(ObjectiveCriteria.Evaluate(null, ">=", 5, stats));
        Assert.Null(ObjectiveCriteria.Evaluate("no_such_metric", ">=", 5, stats));
    }

    [Fact]
    public void Evaluate_ReturnsNullWhenStatUnavailable()
    {
        // Laning-at-10 stats are null until the timeline backfill runs —
        // an unbackfilled game must not produce a fail.
        var stats = new GameStats { CsAt10 = null };

        Assert.Null(ObjectiveCriteria.Evaluate("cs_at_10", ">=", 70, stats));
    }

    [Fact]
    public void Evaluate_NormalizesUnknownOpToAtLeast()
    {
        var stats = new GameStats { CsPerMin = 8 };

        var result = ObjectiveCriteria.Evaluate("cs_per_min", "??", 7, stats);

        Assert.True(result);
    }

    [Fact]
    public void Describe_RendersHumanReadableCriterion()
    {
        Assert.Equal("CS per minute ≥ 7", ObjectiveCriteria.Describe("cs_per_min", ">=", 7.0));
        Assert.Equal("Deaths ≤ 3.5", ObjectiveCriteria.Describe("deaths", "<=", 3.5));
        Assert.Equal("", ObjectiveCriteria.Describe("", ">=", 7.0));
    }

    [Fact]
    public void Metrics_AllHaveUniqueKeys()
    {
        var keys = ObjectiveCriteria.Metrics.Select(m => m.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Repository_RoundTripsCriteriaAndStampsOutcome()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Sivir", true);
        var objectiveId = await scope.Objectives.CreateAsync(
            "Farm fundamentals",
            skillArea: "laning",
            completionCriteria: "7+ CS/min");

        await scope.Objectives.UpdateCriteriaAsync(objectiveId, "cs_per_min", ">=", 7.0);

        var objective = await scope.Objectives.GetAsync(objectiveId);
        Assert.NotNull(objective);
        Assert.True(objective!.HasStructuredCriteria);
        Assert.Equal("cs_per_min", objective.CriteriaMetric);
        Assert.Equal(">=", objective.CriteriaOp);
        Assert.Equal(7.0, objective.CriteriaValue);

        // No row yet — stamping must not create one.
        await scope.Objectives.SetCriteriaMetAsync(gameId, objectiveId, met: true);
        var beforePractice = await scope.Objectives.GetGameObjectivesAsync(gameId);
        Assert.Empty(beforePractice);

        await scope.Objectives.RecordGameAsync(gameId, objectiveId, practiced: true);
        await scope.Objectives.SetCriteriaMetAsync(gameId, objectiveId, met: true);

        var record = Assert.Single(await scope.Objectives.GetGameObjectivesAsync(gameId));
        Assert.Equal(1, record.CriteriaMet);
        Assert.Equal("cs_per_min", record.CriteriaMetric);

        var (hits, evaluated) = await scope.Objectives.GetCriteriaHitRateAsync(objectiveId);
        Assert.Equal(1, hits);
        Assert.Equal(1, evaluated);

        // Flip to a miss — upsert semantics, still one evaluated game.
        await scope.Objectives.SetCriteriaMetAsync(gameId, objectiveId, met: false);
        (hits, evaluated) = await scope.Objectives.GetCriteriaHitRateAsync(objectiveId);
        Assert.Equal(0, hits);
        Assert.Equal(1, evaluated);

        // Clearing the criterion empties the structured fields.
        await scope.Objectives.UpdateCriteriaAsync(objectiveId, "", ">=", 0);
        var cleared = await scope.Objectives.GetAsync(objectiveId);
        Assert.False(cleared!.HasStructuredCriteria);
    }
}
