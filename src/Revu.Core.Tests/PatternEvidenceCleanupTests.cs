using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Constants;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// v3.11: the materializer's stale-anchor cleanup covers every objev: anchor, not only fights,
/// so a correction that moves or removes an event takes its anchor with it. Only untouched
/// default rows go; a token an objective merely stopped tracking keeps its history.
/// </summary>
public sealed class PatternEvidenceCleanupTests
{
    private const long GameId = 7301;

    private static PatternEvidenceMaterializer Create(TestDatabaseScope scope) => new(
        scope.GameEvents, scope.Evidence, scope.Objectives, scope.Games,
        NullLogger<PatternEvidenceMaterializer>.Instance);

    private static GameEvent Ev(string type, int t, string details = "{}") =>
        new() { GameId = GameId, EventType = type, GameTimeS = t, Details = details };

    private static async Task SeedGameAsync(TestDatabaseScope scope)
    {
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(GameId, timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600));
    }

    private static EventCorrectionSubject Subject(GameEvent row) => new(row.EventKey, row.Id, row.EventType, row.GameTimeS);

    [Fact]
    public async Task Materialize_RemovesStalePointAnchorAfterARetime_KeepsNotedAndTriagedRows()
    {
        using var scope = new TestDatabaseScope();
        await SeedGameAsync(scope);
        var objective = await scope.Objectives.CreateAsync("Die less", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objective, new[] { GameEvent.EventTypes.Death });
        await scope.GameEvents.SaveEventsAsync(GameId,
        [
            Ev("DEATH", 812, "{\"killer\":\"Lee Sin\"}"),
            Ev("DEATH", 900, "{\"killer\":\"Ahri\"}"),
            Ev("DEATH", 1000, "{\"killer\":\"Zed\"}"),
        ]);
        var materializer = Create(scope);
        await materializer.MaterializeForGameAsync(GameId);
        var before = await scope.Evidence.GetForGameAsync(GameId, includeDismissed: true);
        Assert.Equal(3, before.Count);
        await scope.Evidence.UpdateNoteAsync(before.Single(r => r.SourceKey == PatternConstants.ObjEventSourceKey("DEATH", 900)).Id, "kept my note");
        await scope.Evidence.UpdateStatusAsync(before.Single(r => r.SourceKey == PatternConstants.ObjEventSourceKey("DEATH", 1000)).Id, EvidenceStatuses.Dismissed);

        // Every death moves or goes: only the untouched anchor is stale.
        var ledger = new EventCorrectionsRepository(scope.ConnectionFactory);
        var rows = await scope.GameEvents.GetEventsAsync(GameId);
        await ledger.SaveAsync(new EventCorrectionRequest(GameId, Guid.NewGuid().ToString("D"), CorrectionOps.Retime,
            Subject(rows.Single(r => r.GameTimeS == 812)), new EventPatch(null, 815, null, null), "Clock drift"));
        await ledger.SaveAsync(new EventCorrectionRequest(GameId, Guid.NewGuid().ToString("D"), CorrectionOps.Retime,
            Subject(rows.Single(r => r.GameTimeS == 900)), new EventPatch(null, 903, null, null), ""));
        await ledger.SaveAsync(new EventCorrectionRequest(GameId, Guid.NewGuid().ToString("D"), CorrectionOps.Remove,
            Subject(rows.Single(r => r.GameTimeS == 1000)), EventPatch.Empty, "Not a death"));
        await materializer.MaterializeForGameAsync(GameId);

        var after = await scope.Evidence.GetForGameAsync(GameId, includeDismissed: true);
        var keys = after.Select(r => r.SourceKey).ToList();
        Assert.Contains(PatternConstants.ObjEventSourceKey("DEATH", 815), keys);
        Assert.Contains(PatternConstants.ObjEventSourceKey("DEATH", 903), keys);
        Assert.DoesNotContain(PatternConstants.ObjEventSourceKey("DEATH", 812), keys);
        Assert.Equal("kept my note", after.Single(r => r.SourceKey == PatternConstants.ObjEventSourceKey("DEATH", 900)).Note);
        Assert.Equal(EvidenceStatuses.Dismissed, after.Single(r => r.SourceKey == PatternConstants.ObjEventSourceKey("DEATH", 1000)).Status);
        Assert.Equal(4, after.Count);

        // Idempotent.
        await materializer.MaterializeForGameAsync(GameId);
        Assert.Equal(4, (await scope.Evidence.GetForGameAsync(GameId, includeDismissed: true)).Count);
    }

    [Fact]
    public async Task Materialize_KeepsAnchorsOfTokensAnObjectiveStoppedTracking()
    {
        using var scope = new TestDatabaseScope();
        await SeedGameAsync(scope);
        var objective = await scope.Objectives.CreateAsync("Objectives and deaths", "macro");
        await scope.Objectives.SetEventTokensForObjectiveAsync(objective, new[] { GameEvent.EventTypes.Death, GameEvent.EventTypes.Dragon });
        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("DEATH", 400, "{\"killer\":\"Lee Sin\"}"), Ev("DRAGON", 1100)]);
        var materializer = Create(scope);
        await materializer.MaterializeForGameAsync(GameId);
        Assert.Equal(2, (await scope.Evidence.GetForGameAsync(GameId, includeDismissed: true)).Count);

        // The objective stops tracking deaths: the event still happens, so its anchor stays.
        await scope.Objectives.SetEventTokensForObjectiveAsync(objective, new[] { GameEvent.EventTypes.Dragon });
        await materializer.MaterializeForGameAsync(GameId);

        var after = await scope.Evidence.GetForGameAsync(GameId, includeDismissed: true);
        Assert.Equal(2, after.Count);
        Assert.Contains(after, r => r.SourceKey == PatternConstants.ObjEventSourceKey("DEATH", 400));
        Assert.Contains(after, r => r.SourceKey == PatternConstants.ObjEventSourceKey("DRAGON", 1100));

        // But an anchor of an event that is gone does go, tracked or not.
        await scope.GameEvents.SaveEventsAsync(GameId, [Ev("DRAGON", 1100)]);
        await materializer.MaterializeForGameAsync(GameId);
        Assert.Equal(PatternConstants.ObjEventSourceKey("DRAGON", 1100),
            Assert.Single(await scope.Evidence.GetForGameAsync(GameId, includeDismissed: true)).SourceKey);
    }
}
