using BH3.Game.Campaign;
using BH3.Game.Messaging;
using BH3.Game.Sessions;
using BH3.Protocol;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Lobby;

public sealed partial class LobbyHandlers
{
    private readonly CampaignService campaign = new(store, clock ?? TimeProvider.System);
    private IGameMessageHandler Change<T>(ushort command, ushort response, MessageParser<T> parser,
        Func<uint, T, IMessage> action) where T : IMessage<T> => new Handler(command, SessionState.Authenticated, (session, packet) =>
    {
        var result = action(Uid(session), parser.ParseFrom(packet.Body));
        var replies = new List<GamePacket> { GamePacketCodec.Reply(packet, response, result.ToByteArray(), Uid(session)) };
        if (Convert.ToInt32(result.Descriptor.FindFieldByName("retcode").Accessor.GetValue(result)) == 0)
        {
            // Publish authoritative snapshots so the next page uses the committed wallet/progress.
            replies.Add(GamePacketCodec.Reply(packet, 11, Main(session).ToByteArray(), Uid(session)));
            replies.Add(GamePacketCodec.Reply(packet, 42, campaign.Stages(Uid(session), new()).ToByteArray(), Uid(session)));
            replies.Add(GamePacketCodec.Reply(packet, 113, campaign.Missions(Uid(session)).ToByteArray(), Uid(session)));
            replies.Add(GamePacketCodec.Reply(packet, 25, Avatars(session, new()).ToByteArray(), Uid(session)));
            replies.Add(GamePacketCodec.Reply(packet, 27, Equipment(session, new()).ToByteArray(), Uid(session)));
        }
        return replies;
    });
    private IEnumerable<IGameMessageHandler> CreateCampaignHandlers()
    {
        yield return Respond(41, 42, GetStageDataReq.Parser, (s, r) => campaign.Stages(Uid(s), r));
        yield return Change(43, 44, StageBeginReq.Parser, campaign.Begin);
        yield return Change(45, 46, StageEndReq.Parser, campaign.End);
        yield return new Handler(49, SessionState.Authenticated, (s, p) => { campaign.SetTeam(Uid(s), UpdateAvatarTeamNotify.Parser.ParseFrom(p.Body)); return []; });
        yield return Respond(60, 61, GetStageDropDisplayReq.Parser, (_, r) => campaign.Drops(r));
        yield return Respond(112, 113, GetMissionDataReq.Parser, (s, _) => campaign.Missions(Uid(s)));
        yield return Change(114, 115, GetMissionRewardReq.Parser, campaign.Claim);
        yield return Respond(117, 118, UpdateMissionProgressReq.Parser, (s, r) => campaign.Update(Uid(s), r));
        yield return Respond(456, 457, GetStageActDifficultyReq.Parser, (s, _) => campaign.Acts(Uid(s)));
        yield return Change(458, 459, TakeStageActChallengeRewardReq.Parser, campaign.ClaimAct);
        yield return Respond(965, 966, GetStageChapterReq.Parser, (s, _) => campaign.Chapters(Uid(s)));
        yield return Respond(1541, 1542, GetStageRecommendAvatarReq.Parser, (_, r) => new GetStageRecommendAvatarRsp {
            Retcode = r.IdList.Count > 64 ? GetStageRecommendAvatarRsp.Types.Retcode.IdTooMuch : GetStageRecommendAvatarRsp.Types.Retcode.Succ,
            StageRecommendAvatarList = { r.IdList.Take(64).Select(id => new StageRecommendAvatar { Id = id, Type = r.Type }) } });
        yield return Respond(502, 503, GetExtraStoryChallengeModeDataReq.Parser, (_, r) => new GetExtraStoryChallengeModeDataRsp {
            Retcode = GetExtraStoryChallengeModeDataRsp.Types.Retcode.ExtraStoryNotOpen, ChapterId = r.ChapterId, IsCanReset = false });
        yield return Respond(813, 814, GetGalInteractTriggerEventReq.Parser, (s, r) => new GetGalInteractTriggerEventRsp {
            Retcode = r.AvatarId == store.Read(Uid(s)).AvatarId ? GetGalInteractTriggerEventRsp.Types.Retcode.Succ : GetGalInteractTriggerEventRsp.Types.Retcode.NoSuchAvatar,
            AvatarId = r.AvatarId, EventId = 0 });
        yield return Respond(476, 477, GetBuffEffectReq.Parser, (_, _) => new GetBuffEffectRsp { Retcode = GetBuffEffectRsp.Types.Retcode.Succ });
        yield return Respond(121, 122, GetSignInRewardStatusReq.Parser, (_, _) => new GetSignInRewardStatusRsp {
            Retcode = GetSignInRewardStatusRsp.Types.Retcode.Succ, IsNeedSignIn = false, NextSignInDay = 0, NextSignInRewardId = 0 });
        yield return Respond(6706, 6707, GetProductListReq.Parser, (_, _) => new GetProductListRsp { Retcode = GetProductListRsp.Types.Retcode.Succ });
        yield return Respond(4167, 4168, GetContinuousRechargeActivityReq.Parser, (_, _) => new GetContinuousRechargeActivityRsp {
            Retcode = GetContinuousRechargeActivityRsp.Types.Retcode.Succ, ActivityId = 0, Progress = 0, TodayVipPoint = 0 });
        yield return Respond(3460, 3461, GreedyEndlessTakeRankRewardReq.Parser, (_, _) => new GreedyEndlessTakeRankRewardRsp {
            Retcode = GreedyEndlessTakeRankRewardRsp.Types.Retcode.NoReward, RewardData = new RewardData() });
    }
    private GetAvatarDataRsp Avatars(GameSession session, GetAvatarDataReq request)
    {
        var state = store.Read(Uid(session)); var result = new GetAvatarDataRsp { Retcode = GetAvatarDataRsp.Types.Retcode.Succ, IsAll = RequestsAll(request.AvatarIdList) };
        if (result.IsAll || request.AvatarIdList.Contains(state.AvatarId)) result.AvatarList.Add(new Avatar {
            AvatarId = state.AvatarId, Star = 1, Level = Math.Max(1, state.AvatarLevel), Exp = state.AvatarExp,
            WeaponUniqueId = 1, DressId = state.DressId, DressList = { state.DressId } });
        return result;
    }
    private GetEquipmentDataRsp Equipment(GameSession session, GetEquipmentDataReq request)
    {
        var state = store.Read(Uid(session)); var result = new GetEquipmentDataRsp { Retcode = GetEquipmentDataRsp.Types.Retcode.Succ,
            IsAll = RequestsAll(request.WeaponUniqueIdList) && RequestsAll(request.StigmataUniqueIdList) && RequestsAll(request.MaterialIdList) && RequestsAll(request.MechaUniqueIdList) };
        if (result.IsAll || request.WeaponUniqueIdList.Contains(0) || request.WeaponUniqueIdList.Contains(1))
            result.WeaponList.Add(new Weapon { UniqueId = 1, Id = state.WeaponId, Level = 1, IsProtected = true });
        result.MaterialList.Add(campaign.Materials(Uid(session)).Where(m => RequestsAll(request.MaterialIdList) || request.MaterialIdList.Contains(m.Id)));
        return result;
    }
}
