#nullable enable

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Data.Repositories;

/// <inheritdoc cref="IMatchupsRepository"/>
public sealed class MatchupsRepository : IMatchupsRepository
{
    public const int MaxNoteLength = 4000;
    public const int MaxChampionNameLength = 40;

    private readonly IDbConnectionFactory _factory;

    public MatchupsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(
        string? lane,
        IEnumerable<string?>? allyChamps,
        IEnumerable<string?>? enemyChamps,
        string? prior = null,
        string? observed = null,
        long? gameId = null,
        long? createdAt = null)
    {
        var laneValue = RequireLane(lane);
        var ally = RequireChampions(allyChamps, laneValue, "ally");
        var enemy = RequireChampions(enemyChamps, laneValue, "enemy");
        var priorText = RequireNote(prior);
        var observedText = RequireNote(observed);

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO matchups (lane, ally_champs, enemy_champs, prior, observed, game_id, created_at)
            VALUES (@lane, @ally, @enemy, @prior, @observed, @gameId, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@lane", laneValue);
        cmd.Parameters.AddWithValue("@ally", JsonSerializer.Serialize(ally));
        cmd.Parameters.AddWithValue("@enemy", JsonSerializer.Serialize(enemy));
        cmd.Parameters.AddWithValue("@prior", priorText);
        cmd.Parameters.AddWithValue("@observed", observedText);
        cmd.Parameters.AddWithValue("@gameId", gameId is > 0 ? gameId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<MatchupCard?> GetAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM matchups WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return await ReadSingleAsync(cmd);
    }

    public async Task<IReadOnlyList<MatchupCard>> GetAllAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM matchups ORDER BY created_at DESC, id DESC";
        return await ReadAllAsync(cmd);
    }

    public async Task<MatchupCard?> GetForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM matchups
            WHERE game_id = @gameId
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadSingleAsync(cmd);
    }

    public async Task<bool> UpdateAsync(
        long id,
        string? lane,
        IEnumerable<string?>? allyChamps,
        IEnumerable<string?>? enemyChamps,
        string? prior,
        string? observed)
    {
        var laneValue = RequireLane(lane);
        var ally = RequireChampions(allyChamps, laneValue, "ally");
        var enemy = RequireChampions(enemyChamps, laneValue, "enemy");
        var priorText = RequireNote(prior);
        var observedText = RequireNote(observed);

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE matchups
            SET lane = @lane,
                ally_champs = @ally,
                enemy_champs = @enemy,
                prior = @prior,
                observed = @observed
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@lane", laneValue);
        cmd.Parameters.AddWithValue("@ally", JsonSerializer.Serialize(ally));
        cmd.Parameters.AddWithValue("@enemy", JsonSerializer.Serialize(enemy));
        cmd.Parameters.AddWithValue("@prior", priorText);
        cmd.Parameters.AddWithValue("@observed", observedText);
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> UpdateNotesAsync(long id, string? prior, string? observed)
    {
        var sets = new List<string>(2);
        if (prior is not null) sets.Add("prior = @prior");
        if (observed is not null) sets.Add("observed = @observed");

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        if (sets.Count == 0)
        {
            // Nothing to change — report whether the card exists so a stale
            // client still learns the card is gone.
            cmd.CommandText = "SELECT 1 FROM matchups WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            return await cmd.ExecuteScalarAsync() is not null;
        }

        cmd.CommandText = $"UPDATE matchups SET {string.Join(", ", sets)} WHERE id = @id";
        if (prior is not null) cmd.Parameters.AddWithValue("@prior", RequireNote(prior));
        if (observed is not null) cmd.Parameters.AddWithValue("@observed", RequireNote(observed));
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM matchups WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    // ── Validation (messages are user-facing; the sidecar returns them as 400s) ──

    private static string RequireLane(string? lane) =>
        MatchupLanes.Normalize(lane)
        ?? throw new ArgumentException("Pick a lane: top, jungle, mid, bot or support.");

    private static IReadOnlyList<string> RequireChampions(IEnumerable<string?>? champions, string lane, string side)
    {
        var names = MatchupLanes.NormalizeChampions(champions);
        var slots = MatchupLanes.ChampSlots(lane);
        if (names.Count == 0)
            throw new ArgumentException($"Add at least one {side} champion.");
        if (names.Count > slots)
        {
            throw new ArgumentException(slots == 1
                ? $"{MatchupLanes.Label(lane)} is a 1v1: one {side} champion."
                : $"{MatchupLanes.Label(lane)} is a 2v2: at most two {side} champions.");
        }
        if (names.Any(n => n.Length > MaxChampionNameLength))
            throw new ArgumentException($"Champion names must be under {MaxChampionNameLength} characters.");
        return names;
    }

    private static string RequireNote(string? note)
    {
        var text = (note ?? "").Trim();
        if (text.Length > MaxNoteLength)
            throw new ArgumentException($"Keep each note under {MaxNoteLength:N0} characters.");
        return text;
    }

    // ── Row mapping ──────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<MatchupCard>> ReadAllAsync(SqliteCommand cmd)
    {
        var results = new List<MatchupCard>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadRow(reader));
        }
        return results;
    }

    private static async Task<MatchupCard?> ReadSingleAsync(SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadRow(reader) : null;
    }

    private static MatchupCard ReadRow(SqliteDataReader reader)
    {
        return new MatchupCard(
            Id: reader.GetInt64(reader.GetOrdinal("id")),
            Lane: reader.IsDBNull(reader.GetOrdinal("lane")) ? "" : reader.GetString(reader.GetOrdinal("lane")),
            AllyChamps: ParseChampions(reader, "ally_champs"),
            EnemyChamps: ParseChampions(reader, "enemy_champs"),
            Prior: reader.IsDBNull(reader.GetOrdinal("prior")) ? "" : reader.GetString(reader.GetOrdinal("prior")),
            Observed: reader.IsDBNull(reader.GetOrdinal("observed")) ? "" : reader.GetString(reader.GetOrdinal("observed")),
            GameId: reader.IsDBNull(reader.GetOrdinal("game_id")) ? null : reader.GetInt64(reader.GetOrdinal("game_id")),
            CreatedAt: reader.IsDBNull(reader.GetOrdinal("created_at")) ? 0 : reader.GetInt64(reader.GetOrdinal("created_at")));
    }

    /// <summary>JSON array of names → list; a malformed cell reads as empty rather than throwing.</summary>
    private static IReadOnlyList<string> ParseChampions(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return [];
        var json = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var names = JsonSerializer.Deserialize<List<string?>>(json);
            return names is null ? [] : names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
