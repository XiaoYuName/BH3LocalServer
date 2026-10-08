namespace BH3.Game.Sessions;

public enum SessionState { Connected, TokenIssued, Authenticated, Closed }

public sealed class GameSession(uint conversation, string peer, uint nonce)
{
    private readonly Lock gate = new();
    private SessionState state = SessionState.Connected;
    private long? playerId;
    public long? PendingPlayerId { get; private set; }
    public uint AccountType { get; private set; }
    public void IssueToken(long uid, uint accountType)
    {
        lock (gate)
        {
            if (state != SessionState.Connected || uid <= 0) throw new InvalidOperationException("Token requires a connected session.");
            PendingPlayerId = uid; AccountType = accountType; state = SessionState.TokenIssued;
        }
    }
    public uint Conversation { get; } = conversation;
    public string Peer { get; } = peer;
    public uint ClientNonce { get; } = nonce;
    public SessionState State { get { lock (gate) return state; } }
    public long? PlayerId { get { lock (gate) return playerId; } }

    // The authentication handler validates the server-issued local ticket first.
    public void Authenticate(long uid)
    {
        lock (gate)
        {
            if (uid <= 0) throw new ArgumentOutOfRangeException(nameof(uid));
            if (state is not (SessionState.Connected or SessionState.TokenIssued) || (PendingPlayerId is { } pending && pending != uid)) throw new InvalidOperationException("Invalid authentication state/identity.");
            playerId = uid; state = SessionState.Authenticated;
        }
    }
    public void Close() { lock (gate) { state = SessionState.Closed; playerId = null; PendingPlayerId = null; } }
    internal T Execute<T>(Func<T> action) { lock (gate) return action(); }
}
