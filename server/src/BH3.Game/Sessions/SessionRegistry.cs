namespace BH3.Game.Sessions;

public sealed class SessionRegistry(int capacity, TimeSpan idleTimeout, TimeProvider? clock = null)
{
    private sealed record Entry(GameSession Session, long LastSeen);
    private readonly Dictionary<string, Entry> peers = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private uint nextConversation;
    public int Count { get { lock (gate) return peers.Count; } }
    public GameSession? Find(string peer)
    { lock (gate) return peers.TryGetValue(peer, out var entry) && entry.Session.State != SessionState.Closed ? entry.Session : null; }
    public void Touch(string peer)
    { lock (gate) if (peers.TryGetValue(peer, out var entry)) peers[peer] = entry with { LastSeen = time.GetTimestamp() }; }

    public GameSession? Connect(string peer, uint nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peer);
        lock (gate)
        {
            ExpireCore();
            if (peers.TryGetValue(peer, out var previous))
            {
                if (previous.Session.ClientNonce == nonce && previous.Session.State != SessionState.Closed)
                { peers[peer] = previous with { LastSeen = time.GetTimestamp() }; return previous.Session; }
                previous.Session.Close(); peers.Remove(peer);
            }
            if (peers.Count >= capacity) return null;
            do { nextConversation++; } while (nextConversation == 0 || peers.Values.Any(e => e.Session.Conversation == nextConversation));
            var session = new GameSession(nextConversation, peer, nonce);
            peers.Add(peer, new(session, time.GetTimestamp())); return session;
        }
    }
    public int Expire() { lock (gate) return ExpireCore(); }
    private int ExpireCore()
    {
        long now = time.GetTimestamp();
        var expired = peers.Where(p => p.Value.Session.State == SessionState.Closed || time.GetElapsedTime(p.Value.LastSeen, now) >= idleTimeout).Select(p => p.Key).ToArray();
        foreach (var key in expired) { peers[key].Session.Close(); peers.Remove(key); }
        return expired.Length;
    }
    public void CloseAll() { lock (gate) { foreach (var value in peers.Values) value.Session.Close(); peers.Clear(); } }
}
