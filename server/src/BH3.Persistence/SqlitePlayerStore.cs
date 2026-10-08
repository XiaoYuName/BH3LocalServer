namespace BH3.Persistence;

public sealed class SqlitePlayerStore(SqliteConnectionFactory factory) : IPlayerStore
{
    public PlayerProfile? Find(long uid)
    {
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT uid, nickname, revision FROM player_profile WHERE uid=$uid;";
        command.Parameters.AddWithValue("$uid", uid);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)) : null;
    }

    public bool Create(long uid, string nickname)
    {
        Validate(uid, nickname);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var account = connection.CreateCommand(); account.Transaction = transaction;
        account.CommandText = "INSERT INTO account(uid, created_utc) VALUES($uid,$created) ON CONFLICT(uid) DO NOTHING;";
        account.Parameters.AddWithValue("$uid", uid); account.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        if (account.ExecuteNonQuery() == 0) return false;
        using var profile = connection.CreateCommand(); profile.Transaction = transaction;
        profile.CommandText = "INSERT INTO player_profile(uid,nickname,revision) VALUES($uid,$name,0);";
        profile.Parameters.AddWithValue("$uid", uid); profile.Parameters.AddWithValue("$name", nickname);
        profile.ExecuteNonQuery(); transaction.Commit(); return true;
    }

    public WriteResult Rename(long uid, long expectedRevision, string nickname)
    {
        Validate(uid, nickname);
        if (expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = "UPDATE player_profile SET nickname=$name, revision=revision+1 WHERE uid=$uid AND revision=$rev;";
        update.Parameters.AddWithValue("$name", nickname); update.Parameters.AddWithValue("$uid", uid); update.Parameters.AddWithValue("$rev", expectedRevision);
        if (update.ExecuteNonQuery() == 1) { transaction.Commit(); return WriteResult.Applied; }
        using var exists = connection.CreateCommand(); exists.Transaction = transaction;
        exists.CommandText = "SELECT count(*) FROM player_profile WHERE uid=$uid;"; exists.Parameters.AddWithValue("$uid", uid);
        return Convert.ToInt32(exists.ExecuteScalar()) == 0 ? WriteResult.Missing : WriteResult.Conflict;
    }

    private static void Validate(long uid, string nickname)
    {
        if (uid <= 0) throw new ArgumentOutOfRangeException(nameof(uid));
        if (string.IsNullOrWhiteSpace(nickname) || nickname.Length > 32 || nickname.Any(char.IsControl))
            throw new ArgumentException("Nickname must contain 1–32 visible characters.", nameof(nickname));
    }
}
