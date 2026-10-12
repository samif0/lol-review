using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services.EventProcessing;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// GET /api/vod runs on every VOD load and every soft refresh: it reads the game's events
/// once (they feed both the timeline and the evidence inbox) and stops when the request
/// is aborted instead of finishing work nobody will read.
/// </summary>
public sealed class VodSnapshotLoadTests
{
    private const long GameId = 4_401;

    // Delegates to the real repository and counts the event reads the snapshot makes.
    private sealed class CountingEvents(IGameEventsRepository inner) : IGameEventsRepository
    {
        public int Reads { get; private set; }
        public Task SaveEventsAsync(long gameId, IReadOnlyList<GameEvent> events) => inner.SaveEventsAsync(gameId, events);
        public Task<IReadOnlyList<GameEvent>> GetEventsAsync(long gameId) { Reads++; return inner.GetEventsAsync(gameId); }
        public Task SaveProcessingReportAsync(long gameId, ProcessingReport report) => inner.SaveProcessingReportAsync(gameId, report);
        public Task<ProcessingReport?> GetProcessingReportAsync(long gameId) => inner.GetProcessingReportAsync(gameId);
        public Task<bool> HasEventsAsync(long gameId) => inner.HasEventsAsync(gameId);
        public Task<int> GetEventCountAsync(long gameId) => inner.GetEventCountAsync(gameId);
        public Task DeleteEventsAsync(long gameId) => inner.DeleteEventsAsync(gameId);
        public Task AppendEventsAsync(long gameId, IReadOnlyList<GameEvent> events) => inner.AppendEventsAsync(gameId, events);
        public Task DeleteEventsByTypeAsync(long gameId, string eventType) => inner.DeleteEventsByTypeAsync(gameId, eventType);
        public Task UpdateEventDetailsAsync(int eventId, string details) => inner.UpdateEventDetailsAsync(eventId, details);
    }

    private static async Task<(SidecarWriteScope Scope, CountingEvents Events)> SeedAsync()
    {
        var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(GameId));
        var events = new CountingEvents(new GameEventsRepository(scope.ConnectionFactory));
        await events.SaveEventsAsync(GameId,
        [
            new GameEvent { GameId = GameId, EventType = "KILL", GameTimeS = 200, Details = "{\"victim\":\"Zed\"}" },
            new GameEvent { GameId = GameId, EventType = "DEATH", GameTimeS = 300, Details = "{\"killer\":\"Zed\"}" },
        ]);
        return (scope, events);
    }

    private static VodSnapshotBuilder Builder(SidecarWriteScope scope, IGameEventsRepository events) =>
        new(scope.Games, scope.Vod, events, scope.Evidence, scope.Objectives, scope.Config, NullLogger<VodSnapshotBuilder>.Instance);

    [Fact]
    public async Task ASnapshotReadsTheGamesEventsOnce()
    {
        var (scope, events) = await SeedAsync();
        using var _ = scope;

        var vod = await Builder(scope, events).BuildAsync(GameId);

        Assert.Contains(vod.GameEvents, e => e.EventType == "KILL");
        Assert.Contains(vod.GameEvents, e => e.EventType == "DEATH");
        Assert.Equal(1, events.Reads);
    }

    [Fact]
    public async Task AnAbortedRequest_StopsTheSnapshot()
    {
        var (scope, events) = await SeedAsync();
        using var _ = scope;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Builder(scope, events).BuildAsync(GameId, new CancellationToken(canceled: true)));
        Assert.Equal(0, events.Reads);
    }

    [Fact]
    public async Task TheRouteEndsAnAbortedLoadQuietly_InsteadOfThrowingToTheErrorLog()
    {
        var (scope, events) = await SeedAsync();
        using var _ = scope;

        var result = await SidecarEndpoints.GetVodSnapshotAsync(GameId, Builder(scope, events), SidecarJson.CreateOptions(),
            new CancellationToken(canceled: true));

        Assert.Equal(499, Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, events.Reads);
    }
}
