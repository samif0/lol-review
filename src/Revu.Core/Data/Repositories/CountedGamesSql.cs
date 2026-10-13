#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>
/// The one definition of a game that counts: visible (not soft-removed with
/// <c>is_hidden</c>) and not a remake (<see cref="RemakeSql.NotRemake"/>). Every count,
/// win rate, streak, rule and practice total over games composes this fragment; the
/// counted-games guard test fails on a new aggregate over games that skips it.
/// </summary>
internal static class CountedGamesSql
{
    /// <summary>
    /// Predicate over a joined <c>games</c> alias. For a <c>LEFT JOIN games</c> from a row
    /// that may have no game (a manual session_log row), pass the source row's game id as
    /// <paramref name="idExpr"/>: a row without a game still counts.
    /// </summary>
    public static string Counted(string gamesAlias, string? idExpr = null) =>
        $"COALESCE({gamesAlias}.is_hidden, 0) = 0 AND {RemakeSql.NotRemake(idExpr ?? $"{gamesAlias}.game_id")}";
}
