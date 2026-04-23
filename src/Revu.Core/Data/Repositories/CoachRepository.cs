#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// Read-only access from C# to the new coach-rebuild tables written by the
/// Python sidecar. Introduced in phase 1 (game_summary) and extended in
/// phases 2-5 as new tables come online.
///
/// C# never writes these tables — the Python sidecar owns them.
/// </summary>
public interface ICoachRepository
{
    Task<CoachGameSummaryRecord?> GetGameSummaryAsync(long gameId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CoachSignalRankingRecord>> GetTopSignalsAsync(int limit = 10, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CoachConceptProfileRecord>> GetTopConceptsAsync(int limit = 20, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CoachSessionRecord>> GetRecentCoachSessionsAsync(int limit = 20, CancellationToken cancellationToken = default);
}

public record CoachGameSummaryRecord(
    long GameId,
    string CompactedJson,
    string? WinProbabilityTimelineJson,
    string? KeyEventsJson,
    int SummaryVersion,
    int CreatedAt,
    int? TokenCount);

public record CoachSignalRankingRecord(
    string FeatureName,
    double SpearmanRho,
    double? PartialRhoMentalControlled,
    double CiLow,
    double CiHigh,
    int SampleSize,
    bool Stable,
    bool DriftFlag,
    double? UserBaselineWinAvg,
    double? UserBaselineLossAvg,
    int Rank);

public record CoachConceptProfileRecord(
    string ConceptCanonical,
    int Frequency,
    double RecencyWeightedFrequency,
    int PositiveCount,
    int NegativeCount,
    int NeutralCount,
    double? WinCorrelation,
    int LastSeenAt,
    int Rank);

public record CoachSessionRecord(
    long Id,
    string Mode,
    string ResponseText,
    string? ResponseJson,
    string ModelName,
    string Provider,
    int? LatencyMs,
    int CreatedAt);

public sealed class CoachRepository : ICoachRepository
{
    private readonly IDbConnectionFactory _factory;

    public CoachRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<CoachGameSummaryRecord?> GetGameSummaryAsync(long gameId, CancellationToken cancellationToken = default)
    {
        using var conn = _factory.CreateConnection();
        if (!await TableExistsAsync(conn, "game_summary", cancellationToken))
            return null;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT game_id, compacted_json, win_probability_timeline_json,
                   key_events_json, summary_version, created_at, token_count
            FROM game_summary WHERE game_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", gameId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new CoachGameSummaryRecord(
            GameId: reader.GetInt64(0),
            CompactedJson: reader.GetString(1),
            WinProbabilityTimelineJson: reader.IsDBNull(2) ? null : reader.GetString(2),
            KeyEventsJson: reader.IsDBNull(3) ? null : reader.GetString(3),
            SummaryVersion: reader.GetInt32(4),
            CreatedAt: reader.GetInt32(5),
            TokenCount: reader.IsDBNull(6) ? null : reader.GetInt32(6));
    }

    public async Task<IReadOnlyList<CoachSignalRankingRecord>> GetTopSignalsAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        var results = new List<CoachSignalRankingRecord>();
        using var conn = _factory.CreateConnection();
        if (!await TableExistsAsync(conn, "user_signal_ranking", cancellationToken))
            return results;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT feature_name, spearman_rho, partial_rho_mental_controlled,
                   ci_low, ci_high, sample_size, stable, drift_flag,
                   user_baseline_win_avg, user_baseline_loss_avg, rank
            FROM user_signal_ranking
            WHERE stable = 1
            ORDER BY rank ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CoachSignalRankingRecord(
                FeatureName: reader.GetString(0),
                SpearmanRho: reader.GetDouble(1),
                PartialRhoMentalControlled: reader.IsDBNull(2) ? null : reader.GetDouble(2),
                CiLow: reader.GetDouble(3),
                CiHigh: reader.GetDouble(4),
                SampleSize: reader.GetInt32(5),
                Stable: reader.GetInt32(6) != 0,
                DriftFlag: reader.GetInt32(7) != 0,
                UserBaselineWinAvg: reader.IsDBNull(8) ? null : reader.GetDouble(8),
                UserBaselineLossAvg: reader.IsDBNull(9) ? null : reader.GetDouble(9),
                Rank: reader.GetInt32(10)));
        }
        return results;
    }

    public async Task<IReadOnlyList<CoachConceptProfileRecord>> GetTopConceptsAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        var results = new List<CoachConceptProfileRecord>();
        using var conn = _factory.CreateConnection();
        if (!await TableExistsAsync(conn, "user_concept_profile", cancellationToken))
            return results;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT concept_canonical, frequency, recency_weighted_frequency,
                   positive_count, negative_count, neutral_count,
                   win_correlation, last_seen_at, rank
            FROM user_concept_profile
            ORDER BY rank ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CoachConceptProfileRecord(
                ConceptCanonical: reader.GetString(0),
                Frequency: reader.GetInt32(1),
                RecencyWeightedFrequency: reader.GetDouble(2),
                PositiveCount: reader.GetInt32(3),
                NegativeCount: reader.GetInt32(4),
                NeutralCount: reader.GetInt32(5),
                WinCorrelation: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                LastSeenAt: reader.GetInt32(7),
                Rank: reader.GetInt32(8)));
        }
        return results;
    }

    public async Task<IReadOnlyList<CoachSessionRecord>> GetRecentCoachSessionsAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        var results = new List<CoachSessionRecord>();
        using var conn = _factory.CreateConnection();
        if (!await TableExistsAsync(conn, "coach_sessions", cancellationToken))
            return results;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, mode, response_text, response_json,
                   model_name, provider, latency_ms, created_at
            FROM coach_sessions
            ORDER BY created_at DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CoachSessionRecord(
                Id: reader.GetInt64(0),
                Mode: reader.GetString(1),
                ResponseText: reader.GetString(2),
                ResponseJson: reader.IsDBNull(3) ? null : reader.GetString(3),
                ModelName: reader.GetString(4),
                Provider: reader.GetString(5),
                LatencyMs: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                CreatedAt: reader.GetInt32(7)));
        }
        return results;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@t";
        cmd.Parameters.AddWithValue("@t", tableName);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }
}
