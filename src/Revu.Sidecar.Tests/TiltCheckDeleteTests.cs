using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Revu.Sidecar;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// P-042: POST /api/tiltcheck/delete. The route hands its handler the write graph's
/// repository and backup guard, so these tests call the handler over the write-capable
/// factory and read back through the read-only factory, as the sidecar does (no HTTP host,
/// house style). After a delete, the snapshots behind GET /api/tiltcheck and GET /api/pregame
/// read only the rows that remain: the page's reload shows the new recent list, stats and
/// latest plan, and the pregame intent card drops the deleted plan.
/// </summary>
public sealed class TiltCheckDeleteTests
{
    private const string CalmPlan = "If I fall two levels behind, then I farm for 60 seconds.";
    private const string StrayPlan = "If I lose, then I queue again right away.";
    private const string PregamePlan = "If I start to flame, then I mute all.";

    [Fact]
    public async Task Delete_RemovesTheRow_AndTheStatsAndLatestPlanRecompute()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var (write, read) = Repositories(scope);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var calm = await SaveAtAsync(scope, write, now - 7200, "anxious", 6, 3, CalmPlan);
        var stray = await SaveAtAsync(scope, write, now - 3600, "frustrated", 10, 2, StrayPlan);

        var before = await TiltCheckAsync(read);
        Assert.Equal(new[] { stray, calm }, before.Recent.Select(r => r.Id));
        Assert.Equal(2, before.Stats.Total);
        Assert.Equal(8.0, before.Stats.AvgBefore);
        Assert.Equal(StrayPlan, before.LatestPlan);
        Assert.Equal(StrayPlan, (await PreGameAsync(scope, read)).ActivePlan);

