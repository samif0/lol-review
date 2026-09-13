using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Revu.Core.Data;

/// <summary>Read-only downgrade guard, to run before migrations, seeds, or other writes.</summary>
public static class DatabaseSchemaCompatibility
{
    public static int EnsureCompatible(string databasePath, int maximumSchemaVersion = Schema.CurrentAppSchemaVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumSchemaVersion);
        if (!File.Exists(databasePath)) throw new FileNotFoundException("The database does not exist.", databasePath);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        var version = ReadVersion(connection);
        if (version > maximumSchemaVersion)
            throw new InvalidDataException($"Database schema {version} is newer than this backend supports ({maximumSchemaVersion}). Preserve this database and use a compatible backend.");
        return version;
    }

    internal static int ReadVersion(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'schema_metadata'";
        if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) == 0) return 0;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_metadata WHERE key = $key";
        command.Parameters.AddWithValue("$key", Schema.AppSchemaVersionKey);
        var value = command.ExecuteScalar();
        if (value is null) return 0; // Legacy database before version tracking.
        if (!int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None,
                CultureInfo.InvariantCulture, out var version))
            throw new InvalidDataException("Database schema version is malformed; compatibility cannot be established.");
        return version;
    }
}
