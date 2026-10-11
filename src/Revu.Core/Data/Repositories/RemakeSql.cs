#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// 3.14.1: shared game_remakes SQL. A remake (League's early surrender) stays saved and
/// reviewable but never counts: every rule, count, win-rate and review-queue read joins
/// <see cref="NotRemake"/>. The table is created by <see cref="Schema.AllCreateStatements"/>
/// without a schema version bump (the clip_narrations precedent), so writers tolerate a
/// database that does not have it yet.
/// </summary>
internal static class RemakeSql
{
    public const string Table = "game_remakes";

    public const string SourceEog = "eog";
    public const string SourceLcu = "lcu";
    public const string SourceMatchV5 = "matchv5";
    public const string SourceUser = "user";

    /// <summary>Every available source was asked and none carried the flag. Rank 0 and
    /// is_remake 0: it only stops the startup sweep from asking again (it never excludes a
    /// game), and any real verdict replaces it.</summary>
    public const string SourceUnknown = "unknown";

    /// <summary>Source precedence: a write applies only when its rank is at least the stored
    /// rank, so the player's own verdict is sticky and Match-V5 corrects a capture read.</summary>
    public static int Rank(string? source) => source switch
    {
        SourceUser => 3,
        SourceMatchV5 => 2,
        SourceEog or SourceLcu => 1,
        _ => 0,
    };

    /// <summary>SQL twin of <see cref="Rank"/>; format with the source expression.</summary>
    public const string RankSql =
        "CASE {0} WHEN 'user' THEN 3 WHEN 'matchv5' THEN 2 WHEN 'eog' THEN 1 WHEN 'lcu' THEN 1 ELSE 0 END";

    public static bool IsKnownSource(string? source) => Rank(source) > 0 || source == SourceUnknown;

    /// <summary>The one shared exclusion fragment. <paramref name="idExpr"/> is a
    /// games.game_id-valued expression; a NULL id (a manual session_log row) passes.</summary>
    public static string NotRemake(string idExpr) =>
        $"NOT EXISTS (SELECT 1 FROM game_remakes rmk WHERE rmk.game_id = {idExpr} AND rmk.is_remake = 1)";

    /// <summary>An <c>is_remake</c> select column for read models that label remakes.</summary>
    public static string IsRemakeColumn(string idExpr) =>
        $"EXISTS (SELECT 1 FROM game_remakes rmk WHERE rmk.game_id = {idExpr} AND rmk.is_remake = 1) AS is_remake";

    public static bool IsMissingTable(SqliteException ex) =>
        ex.SqliteErrorCode == 1
        && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
        && ex.Message.Contains(Table, StringComparison.OrdinalIgnoreCase);

    /// <summary>Create the table (same DDL AllCreateStatements runs at startup).</summary>
    public static async Task EnsureTableAsync(SqliteConnection conn, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = Schema.CreateGameRemakesTable;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
