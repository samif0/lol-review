#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>Counted games in the whole Match library: remakes and hidden games excluded.
/// Skipped games are not reviewed: they left the review queue without a review.</summary>
public sealed record GameLibraryTotals(int Games, int Wins, int Reviewed, int Skipped = 0);

public sealed partial class GameRepository
{
    // string.IsNullOrWhiteSpace for review text (GamesSnapshotBuilder.HasPersistedReview):
    // a field holding only spaces, tabs or line breaks is not a review.
    private static string HasText(string column) =>
        $"TRIM(COALESCE({column}, ''), ' ' || char(9, 10, 11, 12, 13)) != ''";

    // The per-row persisted-review test the library labels use.
    private static readonly string HasPersistedReviewSql = $@"(COALESCE(rating, 0) > 0
                                       OR {HasText("review_notes")}
                                       OR {HasText("mistakes")}
                                       OR {HasText("went_well")}
                                       OR {HasText("focus_next")}
                                       OR {HasText("spotted_problems")}
                                       OR {HasText("outside_control")}
                                       OR {HasText("within_control")}
                                       OR {HasText("attribution")}
                                       OR {HasText("personal_contribution")})";

    // Skipped from the review queue (session_log.is_skipped), the marker the queue reads.
    private const string IsSkippedSql = @"EXISTS (
                        SELECT 1 FROM session_log
                        WHERE session_log.game_id = games.game_id
                          AND COALESCE(session_log.is_skipped, 0) = 1)";

    // The Match library stat strip describes the whole history, not the loaded
    // page. Same visibility as the list (CasualFilter: ranked + manual, not
    // hidden) and the shared counted-games filter; "reviewed" is the per-row
    // persisted-review test the library labels use, and a reviewed game is never
    // also counted as skipped.
    public async Task<GameLibraryTotals> GetLibraryTotalsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN win = 1 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN {HasPersistedReviewSql} THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN NOT {HasPersistedReviewSql} AND {IsSkippedSql} THEN 1 ELSE 0 END), 0)
            FROM games
            WHERE {CountedGamesSql.Counted("games")} {CasualFilter}";

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new GameLibraryTotals(0, 0, 0);
        return new GameLibraryTotals(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    // The games among these whose review the player skipped (Match library rows).
    public async Task<IReadOnlySet<long>> GetSkippedGameIdsAsync(IReadOnlyCollection<long> gameIds)
    {
        var skipped = new HashSet<long>();
        if (gameIds.Count == 0) return skipped;

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        var placeholders = new List<string>(gameIds.Count);
        foreach (var gameId in gameIds.Distinct())
        {
            var name = $"@gameId{placeholders.Count}";
            placeholders.Add(name);
            cmd.Parameters.AddWithValue(name, gameId);
        }
        cmd.CommandText = $"""
            SELECT DISTINCT game_id
            FROM session_log
            WHERE COALESCE(is_skipped, 0) = 1
              AND game_id IN ({string.Join(", ", placeholders)})
            """;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            skipped.Add(reader.GetInt64(0));
        }
        return skipped;
    }

    // The Match library "Removed" view: games the player removed from history,
    // newest first, so any of them can be restored. The same queues as the
    // library (CasualFilter's ranked + manual), but only hidden rows.
    public async Task<List<GameStats>> GetRemovedGamesAsync(int limit = 200)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT games.*, {RemakeSql.IsRemakeColumn("games.game_id")}
            FROM games
            WHERE COALESCE(is_hidden, 0) = 1
              AND COALESCE(queue_type, '') IN ('Ranked Solo/Duo', 'Manual')
            ORDER BY timestamp DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", Math.Max(limit, 0));

        return await ReadAllGamesAsync(cmd);
    }
}
