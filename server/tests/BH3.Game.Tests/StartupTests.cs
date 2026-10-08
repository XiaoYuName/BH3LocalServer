using System.Buffers.Binary;
using System.Text.Json;
using BH3.Game.Lobby;
using BH3.Game.Messaging;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;
using BH3.Protocol.Messages;
using BH3.Tests;
using Google.Protobuf;

namespace BH3.Game.Tests;

public sealed class StartupTests : IDisposable
{
    private readonly TestDirectory temp = new();
    private readonly LobbyStore store;
    private readonly GameDispatcher dispatcher;
    private readonly GameSession session = new(1, "test", 1);
    private readonly SqliteConnectionFactory factory;
    public StartupTests()
    {
        factory = new(Path.Combine(temp.Path, "players.db")); SchemaMigrator.Initialize(factory);
        store = new(factory);
        foreach (uint uid in new[] { 10001u, 10002u }) store.EnsurePlayer(uid, "captain", new(1, 80, 101, 20001, 59101, 1, [1, 2]));
        session.Authenticate(10001);
        dispatcher = new(new LobbyHandlers(store, new SqlitePlayerStore(factory), new byte[32]).Create());
    }
    private static GamePacket Packet(ushort id, IMessage body)
    {
        byte[] prefix = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(prefix, GamePacketCodec.Head);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(12), 10002); // Claimed UID must not select another player's data.
        return new(prefix, id, [], body.ToByteArray());
    }
    private GamePacket Send(ushort command, IMessage request) => Assert.Single(dispatcher.Dispatch(session, Packet(command, request)).Replies);
    public sealed record StartupRequest(ushort Request, string RequestType, ushort Response, string ResponseType);

    [Fact]
    public void ObservedStartupRequestsGetTheirDeclaredResponsesOnlyAfterAuthentication()
    {
        // Fixture derives from the user command trace and independent reference command enum.
        // Original payloads were not logged: this checks default requests plus separate selector tests below.
        var fixture = JsonSerializer.Deserialize<StartupRequest[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "startup-requests.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(129, fixture.Length);
        var types = LobbyReflection.Descriptor.MessageTypes.Concat(Lobby91Reflection.Descriptor.MessageTypes).ToDictionary(d => d.Name);
        foreach (var item in fixture)
        {
            var request = types[item.RequestType].Parser.ParseFrom(Array.Empty<byte>());
            var unauthenticated = new GameSession(2, "untrusted", 1);
            Assert.Equal(DispatchStatus.InvalidState, dispatcher.Dispatch(unauthenticated, Packet(item.Request, request)).Status);
            var response = Send(item.Request, request);
            Assert.Equal(item.Response, response.CommandId); Assert.Equal(10001u, response.ClaimedUserId);
            var decoded = types[item.ResponseType].Parser.ParseFrom(response.Body);
            var retcode = decoded.Descriptor.FindFieldByName("retcode");
            if (retcode is not null) Assert.True(retcode.Accessor.HasValue(decoded), item.ResponseType);
        }
        Assert.Equal(DispatchStatus.Unsupported, dispatcher.Dispatch(session, Packet(65000, new GetMainDataReq())).Status);
    }

    [Fact]
    public void Client91OverallStartupQueryReceivesCompleteEmptySnapshot()
    {
        // Derived independently from 9.1 startup sender + protobuf writer; user log records length 2 only.
        var request = PjmsGetOverallReq.Parser.ParseFrom(new byte[] { 0x10, 0x01 });
        Assert.True(request.IsAll); Assert.Empty(request.OverallIdList);
        var packet = Send(7842, request);
        Assert.Equal(7843, packet.CommandId);
        Assert.Equal(new byte[] { 0x08, 0x00, 0x18, 0x01 }, packet.Body);
        var response = PjmsGetOverallRsp.Parser.ParseFrom(packet.Body);
        Assert.True(response.HasRetcode); Assert.Equal(PjmsGetOverallRsp.Types.Retcode.Succ, response.Retcode);
        Assert.True(response.HasIsAll); Assert.True(response.IsAll); Assert.Empty(response.OverallList);

        var selected = PjmsGetOverallRsp.Parser.ParseFrom(Send(7842, new PjmsGetOverallReq { OverallIdList = { 123 } }).Body);
        Assert.False(selected.IsAll); Assert.Empty(selected.OverallList);
        // Repeated initial queries are idempotent and cannot invent progress.
        Assert.Equal(packet.Body, Send(7842, request).Body);
    }

    [Fact]
    public void ZeroIdStartupQueriesCompleteClient91InitializationWithOwnedData()
    {
        // Static-derived 9.1 inputs; the user's trace recorded only payload lengths.
        byte[] allIds = [0x08, 0x00];
        var avatar = GetAvatarDataRsp.Parser.ParseFrom(Send(24, GetAvatarDataReq.Parser.ParseFrom(allIds)).Body);
        Assert.True(avatar.IsAll); Assert.Equal(101u, Assert.Single(avatar.AvatarList).AvatarId);
        var equipment = GetEquipmentDataRsp.Parser.ParseFrom(Send(26,
            GetEquipmentDataReq.Parser.ParseFrom(new byte[] { 0x08, 0, 0x10, 0, 0x18, 0, 0x20, 0 })).Body);
        Assert.True(equipment.IsAll); Assert.Equal(20001u, Assert.Single(equipment.WeaponList).Id);
        Assert.True(GetMedalDataRsp.Parser.ParseFrom(Send(449, GetMedalDataReq.Parser.ParseFrom(allIds)).Body).IsAll);
        Assert.True(GetGrandKeyRsp.Parser.ParseFrom(Send(506, GetGrandKeyReq.Parser.ParseFrom(allIds)).Body).IsAll);
        Assert.True(GetAvatarRollDataRsp.Parser.ParseFrom(Send(643, GetAvatarRollDataReq.Parser.ParseFrom(allIds)).Body).IsAll);
        Assert.True(GetPhonePendantDataRsp.Parser.ParseFrom(Send(1197, GetPhonePendantDataReq.Parser.ParseFrom(allIds)).Body).IsAll);
    }

    [Fact]
    public void SelectedIdsDoNotCompleteFullInitializationOrReturnUnrequestedItems()
    {
        var selected = GetAvatarDataRsp.Parser.ParseFrom(Send(24, new GetAvatarDataReq { AvatarIdList = { 101 } }).Body);
        Assert.False(selected.IsAll); Assert.Single(selected.AvatarList);
        var missing = GetAvatarDataRsp.Parser.ParseFrom(Send(24, new GetAvatarDataReq { AvatarIdList = { 999999 } }).Body);
        Assert.False(missing.IsAll); Assert.Empty(missing.AvatarList);
        var mixed = GetAvatarDataRsp.Parser.ParseFrom(Send(24, new GetAvatarDataReq { AvatarIdList = { 0, 101, 0 } }).Body);
        Assert.True(mixed.IsAll); Assert.Single(mixed.AvatarList);

        var partial = GetEquipmentDataRsp.Parser.ParseFrom(Send(26, new GetEquipmentDataReq
            { WeaponUniqueIdList = { 0 }, MaterialIdList = { 999999 } }).Body);
        Assert.False(partial.IsAll); Assert.Equal(20001u, Assert.Single(partial.WeaponList).Id);
        var materials = GetEquipmentDataRsp.Parser.ParseFrom(Send(26, new GetEquipmentDataReq { MaterialIdList = { 999999 } }).Body);
        Assert.False(materials.IsAll); Assert.Empty(materials.WeaponList);
        var owned = GetEquipmentDataRsp.Parser.ParseFrom(Send(26, new GetEquipmentDataReq { WeaponUniqueIdList = { 1 } }).Body);
        Assert.False(owned.IsAll); Assert.Single(owned.WeaponList);
        Assert.False(GetMedalDataRsp.Parser.ParseFrom(Send(449, new GetMedalDataReq { MedalIdList = { 123 } }).Body).IsAll);
        Assert.False(GetGrandKeyRsp.Parser.ParseFrom(Send(506, new GetGrandKeyReq { KeyIdList = { 123 } }).Body).IsAll);
        Assert.False(GetAvatarRollDataRsp.Parser.ParseFrom(Send(643, new GetAvatarRollDataReq { AvatarIdList = { 123 } }).Body).IsAll);
        Assert.False(GetPhonePendantDataRsp.Parser.ParseFrom(Send(1197, new GetPhonePendantDataReq { PhonePendantIdList = { 123 } }).Body).IsAll);
    }

    [Fact]
    public void GuideReportsMergePersistAndStayAccountScoped()
    {
        var result = FinishGuideReportRsp.Parser.ParseFrom(Send(129, new FinishGuideReportReq { GuideIdList = { 2, 999001, 999001 } }).Body);
        Assert.Equal(FinishGuideReportRsp.Types.Retcode.Succ, result.Retcode); Assert.True(result.IsFinish);
        Assert.Equal(new uint[] { 2, 999001 }, result.GuideIdList);
        Assert.Equal(new uint[] { 1, 2, 999001 }, new LobbyStore(factory).Read(10001).CompletedGuides);
        Assert.Equal(new uint[] { 1, 2 }, store.Read(10002).CompletedGuides);
        var invalid = FinishGuideReportRsp.Parser.ParseFrom(Send(129, new FinishGuideReportReq { GuideIdList = { 0, 999002 } }).Body);
        Assert.Equal(FinishGuideReportRsp.Types.Retcode.Fail, invalid.Retcode);
        Assert.DoesNotContain(999002u, store.Read(10001).CompletedGuides);
    }

    [Fact]
    public void SelectorsAndEmptyPaginationCompleteInitialization()
    {
        var mail = GetClientMailDataRsp.Parser.ParseFrom(Send(3800, new GetClientMailDataReq { Start = 7, Stop = 30, FilterType = (ClientMailFilterType)1 }).Body);
        Assert.True(mail.IsEnd); Assert.Equal(7u, mail.Start); Assert.Equal(1, (int)mail.FilterType); Assert.NotNull(mail.ClientMailInfo);
        Assert.True(GetFriendListRsp.Parser.ParseFrom(Send(64, new GetFriendListReq()).Body).IsWholeData);
        Assert.True(GetShopListRsp.Parser.ParseFrom(Send(6700, new GetShopListReq()).Body).IsAll);
        var medal = GetMedalDataRsp.Parser.ParseFrom(Send(449, new GetMedalDataReq { MedalIdList = { 123 } }).Body);
        Assert.False(medal.IsAll);
        var theme = GetMissionThemeDataRsp.Parser.ParseFrom(Send(4205, new GetMissionThemeDataReq { ThemeId = 13, IsGetAll = false }).Body);
        Assert.Equal(13u, theme.ThemeId); Assert.False(theme.IsGetAll);
        var group = ChapterGroupGetDataRsp.Parser.ParseFrom(Send(1660, new ChapterGroupGetDataReq { ChapterGroupId = 17 }).Body);
        Assert.Equal(17u, group.ChapterGroupId); Assert.False(group.IsAll);
        Assert.Equal(42u, ReportClientDataVersionRsp.Parser.ParseFrom(Send(398, new ReportClientDataVersionReq { Version = 42 }).Body).ServerVersion);
        Assert.Equal(GetAuthkeyRsp.Types.Retcode.Fail, GetAuthkeyRsp.Parser.ParseFrom(Send(5010, new GetAuthkeyReq()).Body).Retcode);
        Assert.Equal(UpdateMissionProgressRsp.Types.Retcode.FinishWayError, UpdateMissionProgressRsp.Parser.ParseFrom(Send(117, new UpdateMissionProgressReq()).Body).Retcode);
    }

    [Fact]
    public void DormDisplayReferencesAnOwnedRoomAndAvatar()
    {
        var dorm = GetDormDataRsp.Parser.ParseFrom(Send(601, new GetDormDataReq()).Body);
        var house = Assert.Single(dorm.HouseList, h => h.Id == dorm.ShowHouse);
        var room = Assert.Single(house.RoomList, r => r.Id == dorm.ShowRoom);
        Assert.Contains(dorm.VisitAvatar, room.AvatarList); Assert.Contains(dorm.VisitAvatar, dorm.UnlockAvatarList);
        Assert.Equal(store.Read(10001).AvatarId, dorm.VisitAvatar);
    }

    [Fact]
    public void PlayerCardHasTheSlotsReadUnconditionallyByClient91()
    {
        var card = GetPlayerCardRsp.Parser.ParseFrom(Send(480, new GetPlayerCardReq()).Body);
        Assert.Equal(new uint[] { 101, 0, 0 }, card.AvatarIdList);
        Assert.Equal(new uint[] { 0, 0 }, card.MedalIdList);
        Assert.Equal(2, card.MedalList.Count); Assert.All(card.MedalList, m => Assert.Equal(0u, m.Id));
        Assert.Equal(new uint[] { 0 }, card.ElfIdList); Assert.NotNull(card.MsgData);
        // Empty display slots must not create additional owned items.
        Assert.Equal(101u, store.Read(10001).AvatarId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GachaAllQueryNeverSerializesSyntheticZeroEnum(bool explicitZero)
    {
        var request = new GetGachaDisplayReq { IsAll = true };
        if (explicitZero) request.Type = (GachaType)0;
        var response = GetGachaDisplayRsp.Parser.ParseFrom(Send(4702, request).Body);
        Assert.True(response.IsAll); Assert.False(response.HasType);
        Assert.Equal(GetGachaDisplayRsp.Types.Retcode.Succ, response.Retcode);
    }

    [Fact]
    public void GachaTypedQueryPreservesValidSelectorAndRejectsUnknownEnum()
    {
        var valid = GetGachaDisplayRsp.Parser.ParseFrom(Send(4702, new GetGachaDisplayReq { Type = GachaType.GachaHcoin }).Body);
        Assert.True(valid.HasType); Assert.Equal(GachaType.GachaHcoin, valid.Type); Assert.False(valid.IsAll);
        var invalid = GetGachaDisplayRsp.Parser.ParseFrom(Send(4702, new GetGachaDisplayReq { Type = (GachaType)999 }).Body);
        Assert.Equal(GetGachaDisplayRsp.Types.Retcode.Fail, invalid.Retcode); Assert.False(invalid.HasType);
    }

    [Fact]
    public void InactiveFeatureResponsesStillContainObjectsDereferencedByClient91()
    {
        var boss = GetExBossInfoRsp.Parser.ParseFrom(Send(510, new GetExBossInfoReq()).Body);
        Assert.Equal(GetExBossInfoRsp.Types.Retcode.NotOpen, boss.Retcode);
        Assert.NotNull(boss.BossInfo); Assert.Empty(boss.BossInfo.BossIdList); Assert.Equal(0u, boss.BossInfo.CurMaxEnterTimes);
        var mecha = GetOpenworldMechaDefenseRsp.Parser.ParseFrom(Send(4514, new GetOpenworldMechaDefenseReq()).Body);
        Assert.NotNull(mecha.MechaDefense); Assert.Equal(0u, mecha.MechaDefense.LeftEnterTimes);
    }
    public void Dispose() => temp.Dispose();
}
