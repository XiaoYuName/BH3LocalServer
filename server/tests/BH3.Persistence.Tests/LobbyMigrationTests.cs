using BH3.Tests;

namespace BH3.Persistence.Tests;

public sealed class LobbyMigrationTests
{
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
        using var reopened = factory.Open(); Assert.Equal(2, SchemaMigrator.Version(reopened));
    }
}
