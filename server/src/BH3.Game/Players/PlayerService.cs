using BH3.Game.Sessions;
using BH3.Persistence;

namespace BH3.Game.Players;

public sealed class PlayerService(IPlayerStore store)
{
    public PlayerProfile? FindSelf(GameSession session) => session.Execute(() => store.Find(RequirePlayer(session)));
    public WriteResult RenameSelf(GameSession session, long revision, string nickname) =>
        session.Execute(() => store.Rename(RequirePlayer(session), revision, nickname));
    private static long RequirePlayer(GameSession session) =>
        session.State == SessionState.Authenticated && session.PlayerId is { } uid
            ? uid : throw new InvalidOperationException("Player operation requires an authenticated session.");
}
