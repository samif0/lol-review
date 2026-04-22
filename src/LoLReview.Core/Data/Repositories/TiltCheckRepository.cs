#nullable enable

using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>Stores and queries tilt check exercise results.</summary>
public sealed class TiltCheckRepository : ITiltCheckRepository
{
    private readonly IDbConnectionFactory _factory;

    public TiltCheckRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> SaveAsync(
        string emotion,
        int intensityBefore,
        int? intensityAfter = null,
        string reframeThought = "",
        string reframeResponse = "",
        string thoughtType = "",
        string cueWord = "",
        string focusIntention = "",
        long? gameId = null,
        string ifThenPlan = "")
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tilt_checks
                (emotion, intensity_before, intensity_after,
                 reframe_thought, reframe_response, thought_type,
                 cue_word, focus_intention, game_id, if_then_plan, created_at)
            VALUES (@emotion, @intensityBefore, @intensityAfter,
                    @reframeThought, @reframeResponse, @thoughtType,
                    @cueWord, @focusIntention, @gameId, @ifThenPlan, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@emotion", emotion);
        cmd.Parameters.AddWithValue("@intensityBefore", intensityBefore);
        cmd.Parameters.AddWithValue("@intensityAfter",
            intensityAfter.HasValue ? intensityAfter.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@reframeThought", reframeThought);
        cmd.Parameters.AddWithValue("@reframeResponse", reframeResponse);
        cmd.Parameters.AddWithValue("@thoughtType", thoughtType);
        cmd.Parameters.AddWithValue("@cueWord", cueWord);
        cmd.Parameters.AddWithValue("@focusIntention", focusIntention);
        cmd.Parameters.AddWithValue("@gameId",
            gameId.HasValue ? gameId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@ifThenPlan", ifThenPlan);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetRecentAsync(int limit = 20)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM tilt_checks
            ORDER BY created_at DESC LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<TiltCheckStats> GetStatsAsync()
    {
        using var conn = _factory.CreateConnection();

        // Aggregate stats (only where intensity_after is not null)
        int total = 0;
        double avgBefore = 0;
        double avgAfter = 0;
        double avgReduction = 0;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    COUNT(*) as total,
                    AVG(intensity_before) as avg_before,
                    AVG(intensity_after) as avg_after,
                    AVG(intensity_before - intensity_after) as avg_reduction
                FROM tilt_checks
                WHERE intensity_after IS NOT NULL
                """;

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                total = reader.IsDBNull(reader.GetOrdinal("total")) ? 0 : reader.GetInt32(reader.GetOrdinal("total"));

                var avgBeforeOrd = reader.GetOrdinal("avg_before");
                avgBefore = reader.IsDBNull(avgBeforeOrd) ? 0 : Math.Round(reader.GetDouble(avgBeforeOrd), 1);

                var avgAfterOrd = reader.GetOrdinal("avg_after");
                avgAfter = reader.IsDBNull(avgAfterOrd) ? 0 : Math.Round(reader.GetDouble(avgAfterOrd), 1);

                var avgRedOrd = reader.GetOrdinal("avg_reduction");
                avgReduction = reader.IsDBNull(avgRedOrd) ? 0 : Math.Round(reader.GetDouble(avgRedOrd), 1);
            }
        }

        // Top emotions
        var emotions = new List<EmotionCount>();
        using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = """
                SELECT emotion, COUNT(*) as cnt
                FROM tilt_checks
                GROUP BY emotion
                ORDER BY cnt DESC
                """;

            using var reader2 = await cmd2.ExecuteReaderAsync();
            while (await reader2.ReadAsync())
            {
                emotions.Add(new EmotionCount(
                    Emotion: reader2.GetString(reader2.GetOrdinal("emotion")),
                    Count: reader2.GetInt32(reader2.GetOrdinal("cnt"))));
            }
        }

        return new TiltCheckStats(total, avgBefore, avgAfter, avgReduction, emotions);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadAllRowsAsync(SqliteCommand cmd)
    {
        var results = new List<Dictionary<string, object?>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadRow(reader));
        }
        return results;
    }

    private static Dictionary<string, object?> ReadRow(SqliteDataReader reader)
    {
        var dict = new Dictionary<string, object?>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return dict;
    }
}
