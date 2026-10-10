using BH3.Persistence;
using BH3.Tests;
using Microsoft.Data.Sqlite;

namespace BH3.Persistence.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void ForeignDatabaseWithSameVersionIsRejected()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path,"test.db"));
        using (var c = factory.Open()) { using var q = c.CreateCommand(); q.CommandText = "PRAGMA user_version=1;"; q.ExecuteNonQuery(); }
        Assert.Throws<InvalidDataException>(() => SchemaMigrator.Initialize(factory));
    }
    [Fact]
    public void SchemaIsRepeatableAndEveryConnectionEnforcesForeignKeys()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path,"test.db"));
        SchemaMigrator.Initialize(factory); SchemaMigrator.Initialize(factory);
        using var connection = factory.Open(); Assert.Equal(4, SchemaMigrator.Version(connection));
        using var command = connection.CreateCommand(); command.CommandText = "INSERT INTO player_profile VALUES(77,'orphan',0);";
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }
    [Fact]
    public void FutureSchemaIsRejectedWithoutDowngrading()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path,"test.db"));
        using (var c = factory.Open()) { using var q = c.CreateCommand(); q.CommandText = "PRAGMA user_version=9;"; q.ExecuteNonQuery(); }
        Assert.Throws<InvalidDataException>(() => SchemaMigrator.Initialize(factory));
        using var reopened = factory.Open(readOnly:true); Assert.Equal(9, SchemaMigrator.Version(reopened));
    }
    [Fact]
    public void ProfileSurvivesRestartAndStaleRevisionCannotOverwrite()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path,"test.db")); SchemaMigrator.Initialize(factory);
        var store = new SqlitePlayerStore(factory); Assert.True(store.Create(10001,"舰长")); Assert.False(store.Create(10001,"replacement"));
        Assert.Equal(WriteResult.Applied, store.Rename(10001,0,"新舰长"));
        Assert.Equal(WriteResult.Conflict, store.Rename(10001,0,"stale")); Assert.Equal(WriteResult.Missing, store.Rename(2,0,"missing"));
        var reopened = new SqlitePlayerStore(new(factory.DatabasePath)); Assert.Equal(new PlayerProfile(10001,"新舰长",1), reopened.Find(10001));
    }
    [Fact]
    public void FailedProfileInsertRollsBackAccountCreation()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path,"test.db")); SchemaMigrator.Initialize(factory);
        using (var c = factory.Open()) { using var q = c.CreateCommand(); q.CommandText = "CREATE TRIGGER fail_profile BEFORE INSERT ON player_profile BEGIN SELECT RAISE(ABORT, 'injected failure'); END;"; q.ExecuteNonQuery(); }
        var store = new SqlitePlayerStore(factory); Assert.Throws<SqliteException>(() => store.Create(1,"test"));
        using var connection = factory.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM account;";
        Assert.Equal(0L, command.ExecuteScalar());
    }
    [Fact]
    public async Task ConcurrentRevisionWritesHaveOneWinner()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path,"test.db")); SchemaMigrator.Initialize(factory);
        var store = new SqlitePlayerStore(factory); store.Create(1,"test");
        var results = await Task.WhenAll(Task.Run(() => store.Rename(1,0,"a"),TestContext.Current.CancellationToken), Task.Run(() => store.Rename(1,0,"b"),TestContext.Current.CancellationToken));
        Assert.Single(results, x => x == WriteResult.Applied); Assert.Single(results, x => x == WriteResult.Conflict); Assert.Equal(1, store.Find(1)!.Revision);
    }
    [Fact]
    public void InvalidIdentityAndNicknameDoNotCreatePlayers()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path,"test.db")); SchemaMigrator.Initialize(factory);
        var store = new SqlitePlayerStore(factory); Assert.Throws<ArgumentOutOfRangeException>(() => store.Create(0,"name"));
        Assert.Throws<ArgumentException>(() => store.Create(1,"\n")); Assert.Null(store.Find(1));
    }
}
