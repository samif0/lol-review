using System.Text.RegularExpressions;

namespace Revu.Core.Tests;

/// <summary>
/// Guard for the counted-games invariant: every count, sum, average or grouping over the
/// games table must compose the shared filter (CountedGamesSql, or SessionLogRepository's
/// VisibleGamesFilter built from it), so hidden games and remakes never reach a number the
/// player sees. The scan reads the SQL string literals in Revu.Core and Revu.Sidecar
/// (inlining same-file string constants, so a shared CTE is checked where it is used).
/// An intentional exception goes in <see cref="Allowlist"/> with its reason.
/// </summary>
public sealed partial class CountedGamesGuardTests
{
    /// <summary>(repo-relative file, a whitespace-squashed snippet of the SQL or null for the
    /// whole file, why it may count every row).</summary>
    private static readonly (string File, string? Snippet, string Reason)[] Allowlist =
    [
        ("src/Revu.Core/Data/AppDataMigrator.cs", "SELECT COUNT(*) FROM games",
            "Row count to verify a data-folder migration, not a stat."),
        ("src/Revu.Core/Data/DatabaseIntegrityChecker.cs", "SELECT COUNT(*) FROM games",
            "Row count for the integrity report, not a stat."),
        ("src/Revu.Core/Data/LegacyDatabaseMigrationService.cs", "SELECT COUNT(*) FROM games",
            "Row count to verify a legacy import, not a stat."),
        ("src/Revu.Sidecar/RecordingLinkStore.cs", "WHERE game_id = @game",
            "Existence check for one game."),
        ("src/Revu.Core/Data/Repositories/GameRepository.History.cs", "SELECT COUNT(*) FROM games WHERE {string.Join(",
            "Match library page count: the list shows remakes (labelled), so its count includes them."),
        ("src/Revu.Core/Data/Repositories/GameRepository.History.cs", "SELECT COUNT(*) FROM games WHERE NOT (",
            "GetReviewedCountAsync: no player-facing reader."),
        ("src/Revu.Core/Data/Repositories/ObjectivesRepository.cs", "GROUP BY champion_name ORDER BY MAX(",
            "Champion picker: distinct played champions, not a stat."),
        ("src/Revu.Core/Data/Repositories/GameRepository.Analytics.cs", null,
            "Unreached analytics layer (audit data-layer-11), fixed or removed in its own change."),
    ];

    [Fact]
    public void EveryAggregateOverGames_ComposesTheCountedFilter()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var checkedAggregates = 0;
        foreach (var path in SourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            foreach (var (line, sql) in SqlLiterals(File.ReadAllText(path)))
            {
                if (!IsAggregateOverGames(sql)) continue;
                checkedAggregates++;
                if (HasCountedFilter(sql) || IsAllowed(relative, sql)) continue;
                offenders.Add($"{relative}:{line}: {Squash(sql)[..Math.Min(140, Squash(sql).Length)]}");
            }
        }

        // The scan must actually see the codebase's aggregates (a moved folder would
        // otherwise pass vacuously).
        Assert.True(checkedAggregates >= 20, $"Only {checkedAggregates} aggregates over games were scanned.");
        Assert.True(offenders.Count == 0,
            "Aggregates over games without CountedGamesSql (visible + not a remake):\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("SELECT COUNT(*) FROM games WHERE win = 1", true)]
    [InlineData("SELECT d.k, COUNT(*) FROM deaths d JOIN games g ON g.game_id = d.game_id WHERE COALESCE(g.is_hidden, 0) = 0 GROUP BY d.k", true)]
    [InlineData("SELECT COUNT(*) FROM games g WHERE {CountedGamesSql.Counted(\"g\")}", false)]
    [InlineData("SELECT * FROM games WHERE game_id = @id", false)]
    public void Detector_FlagsOnlyUnfilteredAggregates(string sql, bool flagged)
    {
        Assert.Equal(flagged, IsAggregateOverGames(sql) && !HasCountedFilter(sql));
    }

    private static bool IsAggregateOverGames(string sql) =>
        (ReadsGames().IsMatch(sql) || sql.Contains("VisibleGamesJoin", StringComparison.Ordinal))
        && Aggregates().IsMatch(sql);

    private static bool HasCountedFilter(string sql) =>
        sql.Contains("CountedGamesSql", StringComparison.Ordinal)
        || sql.Contains("VisibleGamesFilter", StringComparison.Ordinal)
        || (sql.Contains("is_hidden", StringComparison.Ordinal)
            && (sql.Contains("NotRemake", StringComparison.Ordinal) || sql.Contains("game_remakes", StringComparison.Ordinal)));

    private static bool IsAllowed(string relative, string sql) =>
        Allowlist.Any(entry => string.Equals(entry.File, relative, StringComparison.OrdinalIgnoreCase)
            && (entry.Snippet is null || Squash(sql).Contains(entry.Snippet, StringComparison.Ordinal)));

    // String literals with their line, each with same-file string constants inlined one level.
    private static IEnumerable<(int Line, string Sql)> SqlLiterals(string source)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in ConstantDeclaration().Matches(source))
        {
            constants[m.Groups["name"].Value] = LiteralBody(m.Groups["literal"].Value);
        }

        var seen = new HashSet<int>();
        foreach (Match m in Literal().Matches(source))
        {
            if (!seen.Add(m.Index)) continue;
            var body = InlinedConstant().Replace(LiteralBody(m.Value), c =>
                constants.TryGetValue(c.Groups[1].Value, out var inlined) ? inlined : c.Value);
            yield return (source.AsSpan(0, m.Index).Count('\n') + 1, body);
        }
    }

    private static string LiteralBody(string literal)
    {
        var start = literal.IndexOf('"');
        var quotes = literal.AsSpan(start).IndexOfAnyExcept('"');
        quotes = quotes < 0 ? 1 : Math.Min(quotes, 3);
        return literal.Length >= start + 2 * quotes ? literal[(start + quotes)..^quotes] : "";
    }

    private static string Squash(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static IEnumerable<string> SourceFiles(string root) =>
        new[] { "src/Revu.Core", "src/Revu.Sidecar" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Revu.Core")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [GeneratedRegex(@"\b(FROM|JOIN)\s+games\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReadsGames();

    [GeneratedRegex(@"\b(COUNT|SUM|AVG|TOTAL|GROUP_CONCAT|MIN|MAX)\s*\(|\bGROUP\s+BY\b", RegexOptions.IgnoreCase)]
    private static partial Regex Aggregates();

    // Raw (triple quote), verbatim (@) and regular string literals, optionally
    // interpolated. \x22 is a double quote, kept out of the C# literal on purpose.
    [GeneratedRegex(@"\$*\x22\x22\x22(?:.|\n)*?\x22\x22\x22|\$?@\$?\x22(?:[^\x22]|\x22\x22)*\x22|(?<![@\x22$])\$?\x22(?:[^\x22\\\r\n]|\\.)*\x22")]
    private static partial Regex Literal();

    [GeneratedRegex(@"(?:const|static\s+readonly)\s+string\s+(?<name>\w+)\s*=\s*(?<literal>\$*\x22\x22\x22(?:.|\n)*?\x22\x22\x22|\$?@\$?\x22(?:[^\x22]|\x22\x22)*\x22|\$?\x22(?:[^\x22\\\r\n]|\\.)*\x22)")]
    private static partial Regex ConstantDeclaration();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex InlinedConstant();
}
