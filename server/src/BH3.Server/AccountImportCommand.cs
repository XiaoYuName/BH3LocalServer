using System.Text.Json;
using BH3.Game.Players;
using BH3.Persistence;
using BH3.Server.Configuration;

namespace BH3.Server;

public static class AccountImportCommand
{
    public static int Run(ServerConfig config, string path, uint uid, bool allowPartial, bool syncLevel)
    {
        if (uid == 0) throw new ArgumentException("--uid must identify an existing local account.");
        var plan = AccountCopyImport.Read(path, allowPartial);
        string db = config.ResolveDatabase();
        if (!File.Exists(db)) throw new InvalidDataException("Local player database does not exist; import will not create an account.");
        using var lease = new FileStream(db + ".host.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var factory = new SqliteConnectionFactory(db);
        using (var c = factory.Open(readOnly: true))
        {
            SchemaMigrator.Check(c);
            using var q = c.CreateCommand(); q.CommandText = "SELECT count(*) FROM player_lobby WHERE uid=$uid;";
            q.Parameters.AddWithValue("$uid", uid);
            if (Convert.ToInt32(q.ExecuteScalar()) != 1) throw new InvalidDataException("Local UID is not initialized.");
        }
        string backup = db + ".before-import-" + Guid.NewGuid().ToString("N") + ".bak";
        using (var c = factory.Open(readOnly: true))
        using (var copy = new SqliteConnectionFactory(backup).Open()) c.BackupDatabase(copy);
        SchemaMigrator.Initialize(factory);
        var result = AccountCopyImport.Apply(new LobbyStore(factory), uid, plan, syncLevel);
        Console.WriteLine(JsonSerializer.Serialize(new { result, backup }, ServerConfig.Json));
        return 0;
    }
}
