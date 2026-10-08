using Microsoft.Data.Sqlite;

namespace BH3.Persistence;

public sealed class SqliteConnectionFactory(string path)
{
    public string DatabasePath { get; } = Path.GetFullPath(path);
    public SqliteConnection Open(bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true, DefaultTimeout = 5, Pooling = false
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
}
