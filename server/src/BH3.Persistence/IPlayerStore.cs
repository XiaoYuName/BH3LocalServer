namespace BH3.Persistence;

public sealed record PlayerProfile(long Uid, string Nickname, long Revision);
public enum WriteResult { Applied, Conflict, Missing }

public interface IPlayerStore
{
    PlayerProfile? Find(long uid);
    bool Create(long uid, string nickname);
    WriteResult Rename(long uid, long expectedRevision, string nickname);
}
