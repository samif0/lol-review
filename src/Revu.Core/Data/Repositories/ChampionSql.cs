#nullable enable

using Microsoft.Data.Sqlite;
using Revu.Core.Constants;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// Champion filters in SQL that match every spelling of one champion (history holds
/// both "Kai'Sa" and "Kaisa"): both sides compare on
/// <see cref="GameConstants.NormalizeChampionKey"/>, the same key the analytics merge
/// uses, registered on the connection as a SQLite function.
/// </summary>
internal static class ChampionSql
{
    private const string KeyFunction = "revu_champion_key";

    /// <summary>
    /// A predicate matching <paramref name="column"/> to <paramref name="champion"/>.
    /// Registers the key function on the command's connection and binds the normalized
    /// key as <paramref name="parameterName"/>.
    /// </summary>
    public static string Matches(SqliteCommand cmd, string column, string parameterName, string champion)
    {
        var connection = cmd.Connection ?? throw new InvalidOperationException("The command needs its connection first.");
        connection.CreateFunction<string?, string>(KeyFunction, GameConstants.NormalizeChampionKey, isDeterministic: true);
        cmd.Parameters.AddWithValue(parameterName, GameConstants.NormalizeChampionKey(champion));
        return $"{KeyFunction}({column}) = {parameterName}";
    }
}
