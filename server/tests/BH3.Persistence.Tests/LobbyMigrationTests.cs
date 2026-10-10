using BH3.Tests;

namespace BH3.Persistence.Tests;

public sealed class LobbyMigrationTests
{
    [Fact]
    public void SchemaTwoCampaignMigrationPreservesOriginalLobbyBytesAndSettings()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path, "old.db"));
        SchemaMigrator.Initialize(factory); var store = new LobbyStore(factory);
        store.EnsurePlayer(10001, "existing", new(5, 31, 101, 20001, 59101, 123, [1, 8, 99]));
        store.WriteClient(10001, new(1, 7, [7, 8, 9]));
        const string original = "{\"Level\":5,\"Stamina\":31,\"AvatarId\":101,\"WeaponId\":20001,\"DressId\":59101,\"RegisteredAt\":123,\"CompletedGuides\":[1,8,99]}";
        using (var c = factory.Open())
        {
            using var q = c.CreateCommand(); q.CommandText = "DROP TABLE stage_receipt; DROP TABLE player_campaign; UPDATE player_lobby SET data_json=$json; PRAGMA user_version=2;";
            q.Parameters.AddWithValue("$json", original); q.ExecuteNonQuery();
        }
        SchemaMigrator.Initialize(factory); SchemaMigrator.Initialize(factory);
        using (var c = factory.Open())
        { using var q = c.CreateCommand(); q.CommandText = "SELECT data_json FROM player_lobby WHERE uid=10001;"; Assert.Equal(original, q.ExecuteScalar()); }
        Assert.Equal(31u, store.Read(10001).Stamina); Assert.Equal(0u, store.Read(10001).Scoin);
        Assert.Equal(new uint[] { 1, 8, 99 }, store.Read(10001).CompletedGuides);
        Assert.Equal(new byte[] { 7, 8, 9 }, Assert.Single(store.ReadClient(10001, 1, 7)).Data);
        Assert.Empty(store.Campaign(10001, tx => tx.Campaign.Stages));
    }
    [Fact]
    public void SchemaOneUpgradesWithoutOverwritingPlayerAndClientDataIsIsolated()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path, "old.db"));
        using (var connection = factory.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE account(uid INTEGER PRIMARY KEY,created_utc TEXT NOT NULL);
                CREATE TABLE player_profile(uid INTEGER PRIMARY KEY REFERENCES account(uid),nickname TEXT NOT NULL,revision INTEGER NOT NULL);
                INSERT INTO account VALUES(10001,'2026-10-08');
                INSERT INTO player_profile VALUES(10001,'original-captain',4);
                PRAGMA application_id=0x42483353; PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        SchemaMigrator.Initialize(factory); var lobby = new LobbyStore(factory);
        lobby.EnsurePlayer(10001, "new-name", new(1, 80, 101, 20001, 59101, 100, [1]));
        var profile = new SqlitePlayerStore(factory).Find(10001)!;
        Assert.Equal("original-captain", profile.Nickname); Assert.Equal(4, profile.Revision);
        lobby.WriteClient(10001, new(1, 42, [1, 2, 3]));
        SchemaMigrator.Initialize(factory);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(new LobbyStore(factory).ReadClient(10001, 1, 42)).Data);
        Assert.Empty(lobby.ReadClient(10002, 1, 42));
        Assert.Throws<ArgumentException>(() => lobby.WriteClient(10001, new(1, 42, new byte[65537])));
        using var reopened = factory.Open(); Assert.Equal(4, SchemaMigrator.Version(reopened));
    }
}
