using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data;
using Revu.Core.Services;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Objective create/update and manual game entry are one transaction each: a failing
/// side write leaves nothing half-saved, so a retry never duplicates the objective or
/// the game. Objective delete snapshots the database right before deleting.
/// Failures are injected with SQLite triggers so the real repositories run unchanged.
/// </summary>
public sealed class ObjectiveAndManualEntryTransactionTests
{
    [Fact]
    public async Task CreateObjective_SideTableFails_LeavesNoObjective()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await ExecAsync(scope, """
            CREATE TRIGGER fail_champions BEFORE INSERT ON objective_champions
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => SidecarEndpoints.CreateObjectiveAsync(
            CreateBody("Ward river", champions: ["Ahri"]), scope.Objectives, scope.Prompts, Begin(scope), NullLogger.Instance));

        Assert.Empty(await scope.Objectives.GetAllAsync());
        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM objective_prompts"));
    }

    [Fact]
    public async Task CreateObjective_PersistsEverySideTable()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var id = await SidecarEndpoints.CreateObjectiveAsync(
            CreateBody("Ward river", champions: ["Ahri"]), scope.Objectives, scope.Prompts, Begin(scope), NullLogger.Instance);

        Assert.Equal("Ward river", (await scope.Objectives.GetAsync(id))!.Title);
        Assert.Equal(["Ahri"], await scope.Objectives.GetChampionsForObjectiveAsync(id));
        Assert.Single(await scope.Prompts.GetPromptsForObjectiveAsync(id));
        Assert.Equal(["DEATH"], await scope.Objectives.GetEventTokensForObjectiveAsync(id));
    }

    [Fact]
    public async Task CreateObjective_EventTokenFailure_StillSavesTheObjective()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        await ExecAsync(scope, """
            CREATE TRIGGER fail_tokens BEFORE INSERT ON objective_event_types
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        var id = await SidecarEndpoints.CreateObjectiveAsync(
            CreateBody("Ward river", champions: ["Ahri"]), scope.Objectives, scope.Prompts, Begin(scope), NullLogger.Instance);

        Assert.NotNull(await scope.Objectives.GetAsync(id));
        Assert.Equal(["Ahri"], await scope.Objectives.GetChampionsForObjectiveAsync(id));
        Assert.Empty(await scope.Objectives.GetEventTokensForObjectiveAsync(id));
    }

    [Fact]
    public async Task UpdateObjective_SideTableFails_KeepsTheStoredObjective()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var id = await SidecarEndpoints.CreateObjectiveAsync(
            CreateBody("Ward river", champions: []), scope.Objectives, scope.Prompts, Begin(scope), NullLogger.Instance);
        await ExecAsync(scope, """
            CREATE TRIGGER fail_champions BEFORE INSERT ON objective_champions
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => SidecarEndpoints.UpdateObjectiveAsync(
            UpdateBody(id, "Renamed", champions: ["Ahri"]), scope.Objectives, scope.Prompts, Begin(scope), NullLogger.Instance));

        Assert.Equal("Ward river", (await scope.Objectives.GetAsync(id))!.Title);
        Assert.Equal("Plan the first ward", Assert.Single(await scope.Prompts.GetPromptsForObjectiveAsync(id)).Label);
    }

    [Fact]
    public async Task DeleteObjective_TakesASafetyBackupFirst()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var id = await scope.Objectives.CreateAsync("Ward river", "vision");
        var backup = new RecordingBackupService(async () => await scope.Objectives.GetAsync(id) is not null);

        await SidecarEndpoints.DeleteObjectiveAsync(id, backup, scope.Objectives);

        Assert.Equal([$"delete-objective-{id}"], backup.Reasons);
        Assert.True(backup.ObjectiveExistedAtBackup);
        Assert.Null(await scope.Objectives.GetAsync(id));
    }

    [Fact]
    public async Task ManualEntry_PracticeFails_LeavesNoGameAndNoSessionRow()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river", "vision");
        await ExecAsync(scope, """
            CREATE TRIGGER fail_practice BEFORE INSERT ON game_objectives
            BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => SidecarEndpoints.SaveManualGameAsync(
            ManualBody(objectiveId), scope.Games, scope.SessionLog, scope.Objectives, Begin(scope)));

        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM games"));
        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM session_log"));
    }

    [Fact]
    public async Task ManualEntry_SavesGameSessionRowAndPractice()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();
        var objectiveId = await scope.Objectives.CreateAsync("Ward river", "vision");

        var gameId = await SidecarEndpoints.SaveManualGameAsync(
            ManualBody(objectiveId), scope.Games, scope.SessionLog, scope.Objectives, Begin(scope));

        Assert.Equal("Ahri", (await scope.Games.GetAsync(gameId))!.ChampionName);
        Assert.Equal(7, (await scope.SessionLog.GetEntryAsync(gameId))!.MentalRating);
        Assert.True(Assert.Single(await scope.Objectives.GetGameObjectivesAsync(gameId)).Practiced);
    }

    private static Func<DbWriteSession> Begin(SidecarWriteScope scope) => () => DbWriteSession.Begin(scope.ConnectionFactory);

    private static CreateObjectiveBody CreateBody(string title, List<string> champions) => new(
        Title: title, SkillArea: "vision", Type: "primary", CompletionCriteria: "", Description: "",
        PracticePre: false, PracticeIn: true, PracticePost: false, TargetGameCount: 0,
        Prompts: [new ObjectivePromptBody(0, "pregame", "Plan the first ward")],
        Champions: champions, FocusPhaseIndex: 0, CriteriaMetricIndex: 0, CriteriaOpIndex: 0,
        CriteriaValueText: "", EventTypes: ["DEATH"]);

    private static UpdateObjectiveBody UpdateBody(long id, string title, List<string> champions) => new(
        Id: id, Title: title, SkillArea: "vision", Type: "primary", CompletionCriteria: "", Description: "",
        PracticePre: false, PracticeIn: true, PracticePost: false, TargetGameCount: 0,
        Prompts: [], Champions: champions, FocusPhaseIndex: 0, CriteriaMetricIndex: 0, CriteriaOpIndex: 0,
        CriteriaValueText: "", EventTypes: []);

    private static ManualGameBody ManualBody(long objectiveId) => new(
        ChampionName: "Ahri", Win: true, Kills: 5, Deaths: 2, Assists: 7, MentalRating: 7,
        GameMode: null, Notes: "", Mistakes: "", WentWell: "", FocusNext: "",
        Objectives: [new ManualObjectiveBody(objectiveId, true, "Warded")]);

    private static async Task<long> CountAsync(SidecarWriteScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecAsync(SidecarWriteScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class RecordingBackupService(Func<Task<bool>> objectiveExists) : IBackupService
    {
        public List<string> Reasons { get; } = [];
        public bool ObjectiveExistedAtBackup { get; private set; }

        public async Task CreateSafetyBackupAsync(string reason)
        {
            Reasons.Add(reason);
            ObjectiveExistedAtBackup = await objectiveExists();
        }

        public Task RunBackupAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync() =>
            Task.FromResult<IReadOnlyList<BackupFileInfo>>(Array.Empty<BackupFileInfo>());
        public Task<ResetResult> ResetAllDataAsync() => Task.FromResult(new ResetResult(true, "", null));
        public Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath) =>
            Task.FromResult(new RestoreResult(true, null, null));
    }
}
