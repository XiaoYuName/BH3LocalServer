using System.Buffers.Binary;
using BH3.Game.Lobby;
using BH3.Game.Messaging;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed class LobbyTests
{
    private static GamePacket Packet(ushort id, IMessage message)
    { var prefix = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(prefix, GamePacketCodec.Head); BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(12), 99999); return new(prefix, id, [], message.ToByteArray()); }
    [Fact]
    public void LoginRequiresValidTicketAndMatchingAccountThenUsesSessionIdentity()
    {
        using var temp = new TestDirectory(); var factory = new SqliteConnectionFactory(Path.Combine(temp.Path, "test.db")); SchemaMigrator.Initialize(factory);
        byte[] key = new byte[32]; var store = new LobbyStore(factory); var profile = new SqlitePlayerStore(factory);
        var dispatcher = new GameDispatcher(new LobbyHandlers(store, profile, key).Create()); var session = new GameSession(1, "local", 1);
        Assert.Equal(DispatchStatus.InvalidState, dispatcher.Dispatch(session, Packet(6, new PlayerLoginReq())).Status);
        var bad = dispatcher.Dispatch(session, Packet(4, new GetPlayerTokenReq { AccountUid = "10001", ComboToken = "forged" }));
        Assert.Equal(GetPlayerTokenRsp.Types.Retcode.AccountVerifyError, GetPlayerTokenRsp.Parser.ParseFrom(Assert.Single(bad.Replies).Body).Retcode);
        Assert.Null(profile.Find(10001)); Assert.Equal(SessionState.Connected, session.State);
        string ticket = LocalTicket.Issue(key, 10001, "captain", DateTimeOffset.UtcNow);
        var good = dispatcher.Dispatch(session, Packet(4, new GetPlayerTokenReq { AccountUid = "10001", AccountType = 1, Version = "9.1.0", ComboToken = ticket }));
        Assert.Equal(10001u, GetPlayerTokenRsp.Parser.ParseFrom(Assert.Single(good.Replies).Body).Uid);
        Assert.Equal(SessionState.TokenIssued, session.State);
        var wrong = dispatcher.Dispatch(session, Packet(6, new PlayerLoginReq { AccountUid = "20002" }));
        Assert.Equal(PlayerLoginRsp.Types.Retcode.Fail, PlayerLoginRsp.Parser.ParseFrom(Assert.Single(wrong.Replies).Body).Retcode);
        dispatcher.Dispatch(session, Packet(6, new PlayerLoginReq { AccountUid = "10001" }));
        var main = Assert.Single(dispatcher.Dispatch(session, Packet(10, new GetMainDataReq())).Replies);
        Assert.Equal(10001u, main.ClaimedUserId); Assert.Equal("captain", GetMainDataRsp.Parser.ParseFrom(main.Body).Nickname);
        dispatcher.Dispatch(session, Packet(1588, new SetClientDataReq { ClientData = new ClientData { Type = (ClientDataType)1, Id = 1, Data = ByteString.CopyFromUtf8("persisted") } }));
        Assert.Equal("persisted", System.Text.Encoding.UTF8.GetString(Assert.Single(new LobbyStore(factory).ReadClient(10001, 1, 1)).Data));
        Assert.Empty(store.ReadClient(20002, 1, 1));
        Assert.Equal(DispatchStatus.Unsupported, dispatcher.Dispatch(session, Packet(65000, new GetMainDataReq())).Status);
    }
}
