#nullable enable

using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data;

/// <summary>
/// Abstraction over SQLite connection creation for testability and DI.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>Creates and opens a new <see cref="SqliteConnection"/>.</summary>
    SqliteConnection CreateConnection();

    /// <summary>Full path to the database file on disk.</summary>
    string DatabasePath { get; }
}
