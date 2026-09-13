using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

public sealed class ObjectiveMasterySnapshotTests
{
    private const long FirstGameTime = 1_780_000_000;
    private const long Day = 86400;

    private static ObjectivesSnapshotBuilder Builder(SidecarWriteScope scope) => new(
        scope.Objectives, scope.Games, scope.Config,
        NullLogger<ObjectivesSnapshotBuilder>.Instance);

    [Theory]
    [InlineData(2, false)]
    [InlineData(5, true)]
    public async Task SuccessfulGames_ReportActualDaySpanWithoutPrematureReadiness(int spanDays, bool ready)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Check the minimap before pushing");
        for (var i = 0; i < 3; i++)
        {
            var game = await scope.SeedGameAsync(7700 + i, timestamp: FirstGameTime + i * spanDays * Day / 2);
            await scope.Objectives.RecordGameAsync(game.GameId, objectiveId, practiced: true);
        }

        var card = Assert.Single((await Builder(scope).BuildAsync()).ActiveObjectives);
        var details = Assert.IsType<ObjectiveMasteryDetailsDto>(card.MasteryDetails);
        Assert.Equal(6, card.Score);
        Assert.Equal(100, card.MasteryPct);
        Assert.Equal(3, card.MasteryQualifyingGames);
        Assert.Equal(ready, card.MasteryMet);
        Assert.Equal(spanDays, details.SpanDays);
        Assert.Equal(80, details.SuccessThresholdPct);
        Assert.Equal(3, details.MinGames);
        Assert.Equal(5, details.MinSpanDays);
        Assert.True(details.RecentSuccessMet);
        Assert.True(details.SuccessRateMet);

        // Pin the wire names consumed by the desktop readiness checklist.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(card, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var wireDetails = json.RootElement.GetProperty("masteryDetails");
        Assert.Equal(spanDays, wireDetails.GetProperty("spanDays").GetInt32());
        Assert.True(wireDetails.GetProperty("recentSuccessMet").GetBoolean());
        Assert.True(wireDetails.GetProperty("successRateMet").GetBoolean());
    }

    [Fact]
    public async Task FiftyEffortPoints_KeepExistingReadinessAlternativeWithoutFakingConsistency()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Notice support positioning");
        var game = await scope.SeedGameAsync(7800, timestamp: FirstGameTime);
        await scope.Objectives.RecordGameAsync(game.GameId, objectiveId, practiced: false);
        for (var i = 0; i < 25; i++)
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: game.GameId,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: $"mastery-test-{i}",
                StartTimeSeconds: i * 30,
                EndTimeSeconds: null,
                Title: "Support positioning",
                ObjectiveId: objectiveId));
        }

        var card = Assert.Single((await Builder(scope).BuildAsync()).ActiveObjectives);
        var details = Assert.IsType<ObjectiveMasteryDetailsDto>(card.MasteryDetails);
        Assert.Equal(50, card.Score);
        Assert.True(card.MasteryMet);
        Assert.Equal(0, card.MasteryPct);
        Assert.Equal(1, card.MasteryQualifyingGames);
        Assert.Equal(0, details.SpanDays);
        Assert.False(details.RecentSuccessMet);
        Assert.False(details.SuccessRateMet);
        Assert.Equal("active", (await scope.Objectives.GetAsync(objectiveId))!.Status);
    }

    [Fact]
    public async Task RoundedEightyPercent_DoesNotClaimTheRawSuccessRateRequirementIsMet()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Meet the CS target");
        await scope.Objectives.UpdateCriteriaAsync(objectiveId, "cs_per_min", ">=", 7);
        for (var i = 0; i < 49; i++)
        {
            var game = await scope.SeedGameAsync(7900 + i, timestamp: FirstGameTime + i * Day / 8);
            await scope.Objectives.RecordGameAsync(game.GameId, objectiveId, practiced: false);
            await scope.Objectives.SetCriteriaMetAsync(game.GameId, objectiveId, met: i >= 10);
        }

        var card = Assert.Single((await Builder(scope).BuildAsync()).ActiveObjectives);
        var details = Assert.IsType<ObjectiveMasteryDetailsDto>(card.MasteryDetails);
        Assert.Equal(80, card.MasteryPct); // 39/49 = 79.59%, rounded for display.
        Assert.Equal(49, card.MasteryQualifyingGames);
        Assert.Equal(6, details.SpanDays);
        Assert.True(details.RecentSuccessMet);
        Assert.False(details.SuccessRateMet);
        Assert.False(card.MasteryMet);
    }

    [Fact]
    public async Task EmptyObjective_ReportsNoQualifyingGames_WhileMiniHasNoMasteryDetails()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Map awareness");
        var miniId = await scope.Objectives.CreateAsync("Try a ward timing", type: "mini");

        var snapshot = await Builder(scope).BuildAsync();
        var card = Assert.Single(snapshot.ActiveObjectives, item => item.Id == objectiveId);
        var details = Assert.IsType<ObjectiveMasteryDetailsDto>(card.MasteryDetails);
        Assert.Equal(0, card.MasteryQualifyingGames);
        Assert.Equal(0, details.SpanDays);
        Assert.False(details.RecentSuccessMet);
        Assert.False(details.SuccessRateMet);
        Assert.False(card.MasteryMet);
        Assert.Null(Assert.Single(snapshot.ActiveObjectives, item => item.Id == miniId).MasteryDetails);
    }
}