        var backups = 0;
        var (status, body) = Read(await SidecarEndpoints.DeleteTiltCheckAsync(new TiltCheckIdBody(stray), write,
            () => { backups++; return Task.CompletedTask; }, SidecarJson.CreateOptions(), NullLogger.Instance));

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(1, backups);
        var after = await TiltCheckAsync(read);
        Assert.Equal(new[] { calm }, after.Recent.Select(r => r.Id));
        Assert.True(after.HasRecent);
        Assert.Equal(1, after.Stats.Total);
        Assert.Equal(6.0, after.Stats.AvgBefore);
        Assert.Equal(3.0, after.Stats.AvgAfter);
        Assert.Equal(3.0, after.Stats.AvgReduction);
        Assert.Equal(new[] { "anxious" }, after.Stats.TopEmotions.Select(e => e.Emotion));
        Assert.Equal(CalmPlan, after.LatestPlan);
        Assert.Equal(CalmPlan, (await PreGameAsync(scope, read)).ActivePlan);
    }

    [Fact]
    public async Task DeletingAPreQueuePlanRow_ClearsThatPlanFromTheTiltPageAndThePregameCard()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var (write, read) = Repositories(scope);
        // The row POST /api/pregame/ifthen writes.
        var plan = await write.SaveAsync(emotion: "pregame_plan", intensityBefore: 0, ifThenPlan: PregamePlan);
        Assert.Equal(PregamePlan, (await PreGameAsync(scope, read)).ActivePlan);

        var (status, _) = Read(await Delete(write, plan));

        Assert.Equal(200, status);
        var after = await TiltCheckAsync(read);
        Assert.Empty(after.Recent);
        Assert.False(after.HasRecent);
        Assert.Equal(0, after.Stats.Total);
        Assert.Equal("", after.Stats.AvgReductionText);
        Assert.Empty(after.Stats.TopEmotions);
        Assert.Null(after.LatestPlan);
        Assert.False(after.HasLatestPlan);
        Assert.Null((await PreGameAsync(scope, read)).ActivePlan);
    }

    [Fact]
    public async Task UnknownId_AnswersOk_AndChangesNothing_LikeTheOtherDeletes()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var (write, read) = Repositories(scope);
        var id = await write.SaveAsync("tense", 7, 4, ifThenPlan: CalmPlan);

        var (status, body) = Read(await Delete(write, id + 1000));

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("ok").GetBoolean());
        var after = await TiltCheckAsync(read);
        Assert.Equal(new[] { id }, after.Recent.Select(r => r.Id));
        Assert.Equal(1, after.Stats.Total);
        Assert.Equal(CalmPlan, after.LatestPlan);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    [InlineData(null)]
    public async Task AMissingId_Is400_BeforeTheBackupOrTheDatabase(long? id)
    {
        // A scope with no schema: any database call would throw.
        using var scope = new SidecarWriteScope();
        var backups = 0;
        var body = id is { } value ? new TiltCheckIdBody(value) : null;

        var (status, reply) = Read(await SidecarEndpoints.DeleteTiltCheckAsync(body!, new TiltCheckRepository(scope.ConnectionFactory),
            () => { backups++; return Task.CompletedTask; }, SidecarJson.CreateOptions(), NullLogger.Instance));

        Assert.Equal(400, status);
        Assert.Equal("id required", reply.GetProperty("error").GetString());
        Assert.Equal(0, backups);
    }

    // The route's two graphs: writes through the write-capable factory, reads through the
    // physically read-only one, both on the scope's database file.
    private static (TiltCheckRepository Write, TiltCheckRepository Read) Repositories(SidecarWriteScope scope) => (
        new TiltCheckRepository(new WriteSqliteConnectionFactory(
            NullLogger<WriteSqliteConnectionFactory>.Instance, dbPath: scope.DatabasePath)),
        new TiltCheckRepository(new ReadOnlySqliteConnectionFactory(
            NullLogger<ReadOnlySqliteConnectionFactory>.Instance, scope.DatabasePath)));

    private static Task<IResult> Delete(ITiltCheckRepository write, long id) =>
        SidecarEndpoints.DeleteTiltCheckAsync(new TiltCheckIdBody(id), write, () => Task.CompletedTask,
            SidecarJson.CreateOptions(), NullLogger.Instance);

    // created_at has one-second resolution, so each row gets an explicit, distinct time.
    private static async Task<long> SaveAtAsync(SidecarWriteScope scope, ITiltCheckRepository write, long createdAt,
        string emotion, int before, int after, string plan)
    {
        var id = await write.SaveAsync(emotion, before, after, ifThenPlan: plan);
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tilt_checks SET created_at = @createdAt WHERE id = @id";
        cmd.Parameters.AddWithValue("@createdAt", createdAt);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static Task<TiltCheckDto> TiltCheckAsync(ITiltCheckRepository read) =>
        new TiltCheckSnapshotBuilder(read, NullLogger<TiltCheckSnapshotBuilder>.Instance).BuildAsync();

    // The builder behind GET /api/pregame, with the intel deck's champion lookup stubbed out.
    private static Task<PreGameDto> PreGameAsync(SidecarWriteScope scope, ITiltCheckRepository read)
    {
        var intel = new PreGameIntelService(scope.Objectives, scope.SessionLog, scope.Games, scope.MatchupNotes,
            scope.Prompts, new NoChampionData(), NullLogger<PreGameIntelService>.Instance);
        var builder = new PreGameSnapshotBuilder(scope.Games, scope.Objectives, scope.SessionLog, scope.MatchupNotes,
            scope.Prompts, read, scope.Config, intel, new LcuLiveState(), NullLogger<PreGameSnapshotBuilder>.Instance);
        return builder.BuildAsync(null, null, null, null);
    }

    private static (int Status, JsonElement Body) Read(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return (status, JsonSerializer.SerializeToElement(value, SidecarJson.CreateOptions()));
    }

    private sealed class NoChampionData : IRiotChampionDataClient
    {
        public Task<int> ResolveChampionIdAsync(string displayNameOrAlias, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<ChampionAbilities?> GetChampionAbilitiesAsync(int championId, CancellationToken ct = default)
            => Task.FromResult<ChampionAbilities?>(null);
    }
}
