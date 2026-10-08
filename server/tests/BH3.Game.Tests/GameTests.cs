using BH3.Game.Messaging;
using BH3.Game.Players;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;

namespace BH3.Game.Tests;

public sealed class GameTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(int seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    [Fact]
    public void DuplicateHandshakeReusesConversationAndNonceChangeClosesOldSession()
    {
        var registry = new SessionRegistry(2, TimeSpan.FromSeconds(30));
        var first = registry.Connect("127.0.0.1:1001", 9)!;
        Assert.Same(first, registry.Connect(first.Peer, 9));
        var second = registry.Connect(first.Peer, 10)!;
        Assert.NotEqual(first.Conversation, second.Conversation); Assert.Equal(SessionState.Closed, first.State);
        Assert.Equal(1, registry.Count);
    }
    [Fact]
    public void EndpointCapacityAndMonotonicExpiryAreEnforced()
    {
        var clock = new Clock(); var registry = new SessionRegistry(1, TimeSpan.FromSeconds(5), clock);
        var first = registry.Connect("same-ip:1", 1)!;
        Assert.Null(registry.Connect("same-ip:2", 1)); clock.Advance(4);
        Assert.Same(first, registry.Connect("same-ip:1", 1)); clock.Advance(4); Assert.Equal(0, registry.Expire());
        clock.Advance(1); Assert.Equal(1, registry.Expire()); Assert.Equal(SessionState.Closed, first.State);
        Assert.NotNull(registry.Connect("same-ip:2", 1)); registry.CloseAll(); Assert.Equal(0, registry.Count);
    }
    [Fact]
    public void AuthenticationCannotRebindOrResurrectClosedSession()
    {
        var session = new GameSession(1,"peer",0);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Authenticate(0));
        session.Authenticate(10001); Assert.Equal(10001, session.PlayerId);
        Assert.Throws<InvalidOperationException>(() => session.Authenticate(10002));
        session.Close(); Assert.Null(session.PlayerId);
        Assert.Throws<InvalidOperationException>(() => session.Authenticate(10001));
    }
    private sealed class Handler : IGameMessageHandler
    {
        public ushort CommandId => 123;
        public SessionState RequiredState => SessionState.Authenticated;
        public int Calls;
        public IReadOnlyList<GamePacket> Handle(GameSession session, GamePacket packet) { Calls++; return []; }
    }
    [Fact]
    public void DispatchRejectsUnknownStateMismatchAndClosedSessions()
    {
        var handler = new Handler(); var dispatcher = new GameDispatcher([handler]); var session = new GameSession(1,"p",0);
        var packet = new GamePacket(new byte[26], 123, [], []);
        Assert.Equal(DispatchStatus.Unsupported, dispatcher.Dispatch(session, packet with { CommandId = 999 }).Status);
        Assert.Equal(DispatchStatus.InvalidState, dispatcher.Dispatch(session, packet).Status);
        session.Authenticate(10001); Assert.Equal(DispatchStatus.Handled, dispatcher.Dispatch(session, packet).Status);
        session.Close(); Assert.Equal(DispatchStatus.Closed, dispatcher.Dispatch(session, packet).Status); Assert.Equal(1, handler.Calls);
        Assert.Throws<ArgumentException>(() => new GameDispatcher([handler,handler]));
    }
    private sealed class Store : IPlayerStore
    {
        public long ObservedUid;
        public bool Create(long uid, string nickname) => throw new NotSupportedException();
        public PlayerProfile? Find(long uid) { ObservedUid = uid; return new(uid,"test",0); }
        public WriteResult Rename(long uid, long expectedRevision, string nickname) { ObservedUid = uid; return WriteResult.Applied; }
    }
    [Fact]
    public void PlayerUseCaseOnlyUsesAuthenticatedSessionIdentity()
    {
        var store = new Store(); var service = new PlayerService(store); var session = new GameSession(1,"p",0);
        Assert.Throws<InvalidOperationException>(() => service.FindSelf(session)); Assert.Equal(0, store.ObservedUid);
        session.Authenticate(10001); Assert.Equal(10001, service.FindSelf(session)!.Uid);
        Assert.Equal(WriteResult.Applied, service.RenameSelf(session,0,"captain")); Assert.Equal(10001, store.ObservedUid);
        session.Close(); Assert.Throws<InvalidOperationException>(() => service.RenameSelf(session,0,"captain"));
    }
}
