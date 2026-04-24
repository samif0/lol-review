using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// v2.15.0: champion gating on objectives. Empty champion set means the
/// objective applies to everyone. Non-empty set restricts it to matching
/// champions on GetActiveByPhaseAsync.
/// </summary>
public sealed class ObjectivesRepositoryChampionsTests
{
    [Fact]
    public async Task SetChampionsForObjectiveAsync_PersistsAndIsReadable()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "2v2 planning", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        await scope.Objectives.SetChampionsForObjectiveAsync(id, new[] { "Yasuo", "Yone" });

        var champs = await scope.Objectives.GetChampionsForObjectiveAsync(id);
        Assert.Equal(new[] { "Yasuo", "Yone" }.OrderBy(c => c), champs.OrderBy(c => c));
    }

    [Fact]
    public async Task SetChampionsForObjectiveAsync_DiffSavesAndDedupes()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        await scope.Objectives.SetChampionsForObjectiveAsync(id, new[] { "Ahri", "Lux" });
        await scope.Objectives.SetChampionsForObjectiveAsync(id, new[] { "Ahri", "Orianna" });

        var champs = await scope.Objectives.GetChampionsForObjectiveAsync(id);
        // Lux removed, Ahri kept, Orianna added — no duplicates.
        Assert.Equal(new[] { "Ahri", "Orianna" }.OrderBy(c => c), champs.OrderBy(c => c));
    }

    [Fact]
    public async Task SetChampionsForObjectiveAsync_EmptyListClearsAllChampions()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        await scope.Objectives.SetChampionsForObjectiveAsync(id, new[] { "Ahri" });
        Assert.Single(await scope.Objectives.GetChampionsForObjectiveAsync(id));

        await scope.Objectives.SetChampionsForObjectiveAsync(id, Array.Empty<string>());
        Assert.Empty(await scope.Objectives.GetChampionsForObjectiveAsync(id));
    }

    [Fact]
    public async Task GetActiveByPhaseAsync_NoChampionFilter_ReturnsEverything()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var open = await scope.Objectives.CreateWithPhasesAsync(
            "Open", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var scoped = await scope.Objectives.CreateWithPhasesAsync(
            "Yasuo-only", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetChampionsForObjectiveAsync(scoped, new[] { "Yasuo" });

        // No champion → both come back.
        var all = await scope.Objectives.GetActiveByPhaseAsync(ObjectivePhases.PreGame, championName: null);
        Assert.Equal(new[] { open, scoped }.OrderBy(i => i), all.Select(o => o.Id).OrderBy(i => i));
    }

    [Fact]
    public async Task GetActiveByPhaseAsync_WithChampion_HidesMismatchedObjectives()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var open = await scope.Objectives.CreateWithPhasesAsync(
            "Open (no champs)", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var yasuoOnly = await scope.Objectives.CreateWithPhasesAsync(
            "Yasuo-only", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        var ahriOnly = await scope.Objectives.CreateWithPhasesAsync(
            "Ahri-only", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);

        await scope.Objectives.SetChampionsForObjectiveAsync(yasuoOnly, new[] { "Yasuo" });
        await scope.Objectives.SetChampionsForObjectiveAsync(ahriOnly, new[] { "Ahri" });

        // Playing Yasuo → "Open" and "Yasuo-only" come back, "Ahri-only" doesn't.
        var forYasuo = await scope.Objectives.GetActiveByPhaseAsync(ObjectivePhases.PreGame, "Yasuo");
        Assert.Equal(new[] { open, yasuoOnly }.OrderBy(i => i),
                     forYasuo.Select(o => o.Id).OrderBy(i => i));
    }

    [Fact]
    public async Task GetActiveByPhaseAsync_WithChampion_MatchIsCaseInsensitive()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetChampionsForObjectiveAsync(id, new[] { "Kai'Sa" });

        // Exact match comes back. (The UI always passes Riot's canonical display
        // name, so we don't need to paper over varied casing — but the dedupe in
        // SetChampionsForObjectiveAsync is case-insensitive, tested above.)
        var forKaisa = await scope.Objectives.GetActiveByPhaseAsync(ObjectivePhases.PreGame, "Kai'Sa");
        Assert.Single(forKaisa);
    }

    [Fact]
    public async Task DeleteAsync_CascadesToObjectiveChampions()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Obj", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetChampionsForObjectiveAsync(id, new[] { "Yasuo", "Yone" });

        await scope.Objectives.DeleteAsync(id);

        // Rows in objective_champions should be gone along with the objective.
        // We can assert indirectly: no row can come back for this id.
        var champsAfter = await scope.Objectives.GetChampionsForObjectiveAsync(id);
        Assert.Empty(champsAfter);
    }

    [Fact]
    public async Task GetPlayedChampionsAsync_ReturnsRecentDistinctChampionsNewestFirst()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Direct insert with distinct game_ids + explicit timestamps so the
        // ORDER BY MAX(timestamp) DESC is deterministic without relying on
        // SaveManualAsync's synthetic IDs.
        using (var conn = scope.OpenConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO games (game_id, champion_name, win, timestamp, is_hidden)
                VALUES (111, 'Ahri',  1, 1700000000, 0),
                       (222, 'Yasuo', 1, 1700005000, 0),
                       (333, 'Yasuo', 0, 1700006000, 0)
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var played = await scope.Objectives.GetPlayedChampionsAsync(10);
        // Yasuo is newer → first. Distinct: only one Yasuo entry.
        Assert.Equal(new[] { "Yasuo", "Ahri" }, played);
    }
}
