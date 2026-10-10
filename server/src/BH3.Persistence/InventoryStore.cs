using Microsoft.Data.Sqlite;

namespace BH3.Persistence;

// Protobuf stays opaque here; the game layer owns message interpretation.
public sealed record PlayerInventory(byte[] Avatars, byte[] Equipment, string SourceSha256, uint SourceUid, string ImportedUtc);

public sealed partial class LobbyStore
{
    internal static PlayerInventory? ReadInventory(SqliteConnection connection, SqliteTransaction? tx, uint uid)
    {
        using var q = connection.CreateCommand(); q.Transaction = tx;
        q.CommandText = "SELECT avatars,equipment,source_sha256,source_uid,imported_utc FROM player_inventory WHERE uid=$uid;";
        q.Parameters.AddWithValue("$uid", uid);
        using var r = q.ExecuteReader();
        return r.Read() ? new((byte[])r[0], (byte[])r[1], r.GetString(2), checked((uint)r.GetInt64(3)), r.GetString(4)) : null;
    }
    internal static void WriteInventory(SqliteConnection connection, SqliteTransaction tx, uint uid, PlayerInventory value)
    {
        using var q = connection.CreateCommand(); q.Transaction = tx;
        q.CommandText = """
            INSERT INTO player_inventory(uid,avatars,equipment,source_sha256,source_uid,imported_utc)
            VALUES($uid,$avatars,$equipment,$hash,$source,$time)
            ON CONFLICT(uid) DO UPDATE SET avatars=excluded.avatars,equipment=excluded.equipment,
                source_sha256=excluded.source_sha256,source_uid=excluded.source_uid,imported_utc=excluded.imported_utc;
            """;
        q.Parameters.AddWithValue("$uid", uid); q.Parameters.AddWithValue("$avatars", value.Avatars);
        q.Parameters.AddWithValue("$equipment", value.Equipment); q.Parameters.AddWithValue("$hash", value.SourceSha256);
        q.Parameters.AddWithValue("$source", value.SourceUid); q.Parameters.AddWithValue("$time", value.ImportedUtc);
        q.ExecuteNonQuery();
    }
    public PlayerInventory? Inventory(uint uid)
    {
        using var c = factory.Open(); return ReadInventory(c, null, uid);
    }
}
