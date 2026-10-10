using System.Security.Cryptography;
using System.Text.Json;
using BH3.Game.Messaging;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Lobby;

public sealed partial class LobbyHandlers(LobbyStore store, IPlayerStore profiles, byte[] authenticationKey, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static uint Uid(GameSession session) => checked((uint)(session.PlayerId ?? throw new InvalidOperationException("Not authenticated.")));
    private uint Now => checked((uint)time.GetUtcNow().ToUnixTimeSeconds());
    // Client 9.1 startup senders put ID 0 in the list to request a full snapshot.
    private static bool RequestsAll(ICollection<uint> ids) => ids.Count == 0 || ids.Contains(0u);
    private static LobbyState Initial(uint now)
    {
        using var source = typeof(LobbyHandlers).Assembly.GetManifestResourceStream("BH3.Game.Lobby.starter.json")!;
        var seed = JsonSerializer.Deserialize<LobbyState>(source, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return seed with { RegisteredAt = now };
    }
    private sealed class Handler(ushort command, SessionState state, Func<GameSession, GamePacket, IReadOnlyList<GamePacket>> action, bool anyState = false) : IGameMessageHandler
    {
        public ushort CommandId => command;
        public SessionState RequiredState => state;
        public bool Accepts(SessionState current) => anyState || current == state;
        public IReadOnlyList<GamePacket> Handle(GameSession session, GamePacket packet) => action(session, packet);
    }
    private static IGameMessageHandler Respond<T>(ushort command, ushort response, MessageParser<T> parser, Func<GameSession, T, IMessage> action,
        SessionState state = SessionState.Authenticated, bool anyState = false) where T : IMessage<T> =>
        new Handler(command, state, (session, packet) => [GamePacketCodec.Reply(packet, response, action(session, parser.ParseFrom(packet.Body)).ToByteArray(), checked((uint)(session.PlayerId ?? session.PendingPlayerId ?? 0)))], anyState);

    public IEnumerable<IGameMessageHandler> Create()
    {
        foreach (var handler in CreateSystemsHandlers()) yield return handler;
        foreach (var handler in CreateStartupHandlers()) yield return handler;
        foreach (var handler in CreateCampaignHandlers()) yield return handler;
        foreach (var handler in CreateOperationsHandlers()) yield return handler;
        foreach (var handler in CreateChallengeHandlers()) yield return handler;
        foreach (var handler in CreateCompanionHandlers()) yield return handler;
        yield return Respond(4, 5, GetPlayerTokenReq.Parser, Token, SessionState.Connected);
        yield return Respond(6, 7, PlayerLoginReq.Parser, Login, SessionState.TokenIssued);
        yield return new Handler(1, SessionState.Authenticated, (_, packet) => { KeepAliveNotify.Parser.ParseFrom(packet.Body); return []; }, true);
        yield return Respond(803, 804, SyncTimeReq.Parser, (_, request) => new SyncTimeRsp { Retcode = SyncTimeRsp.Types.Retcode.Succ, CurTime = Now, Seq = request.Seq }, anyState: true);
        yield return Respond(10, 11, GetMainDataReq.Parser, (session, _) => Main(session));
        yield return Respond(24, 25, GetAvatarDataReq.Parser, Avatars);
        yield return Respond(26, 27, GetEquipmentDataReq.Parser, Equipment);
        // 9.1 resolves every returned member to an owned avatar. Zero padding fails
        // prepare-page validation and makes each response trigger another team query.
        yield return Respond(47, 48, GetAvatarTeamDataReq.Parser, (session, _) => new GetAvatarTeamDataRsp { Retcode = GetAvatarTeamDataRsp.Types.Retcode.Succ,
            AvatarTeamList = { new uint[] { 1, 60 }.Select(type => new AvatarTeam { StageType = type, AvatarIdList = { campaign.Team(Uid(session), type) } }) } });
        yield return Respond(110, 111, GetConfigReq.Parser, (session, _) => new GetConfigRsp { Retcode = GetConfigRsp.Types.Retcode.Succ,
            StaminaRecoverConfigTime = 360, ServerCurTime = Now, DayTimeOffset = 14400, RegionName = "pc01", MaxFriendNum = 50, ScoinLimit = 999999999, NextDayBeginTime = ((Now + 14400) / 86400 + 1) * 86400 - 14400, BulletinActivityList = { systems.ActivityConfigs(Uid(session)) } });
        yield return Respond(127, 128, GetFinishGuideDataReq.Parser, (session, _) => new GetFinishGuideDataRsp { Retcode = GetFinishGuideDataRsp.Types.Retcode.Succ, GuideIdList = { store.Read(Uid(session)).CompletedGuides } });
        yield return Respond(1586, 1587, GetClientDataReq.Parser, (session, request) => new GetClientDataRsp { Retcode = GetClientDataRsp.Types.Retcode.Succ, Type = request.Type, Id = request.Id,
            ClientDataList = { store.ReadClient(Uid(session), (int)request.Type, request.Id).Select(v => new ClientData { Type = (ClientDataType)v.Type, Id = v.Id, Data = ByteString.CopyFrom(v.Data) }) } });
        yield return Respond(1588, 1589, SetClientDataReq.Parser, (session, request) =>
        {
            if (request.ClientData is null || request.ClientData.Data.Length > 65536) return new SetClientDataRsp { Retcode = SetClientDataRsp.Types.Retcode.Fail };
            store.WriteClient(Uid(session), new((int)request.ClientData.Type, request.ClientData.Id, request.ClientData.Data.ToByteArray()));
            return new SetClientDataRsp { Retcode = SetClientDataRsp.Types.Retcode.Succ, Type = request.ClientData.Type, Id = request.ClientData.Id };
        });
        yield return Respond(1270, 1272, GetClientSettingReq.Parser, (_, request) => new GetClientSettingRsp { Retcode = GetClientSettingRsp.Types.Retcode.Succ, ClientSettingType = request.ClientSettingType });
        yield return Respond(5454, 5455, GetWarshipDataReq.Parser, (_, _) => new GetWarshipDataRsp { Retcode = GetWarshipDataRsp.Types.Retcode.Succ, IsAll = true, WarshipList = { new WarshipThemeData { WarshipId = 0 } } });
        yield return Respond(5450, 5451, GetWarshipItemDataReq.Parser, (_, _) => new GetWarshipItemDataRsp { Retcode = GetWarshipItemDataRsp.Types.Retcode.Succ, IsAll = true });
        yield return Respond(1523, 1524, GetCustomHeadDataReq.Parser, (_, _) => new GetCustomHeadDataRsp { Retcode = GetCustomHeadDataRsp.Types.Retcode.Succ, IsAll = true });
        yield return Respond(590, 591, GetFrameDataReq.Parser, (s, _) => operations.Frames(Uid(s)));
        yield return Respond(231, 232, GetExtraStoryDataReq.Parser, (_, _) => new GetExtraStoryDataRsp { Retcode = GetExtraStoryDataRsp.Types.Retcode.Succ });
        yield return Respond(4192, 4193, GetLoginActivityReq.Parser, (_, _) => new GetLoginActivityRsp { Retcode = GetLoginActivityRsp.Types.Retcode.Succ });
        yield return Respond(1705, 1706, GetActivityMainDataReq.Parser, (_, _) => new GetActivityMainDataRsp { Retcode = GetActivityMainDataRsp.Types.Retcode.Succ });
        yield return Respond(104, 105, GetHasGotItemIdListReq.Parser, (session, _) =>
        {
            var equipment = Equipment(session, new());
            return new GetHasGotItemIdListRsp { Retcode = GetHasGotItemIdListRsp.Types.Retcode.Succ,
                ItemIdList = { equipment.WeaponList.Select(w => w.Id).Concat(equipment.StigmataList.Select(s => s.Id)).Distinct() } };
        });
        yield return Respond(619, 620, GetHasGotFurnitureIdListReq.Parser, (_, _) => new GetHasGotFurnitureIdListRsp { Retcode = GetHasGotFurnitureIdListRsp.Types.Retcode.Succ });
    }
    private IMessage Token(GameSession session, GetPlayerTokenReq request)
    {
        LocalIdentity? identity = null;
        foreach (string ticket in new[] { request.ComboToken, request.AccountToken, request.Token })
            if (LocalTicket.TryValidate(authenticationKey, ticket, time.GetUtcNow(), out identity)) break;
        if (identity is null || request.AccountUid != identity.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return new GetPlayerTokenRsp { Retcode = GetPlayerTokenRsp.Types.Retcode.AccountVerifyError };
        if (request.Version.Length > 0 && !request.Version.StartsWith("9.1.0", StringComparison.Ordinal))
            return new GetPlayerTokenRsp { Retcode = GetPlayerTokenRsp.Types.Retcode.ForceUpdate };
        store.EnsurePlayer(identity.Uid, identity.Name, Initial(Now));
        session.IssueToken(identity.Uid, request.AccountType);
        return new GetPlayerTokenRsp { Retcode = GetPlayerTokenRsp.Types.Retcode.Succ, Uid = identity.Uid, AccountUid = request.AccountUid,
            AccountType = request.AccountType, Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() };
    }
    private IMessage Login(GameSession session, PlayerLoginReq request)
    {
        uint uid = checked((uint)session.PendingPlayerId!);
        if (request.AccountUid.Length > 0 && request.AccountUid != uid.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return new PlayerLoginRsp { Retcode = PlayerLoginRsp.Types.Retcode.Fail };
        session.Authenticate(uid);
        return new PlayerLoginRsp { Retcode = PlayerLoginRsp.Types.Retcode.Succ, RegionId = 248, RegionName = "pc01", IsFirstLogin = false,
            CgType = CGType.CgSevenChapter, AccountType = session.AccountType, AccountUid = uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            LoginSessionToken = checked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue)), IsPacketCacheEmpty = true };
    }
    private GetMainDataRsp Main(GameSession session)
    {
        uint uid = Uid(session); var state = campaign.Refresh(uid); var profile = profiles.Find(uid)!;
        return new GetMainDataRsp { Retcode = GetMainDataRsp.Types.Retcode.Succ, Nickname = profile.Nickname, Level = state.Level, Stamina = state.Stamina, Exp = state.Exp, Scoin = state.Scoin, Hcoin = state.Hcoin, Mcoin = operations.Mcoin(uid),
            AssistantAvatarId = state.AvatarId, StaminaRecoverConfigTime = 360, StaminaRecoverLeftTime = 360, EquipmentSizeLimit = 2300, IsAll = true,
            SelfDesc = "BH3 Local", RegisterTime = state.RegisteredAt, TotalLoginDays = 1, LevelLockId = 1,
            WarshipAvatar = new WarshipAvatarData { WarshipFirstAvatarId = state.AvatarId }, WarshipTheme = new WarshipThemeData { WarshipId = 0 },
            TypeList = { Enumerable.Range(2, 38).Select(v => (uint)v) } };
    }
}
