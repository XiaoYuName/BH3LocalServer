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

public sealed class TeamPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationMembersResolveAfterSaveAndRestartAndCanStartStage(bool saveTeam)
    {
        using var temp = new TestDirectory();
        var factory = new SqliteConnectionFactory(Path.Combine(temp.Path, "team.db"));
        SchemaMigrator.Initialize(factory);
        var store = new LobbyStore(factory);
        store.EnsurePlayer(10001, "captain", new(1, 80, 101, 20001, 59101, 1, [1, 2]));
        var session = new GameSession(1, "test", 1); session.Authenticate(10001);
        GameDispatcher CreateDispatcher() => new(new LobbyHandlers(new LobbyStore(factory), new SqlitePlayerStore(factory), new byte[32]).Create());
        var dispatcher = CreateDispatcher();
        GamePacket Packet(ushort command, IMessage request)
        {
            byte[] prefix = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(prefix, GamePacketCodec.Head);
            return new(prefix, command, [], request.ToByteArray());
        }
        if (saveTeam)
        {
            var update = dispatcher.Dispatch(session, Packet(49, new UpdateAvatarTeamNotify
                { Team = new AvatarTeam { StageType = 1, AvatarIdList = { 101, 0, 0 } } }));
            Assert.Equal(DispatchStatus.Handled, update.Status); Assert.Empty(update.Replies);
        }
        dispatcher = CreateDispatcher();
        var roster = GetAvatarDataRsp.Parser.ParseFrom(Assert.Single(dispatcher.Dispatch(session,
            Packet(24, new GetAvatarDataReq { AvatarIdList = { 0 } })).Replies).Body);
        var response = Assert.Single(dispatcher.Dispatch(session, Packet(47, new GetAvatarTeamDataReq())).Replies);
        var team = Assert.Single(GetAvatarTeamDataRsp.Parser.ParseFrom(response.Body).AvatarTeamList, x => x.StageType == 1);
        Assert.Equal(1u, team.StageType); Assert.NotEmpty(team.AvatarIdList);
        // 9.1 treats every member as an avatar lookup; ID 0 is not an empty UI slot.
        Assert.All(team.AvatarIdList, id => Assert.Contains(roster.AvatarList, avatar => avatar.AvatarId == id));
        Assert.Equal(80u, store.Read(10001).Stamina);
        var begin = dispatcher.Dispatch(session, Packet(43, new StageBeginReq
            { StageId = 10101, AvatarIdList = { team.AvatarIdList }, AvatarTeamType = AvatarTeamType.AvatarTeamNormal }));
        Assert.Equal(StageBeginRsp.Types.Retcode.Succ,
            StageBeginRsp.Parser.ParseFrom(Assert.Single(begin.Replies, p => p.CommandId == 44).Body).Retcode);
        Assert.Equal(74u, store.Read(10001).Stamina);
    }
}
