using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BH3.Persistence;

public sealed class StageProgress
{
    public uint Wins { get; set; }
    public uint Entries { get; set; }
    public uint BestTime { get; set; }
    public uint BestScore { get; set; }
    public HashSet<uint> Challenges { get; set; } = [];
}
public sealed record StageRun(uint StageId, string Key, uint StartedAt, uint Cost, uint[] Avatars, byte[] BeginResponse);
public sealed class CampaignState
{
    public Dictionary<uint, StageProgress> Stages { get; set; } = [];
    public HashSet<uint> ClaimedMissions { get; set; } = [];
    public HashSet<string> ClaimedActRewards { get; set; } = [];
    public Dictionary<uint, uint> Materials { get; set; } = [];
    public uint[] Team { get; set; } = [];
    public StageRun? Run { get; set; }
}

// All gameplay state, wallet changes and settlement receipts share the same SQLite transaction.
public sealed class CampaignTransaction(SqliteConnection connection, SqliteTransaction transaction, uint uid, LobbyState lobby, CampaignState campaign)
{
    public LobbyState Lobby { get; set; } = lobby;
    public CampaignState Campaign { get; } = campaign;
    public byte[]? Receipt(string fingerprint)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT response FROM stage_receipt WHERE uid=$uid AND fingerprint=$fingerprint;";
        command.Parameters.AddWithValue("$uid", uid); command.Parameters.AddWithValue("$fingerprint", fingerprint);
        return command.ExecuteScalar() as byte[];
    }
    public void SaveReceipt(string fingerprint, byte[] response)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO stage_receipt(uid,fingerprint,response) VALUES($uid,$fingerprint,$response);";
        command.Parameters.AddWithValue("$uid", uid); command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$response", response); command.ExecuteNonQuery();
    }
}

public sealed partial class LobbyStore
{
    public T Campaign<T>(uint uid, Func<CampaignTransaction, T> action)
    {
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$uid", uid);
        command.CommandText = "SELECT data_json FROM player_lobby WHERE uid=$uid;";
        var lobby = JsonSerializer.Deserialize<LobbyState>((string?)command.ExecuteScalar() ?? throw new InvalidOperationException("Player not initialized."))!;
        command.CommandText = "SELECT data_json FROM player_campaign WHERE uid=$uid;";
        var campaign = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CampaignState>(json)! : new CampaignState();
        var context = new CampaignTransaction(connection, transaction, uid, lobby, campaign);
        T result = action(context);
        command.CommandText = """
            UPDATE player_lobby SET data_json=$lobby WHERE uid=$uid;
            INSERT INTO player_campaign(uid,data_json) VALUES($uid,$campaign)
                ON CONFLICT(uid) DO UPDATE SET data_json=excluded.data_json;
            """;
        command.Parameters.AddWithValue("$lobby", JsonSerializer.Serialize(context.Lobby));
        command.Parameters.AddWithValue("$campaign", JsonSerializer.Serialize(context.Campaign));
        command.ExecuteNonQuery(); transaction.Commit(); return result;
    }
}
