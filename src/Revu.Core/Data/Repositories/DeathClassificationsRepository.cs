#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// v2.18 (schema v5): the death-audit taxonomy. Six coarse causes — broad
/// enough that every death fits one, narrow enough that the mix over a block
/// of games is diagnostic ("44% of your deaths are vision-class").
/// Keys are persisted in death_classifications.death_class — never rename.
/// </summary>
public static class DeathClasses
{
    public const string Greed = "greed";
    public const string Vision = "vision";
    public const string Wave = "wave";
    public const string Cooldowns = "cooldowns";
    public const string Outnumbered = "outnumbered";
    public const string Tempo = "tempo";

    /// <summary>(Key, short chip label, one-line meaning) in display order.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Hint)> All =
    [
        (Greed, "GREED", "Overstayed — wave, tower dive, one more trade"),
        (Vision, "VISION", "No ward coverage where the threat came from"),
        (Wave, "WAVE", "Overextended for the wave state"),
        (Cooldowns, "COOLDOWNS", "Fought without key spells (yours or theirs)"),
        (Outnumbered, "NUMBERS", "Took a fight down bodies"),
        (Tempo, "TEMPO", "Shouldn't have been there at all"),
    ];

    public static string LabelFor(string? key)
    {
        foreach (var entry in All)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Label;
            }
        }
        return "";
    }
}

public sealed record DeathClassificationRecord(
    long Id,
    long GameId,
    int GameTimeSeconds,
    string DeathClass,
    long? CreatedAt);

/// <summary>One classified-death bucket in the cross-game mix.</summary>
public sealed record DeathMixEntry(string DeathClass, int Count);

public interface IDeathClassificationsRepository
{
    /// <summary>Set (or change) the class for one death, keyed on (game, second).</summary>
    Task UpsertAsync(long gameId, int gameTimeSeconds, string deathClass);

    /// <summary>Clear a classification back to unclassified.</summary>
    Task ClearAsync(long gameId, int gameTimeSeconds);

    Task<IReadOnlyList<DeathClassificationRecord>> GetForGameAsync(long gameId);

    /// <summary>
    /// Class counts across visible games in the last <paramref name="days"/>,
    /// largest bucket first.
    /// </summary>
    Task<IReadOnlyList<DeathMixEntry>> GetClassMixAsync(int days);

    /// <summary>Total DEATH events across visible games in the window (for "n of m classified").</summary>
    Task<int> GetDeathEventCountAsync(int days);
}

public sealed class DeathClassificationsRepository : IDeathClassificationsRepository
{
    private readonly IDbConnectionFactory _factory;

    public DeathClassificationsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task UpsertAsync(long gameId, int gameTimeSeconds, string deathClass)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO death_classifications (game_id, game_time_s, death_class, created_at)
            VALUES (@gameId, @gameTimeS, @deathClass, @createdAt)
            ON CONFLICT(game_id, game_time_s) DO UPDATE SET death_class = excluded.death_class
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@gameTimeS", gameTimeSeconds);
        cmd.Parameters.AddWithValue("@deathClass", deathClass?.Trim().ToLowerInvariant() ?? "");
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearAsync(long gameId, int gameTimeSeconds)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM death_classifications
            WHERE game_id = @gameId AND game_time_s = @gameTimeS
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@gameTimeS", gameTimeSeconds);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<DeathClassificationRecord>> GetForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, game_id, game_time_s, death_class, created_at
            FROM death_classifications
            WHERE game_id = @gameId
            ORDER BY game_time_s ASC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);

        var results = new List<DeathClassificationRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DeathClassificationRecord(
                Id: reader.GetInt64(0),
                GameId: reader.GetInt64(1),
                GameTimeSeconds: reader.GetInt32(2),
                DeathClass: reader.IsDBNull(3) ? "" : reader.GetString(3),
                CreatedAt: reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        }
        return results;
    }

    public async Task<IReadOnlyList<DeathMixEntry>> GetClassMixAsync(int days)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT dc.death_class, COUNT(*)
            FROM death_classifications dc
            JOIN games g ON g.game_id = dc.game_id
            WHERE COALESCE(g.is_hidden, 0) = 0
              AND g.timestamp >= @cutoff
              AND dc.death_class != ''
            GROUP BY dc.death_class
            ORDER BY COUNT(*) DESC, dc.death_class ASC
            """;
        cmd.Parameters.AddWithValue("@cutoff", CutoffFor(days));

        var results = new List<DeathMixEntry>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DeathMixEntry(
                DeathClass: reader.GetString(0),
                Count: reader.GetInt32(1)));
        }
        return results;
    }

    public async Task<int> GetDeathEventCountAsync(int days)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM game_events ge
            JOIN games g ON g.game_id = ge.game_id
            WHERE ge.event_type = 'DEATH'
              AND COALESCE(g.is_hidden, 0) = 0
              AND g.timestamp >= @cutoff
            """;
        cmd.Parameters.AddWithValue("@cutoff", CutoffFor(days));
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0);
    }

    private static long CutoffFor(int days)
        => DateTimeOffset.UtcNow.AddDays(-Math.Max(1, days)).ToUnixTimeSeconds();
}
