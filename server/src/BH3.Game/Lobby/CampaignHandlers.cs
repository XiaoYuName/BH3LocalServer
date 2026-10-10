using BH3.Game.Campaign;
using BH3.Game.Operations;
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
        if (result is GetVipRewardRsp { Retcode: GetVipRewardRsp.Types.Retcode.Succ or GetVipRewardRsp.Types.Retcode.RepeatGet })
        {
            replies.Insert(0,GamePacketCodec.Reply(packet,6718,operations.VipRewards(Uid(session)).ToByteArray(),Uid(session)));
            replies.Insert(1,GamePacketCodec.Reply(packet,CommandIds.GetMedalDataRsp,operations.Medals(Uid(session),new()).ToByteArray(),Uid(session)));
            replies.Insert(2,GamePacketCodec.Reply(packet,591,operations.Frames(Uid(session)).ToByteArray(),Uid(session)));
        }
        if (result is TakeStageActChallengeRewardRsp { Retcode: TakeStageActChallengeRewardRsp.Types.Retcode.Succ
                or TakeStageActChallengeRewardRsp.Types.Retcode.HasTake })
        {
            // 9.1 redraws the reward entry on 459 using LevelModule's 457 cache.
            // Publish the committed claim flags BEFORE that callback, including on
            // retries from a stale page. HasTake only repairs the cache; no new grant.
            replies.Insert(0, GamePacketCodec.Reply(packet, 457, campaign.Acts(Uid(session)).ToByteArray(), Uid(session)));
        }
        if (Convert.ToInt32(result.Descriptor.FindFieldByName("retcode").Accessor.GetValue(result)) == 0 || result is TakeClientMailAttachmentRsp { Retcode: TakeClientMailAttachmentRsp.Types.Retcode.PartFail })
        {
            // Publish authoritative snapshots so the next page uses the committed wallet/progress.
            if(result is GetMissionRewardRsp or BuyBattlePassTicketRsp or TakeBattlePassLevelRewardRsp or BuyBattlePassLevelRsp or TakeBattlePassPhaseExpRsp or TakeDutyRewardRsp)
            {
            replies.Insert(0,GamePacketCodec.Reply(packet,3751,systems.Pass(Uid(session)).ToByteArray(),Uid(session)));
            replies.Insert(1,GamePacketCodec.Reply(packet,969,systems.Duty(Uid(session)).ToByteArray(),Uid(session)));
            }
            if(result.Descriptor.Name.StartsWith("GrandKey",StringComparison.Ordinal))
                replies.Insert(0,GamePacketCodec.Reply(packet,507,systems.GrandKeys(Uid(session),new()).ToByteArray(),Uid(session)));
            if(result is TakeWikiRatingRewardRsp)
            {
                replies.Insert(0,GamePacketCodec.Reply(packet,1194,systems.Wiki(Uid(session)).ToByteArray(),Uid(session)));
                replies.Insert(1,GamePacketCodec.Reply(packet,1198,PhonePendants(Uid(session),new()).ToByteArray(),Uid(session)));
            }
            if(result is ElfStarUpRsp or AddElfExpByMaterialRsp or ElfSkillLevelUpRsp or ElfFragmentTransformRsp or SwitchElfSkillRsp or GachaRsp or TakeClientMailAttachmentRsp or BuyGoodsRsp)
            {
                var elf=companions.Get(Uid(session));
                replies.Insert(0,GamePacketCodec.Reply(packet,2102,new SyncElfDataNotify{ElfList={elf.ElfList}}.ToByteArray(),Uid(session)));
                replies.Insert(1,GamePacketCodec.Reply(packet,2103,new SyncElfFragmentNotify{ElfFragmentList={elf.ElfFragmentList}}.ToByteArray(),Uid(session)));
            }
            replies.Add(GamePacketCodec.Reply(packet, 11, Main(session).ToByteArray(), Uid(session)));
            replies.Add(GamePacketCodec.Reply(packet, 42, campaign.Stages(Uid(session), new()).ToByteArray(), Uid(session)));
            replies.Add(GamePacketCodec.Reply(packet, 113, campaign.Missions(Uid(session)).ToByteArray(), Uid(session)));
            replies.Add(GamePacketCodec.Reply(packet, 25, Avatars(session, new()).ToByteArray(), Uid(session)));
            replies.Add(GamePacketCodec.Reply(packet, 27, Equipment(session, new()).ToByteArray(), Uid(session)));
            if(result is BuyGoodsRsp)
            {
                replies.Insert(0,GamePacketCodec.Reply(packet,6701,operations.Shops(Uid(session)).ToByteArray(),Uid(session)));
                replies.Insert(1,GamePacketCodec.Reply(packet,6703,operations.ShoppingMall(Uid(session)).ToByteArray(),Uid(session)));
            }
            if(result is TakeCardProductDailyRewardRsp or TakeCardProductBonusRewardRsp or ExchangeHcoinByMcoinRsp)
            {
                replies.Insert(0,GamePacketCodec.Reply(packet,6722,operations.CardInfo(Uid(session)).ToByteArray(),Uid(session)));
                replies.Insert(1,GamePacketCodec.Reply(packet,6707,operations.ProductList(Uid(session)).ToByteArray(),Uid(session)));
            }
        }
        return replies;
    });
    private IEnumerable<IGameMessageHandler> CreateCampaignHandlers()
    {
        yield return Respond(41, 42, GetStageDataReq.Parser, (s, r) => campaign.Stages(Uid(s), r));
        yield return Change(43, 44, StageBeginReq.Parser, campaign.Begin);
        yield return Change(45, 46, StageEndReq.Parser, campaign.End);
        yield return Respond(CommandIds.StageInnerDataReportReq, CommandIds.StageInnerDataReportRsp, StageInnerDataReportReq.Parser,
            (_, _) => new StageInnerDataReportRsp { Retcode = StageInnerDataReportRsp.Types.Retcode.Succ });
        // Affection progression is not implemented. This callback never settles a stage.
        yield return Respond(CommandIds.AddGoodfeelReq, CommandIds.AddGoodfeelRsp, AddGoodfeelReq.Parser,
            (s, r) => new AddGoodfeelRsp { Retcode = campaign.OwnedAvatars(Uid(s)).Contains(r.AvatarId)
                ? AddGoodfeelRsp.Types.Retcode.Fail : AddGoodfeelRsp.Types.Retcode.AvatarNotExist });
        yield return Respond(CommandIds.PjmsGetCurWorldReq, CommandIds.PjmsGetCurWorldRsp, PjmsGetCurWorldReq.Parser,
            (_, _) => new PjmsGetCurWorldRsp { Retcode = PjmsGetCurWorldRsp.Types.Retcode.Succ, World = InactivePjmsWorld() });
        yield return new Handler(49, SessionState.Authenticated, (s, p) => { campaign.SetTeam(Uid(s), UpdateAvatarTeamNotify.Parser.ParseFrom(p.Body)); return []; });
        yield return Respond(60, 61, GetStageDropDisplayReq.Parser, (_, r) => campaign.Drops(r));
        yield return new Handler(112,BH3.Game.Sessions.SessionState.Authenticated,(s,p)=>[
            GamePacketCodec.Reply(p,3751,systems.Pass(Uid(s)).ToByteArray(),Uid(s)),
            GamePacketCodec.Reply(p,969,systems.Duty(Uid(s)).ToByteArray(),Uid(s)),
            GamePacketCodec.Reply(p,113,campaign.Missions(Uid(s)).ToByteArray(),Uid(s))]);
        yield return Change(114, 115, GetMissionRewardReq.Parser, campaign.Claim);
        yield return Respond(117, 118, UpdateMissionProgressReq.Parser, (s, r) => campaign.Update(Uid(s), r));
        yield return Respond(456, 457, GetStageActDifficultyReq.Parser, (s, _) => campaign.Acts(Uid(s)));
        yield return Change(458, 459, TakeStageActChallengeRewardReq.Parser, campaign.ClaimAct);
        yield return Respond(965, 966, GetStageChapterReq.Parser, (s, _) => campaign.Chapters(Uid(s)));
        yield return Respond(1378, 1379, FinishPlotReq.Parser, (s, r) => campaign.FinishPlot(Uid(s), r));
        yield return Respond(1541, 1542, GetStageRecommendAvatarReq.Parser, (_, r) => new GetStageRecommendAvatarRsp {
            Retcode = r.IdList.Count > 64 ? GetStageRecommendAvatarRsp.Types.Retcode.IdTooMuch : GetStageRecommendAvatarRsp.Types.Retcode.Succ,
            StageRecommendAvatarList = { r.IdList.Take(64).Select(id => new StageRecommendAvatar { Id = id, Type = r.Type }) } });
        yield return Respond(502, 503, GetExtraStoryChallengeModeDataReq.Parser, (_, r) => new GetExtraStoryChallengeModeDataRsp {
            Retcode = GetExtraStoryChallengeModeDataRsp.Types.Retcode.ExtraStoryNotOpen, ChapterId = r.ChapterId, IsCanReset = false });
        yield return Respond(813, 814, GetGalInteractTriggerEventReq.Parser, (s, r) => new GetGalInteractTriggerEventRsp {
            Retcode = campaign.OwnedAvatars(Uid(s)).Contains(r.AvatarId) ? GetGalInteractTriggerEventRsp.Types.Retcode.Succ : GetGalInteractTriggerEventRsp.Types.Retcode.NoSuchAvatar,
            AvatarId = r.AvatarId, EventId = 0 });
        yield return Respond(476, 477, GetBuffEffectReq.Parser, (_, _) => new GetBuffEffectRsp { Retcode = GetBuffEffectRsp.Types.Retcode.Succ });
        yield return Respond(121, 122, GetSignInRewardStatusReq.Parser, (_, _) => new GetSignInRewardStatusRsp {
            Retcode = GetSignInRewardStatusRsp.Types.Retcode.Succ, IsNeedSignIn = false, NextSignInDay = 0, NextSignInRewardId = 0 });
        yield return Respond(6706, 6707, GetProductListReq.Parser, (session, _) => operations.ProductList(Uid(session)));
        yield return Respond(4167, 4168, GetContinuousRechargeActivityReq.Parser, (_, _) => new GetContinuousRechargeActivityRsp {
            Retcode = GetContinuousRechargeActivityRsp.Types.Retcode.Succ, ActivityId = 0, Progress = 0, TodayVipPoint = 0 });
        yield return Respond(3460, 3461, GreedyEndlessTakeRankRewardReq.Parser, (_, _) => new GreedyEndlessTakeRankRewardRsp {
            Retcode = GreedyEndlessTakeRankRewardRsp.Types.Retcode.NoReward, RewardData = new RewardData() });
    }
    // Ordinary chapter-one accounts have no active PJMS world. The query itself succeeds;
    // 9.1's PrepareReEnterCurrentWorldTask treats FAIL as a fatal return-flow error.
    // Keep the object present and identical to the startup snapshot, with the inactive ID.
    private static PjmsWorld InactivePjmsWorld() => new() { WorldId = 0 };

    private GetAvatarDataRsp Avatars(GameSession session, GetAvatarDataReq request)
    {
        if (store.Inventory(Uid(session)) is { } inventory)
        {
            var imported = store.Campaign(Uid(session),tx=>GrantService.Inventory(tx).Avatars);
            bool all = RequestsAll(request.AvatarIdList);
            var selected = imported.AvatarList.Where(a => all || request.AvatarIdList.Contains(a.AvatarId)).ToArray();
            imported.AvatarList.Clear(); imported.AvatarList.Add(selected); imported.IsAll = all;
            return imported;
        }
        var state = store.Read(Uid(session)); var result = new GetAvatarDataRsp { Retcode = GetAvatarDataRsp.Types.Retcode.Succ, IsAll = RequestsAll(request.AvatarIdList) };
        if (result.IsAll || request.AvatarIdList.Contains(state.AvatarId)) result.AvatarList.Add(new Avatar {
            AvatarId = state.AvatarId, Star = 1, Level = Math.Max(1, state.AvatarLevel), Exp = state.AvatarExp,
            WeaponUniqueId = 1, DressId = state.DressId, DressList = { state.DressId } });
        return result;
    }
    private GetEquipmentDataRsp Equipment(GameSession session, GetEquipmentDataReq request)
    {
        if (store.Inventory(Uid(session)) is { } inventory)
        {
            var imported = GetEquipmentDataRsp.Parser.ParseFrom(inventory.Equipment);
            bool weaponsAll = RequestsAll(request.WeaponUniqueIdList), stigmataAll = RequestsAll(request.StigmataUniqueIdList);
            var weapons = imported.WeaponList.Where(w => weaponsAll || request.WeaponUniqueIdList.Contains(w.UniqueId)).ToArray();
            var stigmata = imported.StigmataList.Where(s => stigmataAll || request.StigmataUniqueIdList.Contains(s.UniqueId)).ToArray();
            imported.WeaponList.Clear(); imported.WeaponList.Add(weapons);
            imported.StigmataList.Clear(); imported.StigmataList.Add(stigmata);
            imported.MaterialList.Clear(); imported.MaterialList.Add(campaign.Materials(Uid(session))
                .Where(m => RequestsAll(request.MaterialIdList) || request.MaterialIdList.Contains(m.Id)));
            imported.IsAll = weaponsAll && stigmataAll && RequestsAll(request.MaterialIdList) && RequestsAll(request.MechaUniqueIdList);
            return imported;
        }
        var state = store.Read(Uid(session)); var result = new GetEquipmentDataRsp { Retcode = GetEquipmentDataRsp.Types.Retcode.Succ,
            IsAll = RequestsAll(request.WeaponUniqueIdList) && RequestsAll(request.StigmataUniqueIdList) && RequestsAll(request.MaterialIdList) && RequestsAll(request.MechaUniqueIdList) };
        if (result.IsAll || request.WeaponUniqueIdList.Contains(0) || request.WeaponUniqueIdList.Contains(1))
            result.WeaponList.Add(new Weapon { UniqueId = 1, Id = state.WeaponId, Level = 1, IsProtected = true });
        result.MaterialList.Add(campaign.Materials(Uid(session)).Where(m => RequestsAll(request.MaterialIdList) || request.MaterialIdList.Contains(m.Id)));
        return result;
    }
}
