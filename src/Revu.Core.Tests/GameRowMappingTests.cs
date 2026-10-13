using System.Text.RegularExpressions;

namespace Revu.Core.Tests;

/// <summary>
/// GameRepository's row mapper reads an absent column as its default (older databases,
/// reads that skip a computed column), so a misspelled column name would silently read
/// as 0 or "". Pin every name it reads to a real games column.
/// </summary>
public sealed partial class GameRowMappingTests
{
    // Computed per read (RemakeSql.IsRemakeColumn), not stored on games.
    private static readonly HashSet<string> ComputedColumns = new(StringComparer.Ordinal) { "is_remake" };

    [Fact]
    public async Task EveryColumnTheMapperReads_ExistsOnTheGamesTable()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var schemaColumns = new HashSet<string>(StringComparer.Ordinal);
        using (var conn = scope.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM pragma_table_info('games')";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) schemaColumns.Add(reader.GetString(0));
        }

        var source = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "src", "Revu.Core", "Data", "Repositories", "GameRepository.cs"));
        var read = MapperRead().Matches(source).Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.True(read.Count >= 60, $"Only {read.Count} mapped columns found; the scan is out of date.");
        var unknown = read.Where(column => !schemaColumns.Contains(column) && !ComputedColumns.Contains(column)).ToList();
        Assert.True(unknown.Count == 0, "The mapper reads columns the games table does not have: " + string.Join(", ", unknown));
    }

    [Fact]
    public async Task ARowWithoutAComputedColumn_ReadsItsDefault()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8601, champion: "Ahri"));

        // GetAsync's select may not carry is_remake; the mapper still returns the row.
        var game = await scope.Games.GetAsync(8601);

        Assert.NotNull(game);
        Assert.Equal("Ahri", game!.ChampionName);
        Assert.Equal(7.0, game.CsPerMin);
        Assert.False(game.IsRemake);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Revu.Core")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [GeneratedRegex(@"(?:Get(?:Int|Long|Double|String)OrDefault|GetNullable(?:Double|Int))\(reader, \x22(\w+)\x22\)")]
    private static partial Regex MapperRead();
}
