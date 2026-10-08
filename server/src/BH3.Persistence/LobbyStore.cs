using System.Text.Json;

namespace BH3.Persistence;

public sealed record LobbyState(uint Level, uint Stamina, uint AvatarId, uint WeaponId, uint DressId, uint RegisteredAt, uint[] CompletedGuides,
    uint Exp = 0, uint Scoin = 0, uint Hcoin = 0, uint AvatarLevel = 1, uint AvatarExp = 0, uint StaminaUpdatedAt = 0);
public sealed record ClientBlob(int Type, uint Id, byte[] Data);

public sealed partial class LobbyStore(SqliteConnectionFactory factory)
{
    public void EnsurePlayer(uint uid, string name, LobbyState initial)
    {
        if (uid == 0 || string.IsNullOrWhiteSpace(name) || name.Length > 32 || name.Any(char.IsControl)) throw new ArgumentException("Invalid player identity.");
        using var connection = factory.Open(); using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account(uid,created_utc) VALUES($uid,$time) ON CONFLICT(uid) DO NOTHING;
            INSERT INTO player_profile(uid,nickname,revision) VALUES($uid,$name,0) ON CONFLICT(uid) DO NOTHING;
            INSERT INTO player_lobby(uid,data_json) VALUES($uid,$data) ON CONFLICT(uid) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$uid", uid); command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(initial));
        command.ExecuteNonQuery(); tx.Commit();
    }
    public LobbyState Read(uint uid)
    {
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT data_json FROM player_lobby WHERE uid=$uid;"; command.Parameters.AddWithValue("$uid", uid);
        return JsonSerializer.Deserialize<LobbyState>((string?)command.ExecuteScalar() ?? throw new InvalidOperationException("Player not initialized."))!;
    }
    public ClientBlob[] ReadClient(uint uid, int type, uint id)
    {
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT type,id,data FROM client_data WHERE uid=$uid AND type=$type AND ($id=0 OR id=$id) ORDER BY id;";
        command.Parameters.AddWithValue("$uid", uid); command.Parameters.AddWithValue("$type", type); command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader(); var result = new List<ClientBlob>();
        while (reader.Read()) result.Add(new(reader.GetInt32(0), checked((uint)reader.GetInt64(1)), (byte[])reader[2]));
        return result.ToArray();
    }
    public void CompleteGuides(uint uid, IEnumerable<uint> guideIds)
    {
        var requested = guideIds.Distinct().ToArray();
        if (requested.Length > 4096 || requested.Contains(0u)) throw new ArgumentException("Invalid guide report.");
        using var connection = factory.Open(); using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT data_json FROM player_lobby WHERE uid=$uid;";
        command.Parameters.AddWithValue("$uid", uid);
        var state = JsonSerializer.Deserialize<LobbyState>((string?)command.ExecuteScalar() ?? throw new InvalidOperationException("Player not initialized."))!;
        var completed = state.CompletedGuides.Concat(requested).Distinct().Order().ToArray();
        if (completed.Length > 16384) throw new ArgumentException("Guide capacity reached.");
        command.CommandText = "UPDATE player_lobby SET data_json=$data WHERE uid=$uid;";
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(state with { CompletedGuides = completed }));
        command.ExecuteNonQuery(); tx.Commit();
    }
    public void WriteClient(uint uid, ClientBlob value)
    {
        if (value.Data.Length > 65536 || value.Type < 0) throw new ArgumentException("Invalid client data.");
        using var connection = factory.Open(); using var tx = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT count(*) FROM client_data WHERE uid=$uid AND NOT(type=$type AND id=$id);";
        command.Parameters.AddWithValue("$uid", uid); command.Parameters.AddWithValue("$type", value.Type); command.Parameters.AddWithValue("$id", value.Id);
        if (Convert.ToInt32(command.ExecuteScalar()) >= 128) throw new InvalidOperationException("Client data capacity reached.");
        command.CommandText = "INSERT INTO client_data(uid,type,id,data) VALUES($uid,$type,$id,$data) ON CONFLICT(uid,type,id) DO UPDATE SET data=excluded.data;";
        command.Parameters.AddWithValue("$data", value.Data); command.ExecuteNonQuery(); tx.Commit();
    }
}
