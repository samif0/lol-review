namespace Revu.Core.Tests;

/// <summary>
/// RecordGameAsync must be a true UPSERT. The original INSERT OR REPLACE
/// deleted + re-inserted the (game, objective) row on every re-save, which
/// silently nulled criteria_met — shrinking the criteria hit-rate and mastery
/// denominators each time a review was edited.
/// </summary>
public sealed class ObjectivesRecordGameUpsertTests
{
    [Fact]
    public async Task RecordGame_ReSave_PreservesCriteriaMet()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        const long gameId = 7_501;
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(gameId, champion: "Ahri"));
        var objectiveId = await scope.Objectives.CreateAsync("Ward before objectives", completionCriteria: "x");

        await scope.Objectives.RecordGameAsync(gameId, objectiveId, practiced: true);
        await scope.Objectives.SetCriteriaMetAsync(gameId, objectiveId, met: true);

        // Re-save (a review edit re-records the same objective).
        await scope.Objectives.RecordGameAsync(gameId, objectiveId, practiced: true, executionNote: "edited note");

        var records = await scope.Objectives.GetGameObjectivesAsync(gameId);
        var record = Assert.Single(records);
        Assert.Equal(1, record.CriteriaMet);
        Assert.Equal("edited note", record.ExecutionNote);

        var (hits, evaluated) = await scope.Objectives.GetCriteriaHitRateAsync(objectiveId);
        Assert.Equal(1, hits);
        Assert.Equal(1, evaluated);
    }
}
