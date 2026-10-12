#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>Counted games in the whole Match library: remakes and hidden games excluded.</summary>
public sealed record GameLibraryTotals(int Games, int Wins, int Reviewed);

public sealed partial class GameRepository
{
    // string.IsNullOrWhiteSpace for review text (GamesSnapshotBuilder.HasPersistedReview):
    // a field holding only spaces, tabs or line breaks is not a review.
    private static string HasText(string column) =>
        $"TRIM(COALESCE({column}, ''), ' ' || char(9, 10, 11, 12, 13)) != ''";

    // The Match library stat strip describes the whole history, not the loaded
    // page. Same visibility as the list (CasualFilter: ranked + manual, not
    // hidden) and the shared remake exclusion; "reviewed" is the per-row
    // persisted-review test the library labels use.
    public async Task<GameLibraryTotals> GetLibraryTotalsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN win = 1 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN COALESCE(rating, 0) > 0
                                       OR {HasText("review_notes")}
                                       OR {HasText("mistakes")}
                                       OR {HasText("went_well")}
                                       OR {HasText("focus_next")}
                                       OR {HasText("spotted_problems")}
                                       OR {HasText("outside_control")}
                                       OR {HasText("within_control")}
                                       OR {HasText("attribution")}
                                       OR {HasText("personal_contribution")}
                                     THEN 1 ELSE 0 END), 0)
            FROM games
            WHERE 1=1 {CasualFilter}
              AND {RemakeSql.NotRemake("games.game_id")}";

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new GameLibraryTotals(0, 0, 0);
        return new GameLibraryTotals(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }
}
