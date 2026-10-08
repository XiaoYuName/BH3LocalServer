using Microsoft.Data.Sqlite;

namespace BH3.Persistence;

public static class SchemaMigrator
{
    public const int CurrentVersion = 2;
    public const int ApplicationId = 0x42483353;
    public static int Version(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static int Check(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        int version = Version(connection, transaction);
        if (version > CurrentVersion) throw new InvalidDataException($"Database schema {version} is newer than supported {CurrentVersion}.");
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA application_id;";
        int identity = Convert.ToInt32(command.ExecuteScalar());
        if ((version > 0 && identity != ApplicationId) || (identity != 0 && identity != ApplicationId))
            throw new InvalidDataException("Database does not belong to BH3.Server.");
        return version;
    }

    public static void Initialize(SqliteConnectionFactory factory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(factory.DatabasePath)!);
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        int version = Check(connection, transaction);
        if (version == 0)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE account (
                    uid INTEGER PRIMARY KEY CHECK(uid > 0),
                    created_utc TEXT NOT NULL
                );
                CREATE TABLE player_profile (
                    uid INTEGER PRIMARY KEY REFERENCES account(uid) ON DELETE CASCADE,
                    nickname TEXT NOT NULL CHECK(length(nickname) BETWEEN 1 AND 32),
                    revision INTEGER NOT NULL DEFAULT 0 CHECK(revision >= 0)
                );
                PRAGMA user_version = 1;
                PRAGMA application_id = 0x42483353;
                """;
            command.ExecuteNonQuery();
        }
        if (version < 2)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE player_lobby (
                    uid INTEGER PRIMARY KEY REFERENCES player_profile(uid) ON DELETE CASCADE,
                    data_json TEXT NOT NULL
                );
                CREATE TABLE client_data (
                    uid INTEGER NOT NULL REFERENCES player_profile(uid) ON DELETE CASCADE,
                    type INTEGER NOT NULL, id INTEGER NOT NULL,
                    data BLOB NOT NULL CHECK(length(data) <= 65536),
                    PRIMARY KEY(uid,type,id)
                );
                PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
