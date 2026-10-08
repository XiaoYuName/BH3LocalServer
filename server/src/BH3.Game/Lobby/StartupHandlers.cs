using BH3.Game.Messaging;
using BH3.Game.Sessions;
using BH3.Protocol;
using BH3.Protocol.Messages;

namespace BH3.Game.Lobby;

public sealed partial class LobbyHandlers
{
    // Allowlist from the user's 9.1 startup log. Unknown commands remain Unsupported.
    // Empty collections describe local initial state; closed activities do not grant progress.
    private IEnumerable<IGameMessageHandler> CreateStartupHandlers()
    {
        // The 9.1 startup request is is_all=true (field 2); field 3 completes the full snapshot.
        // No local PJMS overall progress exists yet. Return the empty snapshot without granting progress.
        yield return Respond(CommandIds.PjmsGetOverallReq, CommandIds.PjmsGetOverallRsp, PjmsGetOverallReq.Parser, (_, request) => new PjmsGetOverallRsp { Retcode = PjmsGetOverallRsp.Types.Retcode.Succ, IsAll = request.IsAll });
        yield return Respond(CommandIds.GetFriendListReq, CommandIds.GetFriendListRsp, GetFriendListReq.Parser, (session, request) => new GetFriendListRsp { Retcode = GetFriendListRsp.Types.Retcode.Succ, IsWholeData = true });
        yield return Respond(CommandIds.GetAskAddFriendListReq, CommandIds.GetAskAddFriendListRsp, GetAskAddFriendListReq.Parser, (session, request) => new GetAskAddFriendListRsp { Retcode = GetAskAddFriendListRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetRecommendFriendListReq, CommandIds.GetRecommendFriendListRsp, GetRecommendFriendListReq.Parser, (session, request) => new GetRecommendFriendListRsp { Retcode = GetRecommendFriendListRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetAssistantFrozenListReq, CommandIds.GetAssistantFrozenListRsp, GetAssistantFrozenListReq.Parser, (session, request) => new GetAssistantFrozenListRsp { Retcode = GetAssistantFrozenListRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.UpdateMissionProgressReq, CommandIds.UpdateMissionProgressRsp, UpdateMissionProgressReq.Parser, (session, request) => new UpdateMissionProgressRsp { Retcode = UpdateMissionProgressRsp.Types.Retcode.Fail });
        yield return Respond(CommandIds.GetWeekDayActivityDataReq, CommandIds.GetWeekDayActivityDataRsp, GetWeekDayActivityDataReq.Parser, (session, request) => new GetWeekDayActivityDataRsp { Retcode = GetWeekDayActivityDataRsp.Types.Retcode.Succ, IsWholeData = true });
        yield return Respond(CommandIds.FinishGuideReportReq, CommandIds.FinishGuideReportRsp, FinishGuideReportReq.Parser, FinishGuides);
        yield return Respond(CommandIds.GetGobackReq, CommandIds.GetGobackRsp, GetGobackReq.Parser, (session, request) => new GetGobackRsp { Retcode = GetGobackRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.ReportClientDataVersionReq, CommandIds.ReportClientDataVersionRsp, ReportClientDataVersionReq.Parser, (session, request) => new ReportClientDataVersionRsp { ServerVersion = request.Version });
        yield return Respond(CommandIds.GetMedalDataReq, CommandIds.GetMedalDataRsp, GetMedalDataReq.Parser, (session, request) => new GetMedalDataRsp { Retcode = GetMedalDataRsp.Types.Retcode.Succ, IsAll = RequestsAll(request.MedalIdList) });
        yield return Respond(CommandIds.GetPediaReq, CommandIds.GetPediaRsp, GetPediaReq.Parser, (session, request) => new GetPediaRsp { Retcode = GetPediaRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetPlayerCardReq, CommandIds.GetPlayerCardRsp, GetPlayerCardReq.Parser, PlayerCard);
        yield return Respond(CommandIds.GetGrandKeyReq, CommandIds.GetGrandKeyRsp, GetGrandKeyReq.Parser, (session, request) => new GetGrandKeyRsp { Retcode = GetGrandKeyRsp.Types.Retcode.Succ, IsAll = RequestsAll(request.KeyIdList) });
        yield return Respond(CommandIds.GetExBossScheduleReq, CommandIds.GetExBossScheduleRsp, GetExBossScheduleReq.Parser, (session, request) => new GetExBossScheduleRsp { Retcode = GetExBossScheduleRsp.Types.Retcode.FeatureClosed });
        yield return Respond(CommandIds.GetExBossInfoReq, CommandIds.GetExBossInfoRsp, GetExBossInfoReq.Parser, (session, request) => new GetExBossInfoRsp { Retcode = GetExBossInfoRsp.Types.Retcode.NotOpen, BossInfo = new ExBossInfo() });
        yield return Respond(CommandIds.GetMasterPupilDataReq, CommandIds.GetMasterPupilDataRsp, GetMasterPupilDataReq.Parser, (session, request) => new GetMasterPupilDataRsp { Retcode = MasterPupilRetcode.Types.Retcode.Succ, Type = request.Type });
        yield return Respond(CommandIds.GetTrialAvatarReq, CommandIds.GetTrialAvatarRsp, GetTrialAvatarReq.Parser, (session, request) => new GetTrialAvatarRsp { Retcode = GetTrialAvatarRsp.Types.Retcode.Succ, IsAllUpdate = true });
        yield return Respond(CommandIds.GetMasterPupilCardReq, CommandIds.GetMasterPupilCardRsp, GetMasterPupilCardReq.Parser, (session, request) => new GetMasterPupilCardRsp { Retcode = MasterPupilRetcode.Types.Retcode.Succ, Card = new MasterPupilCard { Uid = Uid(session) } });
        yield return Respond(CommandIds.GetDormDataReq, CommandIds.GetDormDataRsp, GetDormDataReq.Parser, Dorm);
        yield return Respond(CommandIds.GetAvatarRollDataReq, CommandIds.GetAvatarRollDataRsp, GetAvatarRollDataReq.Parser, (session, request) => new GetAvatarRollDataRsp { Retcode = GetAvatarRollDataRsp.Types.Retcode.Succ, IsAll = RequestsAll(request.AvatarIdList) });
        yield return Respond(CommandIds.GetMasterPupilApplyReq, CommandIds.GetMasterPupilApplyRsp, GetMasterPupilApplyReq.Parser, (session, request) => new GetMasterPupilApplyRsp { Retcode = MasterPupilRetcode.Types.Retcode.Succ, Type = request.Type });
        yield return Respond(CommandIds.GetMasterPupilMainDataReq, CommandIds.GetMasterPupilMainDataRsp, GetMasterPupilMainDataReq.Parser, (session, request) => new GetMasterPupilMainDataRsp { Retcode = MasterPupilRetcode.Types.Retcode.Succ, Type = request.Type, Master = new MasterMainData(), Pupil = new PupilMainData() });
        yield return Respond(CommandIds.GetStageChapterReq, CommandIds.GetStageChapterRsp, GetStageChapterReq.Parser, (session, request) => new GetStageChapterRsp { Retcode = GetStageChapterRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetFriendRemarkListReq, CommandIds.GetFriendRemarkListRsp, GetFriendRemarkListReq.Parser, (session, request) => new GetFriendRemarkListRsp { Retcode = GetFriendRemarkListRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetWorldMapDataReq, CommandIds.GetWorldMapDataRsp, GetWorldMapDataReq.Parser, (session, request) => new GetWorldMapDataRsp { Retcode = GetWorldMapDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetPhotoDataReq, CommandIds.GetPhotoDataRsp, GetPhotoDataReq.Parser, (session, request) => new GetPhotoDataRsp { Retcode = GetPhotoDataRsp.Types.Retcode.Succ, Type = request.Type });
        yield return Respond(CommandIds.GetWikiDataReq, CommandIds.GetWikiDataRsp, GetWikiDataReq.Parser, (session, request) => new GetWikiDataRsp { Retcode = GetWikiDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetPhonePendantDataReq, CommandIds.GetPhonePendantDataRsp, GetPhonePendantDataReq.Parser, (session, request) => new GetPhonePendantDataRsp { Retcode = GetPhonePendantDataRsp.Types.Retcode.Succ, IsAll = RequestsAll(request.PhonePendantIdList) });
        yield return Respond(CommandIds.GetEmojiDataReq, CommandIds.GetEmojiDataRsp, GetEmojiDataReq.Parser, (session, request) => new GetEmojiDataRsp { Retcode = GetEmojiDataRsp.Types.Retcode.Succ, IsAll = true });
        yield return Respond(CommandIds.GetRegionUidRangeReq, CommandIds.GetRegionUidRangeRsp, GetRegionUidRangeReq.Parser, (session, request) => new GetRegionUidRangeRsp { Retcode = GetRegionUidRangeRsp.Types.Retcode.Succ, LocalRegionName = "pc01", RegionUidRangeList = { new RegionUidRange { StartUid = 1, EndUid = uint.MaxValue, RegionName = "pc01" } } });
        yield return Respond(CommandIds.GetPlotListReq, CommandIds.GetPlotListRsp, GetPlotListReq.Parser, (session, request) => new GetPlotListRsp { Retcode = GetPlotListRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetCurrencyExchangeInfoReq, CommandIds.GetCurrencyExchangeInfoRsp, GetCurrencyExchangeInfoReq.Parser, (session, request) => new GetCurrencyExchangeInfoRsp { Retcode = GetCurrencyExchangeInfoRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetRecommendMissionPanelListReq, CommandIds.GetRecommendMissionPanelListRsp, GetRecommendMissionPanelListReq.Parser, (session, request) => new GetRecommendMissionPanelListRsp { Retcode = GetRecommendMissionPanelListRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetBlackListReq, CommandIds.GetBlackListRsp, GetBlackListReq.Parser, (session, request) => new GetBlackListRsp { Retcode = GetBlackListRsp.Types.Retcode.Succ, IsOnlyUid = request.IsOnlyUid, IsWholeData = true });
        yield return Respond(CommandIds.GetWebActivityInfoReq, CommandIds.GetWebActivityInfoRsp, GetWebActivityInfoReq.Parser, (session, request) => new GetWebActivityInfoRsp { Retcode = GetWebActivityInfoRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.LoginWishGetMainDataReq, CommandIds.LoginWishGetMainDataRsp, LoginWishGetMainDataReq.Parser, (session, request) => new LoginWishGetMainDataRsp { Retcode = LoginWishGetMainDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetEliteChapterCompensationInfoReq, CommandIds.GetEliteChapterCompensationInfoRsp, GetEliteChapterCompensationInfoReq.Parser, (session, request) => new GetEliteChapterCompensationInfoRsp { Retcode = GetEliteChapterCompensationInfoRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetPrivilegeInfoReq, CommandIds.GetPrivilegeInfoRsp, GetPrivilegeInfoReq.Parser, (session, request) => new GetPrivilegeInfoRsp { Retcode = GetPrivilegeInfoRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.ChapterGroupGetDataReq, CommandIds.ChapterGroupGetDataRsp, ChapterGroupGetDataReq.Parser, (session, request) => new ChapterGroupGetDataRsp { Retcode = ChapterGroupGetDataRsp.Types.Retcode.Succ, ChapterGroupId = request.ChapterGroupId, IsAll = request.ChapterGroupId == 0 });
        yield return Respond(CommandIds.GetChapterCompensationInfoReq, CommandIds.GetChapterCompensationInfoRsp, GetChapterCompensationInfoReq.Parser, (session, request) => new GetChapterCompensationInfoRsp { Retcode = GetChapterCompensationInfoRsp.Types.Retcode.Succ, IsAll = request.ChapterId == 0 });
        yield return Respond(CommandIds.GetChallengeStepCompensationInfoReq, CommandIds.GetChallengeStepCompensationInfoRsp, GetChallengeStepCompensationInfoReq.Parser, (session, request) => new GetChallengeStepCompensationInfoRsp { Retcode = GetChallengeStepCompensationInfoRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetInviteActivityInviterDataReq, CommandIds.GetInviteActivityInviterDataRsp, GetInviteActivityInviterDataReq.Parser, (session, request) => new GetInviteActivityInviterDataRsp { Retcode = GetInviteActivityInviterDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetInviteActivityInviteeDataReq, CommandIds.GetInviteActivityInviteeDataRsp, GetInviteActivityInviteeDataReq.Parser, (session, request) => new GetInviteActivityInviteeDataRsp { Retcode = GetInviteActivityInviteeDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetCollectionListReq, CommandIds.GetCollectionListRsp, GetCollectionListReq.Parser, (session, request) => new GetCollectionListRsp { Retcode = GetCollectionListRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetChatgroupListReq, CommandIds.GetChatgroupListRsp, GetChatgroupListReq.Parser, (session, request) => new GetChatgroupListRsp { Retcode = GetChatgroupListRsp.Types.Retcode.Succ, IsAll = request.IsAll || request.ChatgroupIdList.Count == 0 });
        yield return Respond(CommandIds.EnterWorldChatroomReq, CommandIds.EnterWorldChatroomRsp, EnterWorldChatroomReq.Parser, (session, request) => new EnterWorldChatroomRsp { Retcode = EnterWorldChatroomRsp.Types.Retcode.FeatureClosed, ChatroomId = request.ChatroomId, ActivityType = request.ActivityType });
        yield return Respond(CommandIds.GetRpgTaleReq, CommandIds.GetRpgTaleRsp, GetRpgTaleReq.Parser, (session, request) => new GetRpgTaleRsp { Retcode = GetRpgTaleRsp.Types.Retcode.Succ, TaleId = request.TaleId, IsAll = request.IsAll });
        yield return Respond(CommandIds.ChatworldGetActivityScheduleReq, CommandIds.ChatworldGetActivityScheduleRsp, ChatworldGetActivityScheduleReq.Parser, (session, request) => new ChatworldGetActivityScheduleRsp { Retcode = ChatworldGetActivityScheduleRsp.Types.Retcode.Succ, SceneId = request.SceneId });
        yield return Respond(CommandIds.ChatworldGetPrayInfoReq, CommandIds.ChatworldGetPrayInfoRsp, ChatworldGetPrayInfoReq.Parser, (session, request) => new ChatworldGetPrayInfoRsp { Retcode = ChatworldGetPrayInfoRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.ChatworldBeastGetActivityReq, CommandIds.ChatworldBeastGetActivityRsp, ChatworldBeastGetActivityReq.Parser, (session, request) => new ChatworldBeastGetActivityRsp { Retcode = ChatworldBeastGetActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetArmadaDataReq, CommandIds.GetArmadaDataRsp, GetArmadaDataReq.Parser, (session, request) => new GetArmadaDataRsp { Retcode = GetArmadaDataRsp.Types.Retcode.Succ, Status = ArmadaPlayerStatus.ArmadaPlayerNotJoin, IsNeedRecommend = false });
        yield return Respond(CommandIds.GetConsignedOrderDataReq, CommandIds.GetConsignedOrderDataRsp, GetConsignedOrderDataReq.Parser, (session, request) => new GetConsignedOrderDataRsp { Retcode = GetConsignedOrderDataRsp.Types.Retcode.NotInArmada });
        yield return Respond(CommandIds.GetArmadaStageScoreActivityReq, CommandIds.GetArmadaStageScoreActivityRsp, GetArmadaStageScoreActivityReq.Parser, (session, request) => new GetArmadaStageScoreActivityRsp { Retcode = GetArmadaStageScoreActivityRsp.Types.Retcode.NotInArmada });
        yield return Respond(CommandIds.GetArmadaActivityListReq, CommandIds.GetArmadaActivityListRsp, GetArmadaActivityListReq.Parser, (session, request) => new GetArmadaActivityListRsp { Retcode = GetArmadaActivityListRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetAvatarMissionActivityReq, CommandIds.GetAvatarMissionActivityRsp, GetAvatarMissionActivityReq.Parser, (session, request) => new GetAvatarMissionActivityRsp { Retcode = GetAvatarMissionActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetRoomDataReq, CommandIds.GetRoomDataRsp, GetRoomDataReq.Parser, (session, request) => new GetRoomDataRsp { Retcode = GetRoomDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetEndlessStatusReq, CommandIds.GetEndlessStatusRsp, GetEndlessStatusReq.Parser, (session, request) => new GetEndlessStatusRsp { Retcode = GetEndlessStatusRsp.Types.Retcode.Succ, CurStatus = new EndlessStatus { CanJoinIn = false } });
        yield return Respond(CommandIds.GetDLCReq, CommandIds.GetDLCRsp, GetDLCReq.Parser, (session, request) => new GetDLCRsp { Retcode = GetDLCRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetDLCAvatarReq, CommandIds.GetDLCAvatarRsp, GetDLCAvatarReq.Parser, (session, request) => new GetDLCAvatarRsp { Retcode = GetDLCAvatarRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetDLCTowerReq, CommandIds.GetDLCTowerRsp, GetDLCTowerReq.Parser, (session, request) => new GetDLCTowerRsp { Retcode = GetDLCTowerRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetEquipmentForgeDataReq, CommandIds.GetEquipmentForgeDataRsp, GetEquipmentForgeDataReq.Parser, (session, request) => new GetEquipmentForgeDataRsp { Retcode = GetEquipmentForgeDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetExtractReforgeActivityReq, CommandIds.GetExtractReforgeActivityRsp, GetExtractReforgeActivityReq.Parser, (session, request) => new GetExtractReforgeActivityRsp { Retcode = GetExtractReforgeActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetBattlePassMissionPanelReq, CommandIds.GetBattlePassMissionPanelRsp, GetBattlePassMissionPanelReq.Parser, (session, request) => new GetBattlePassMissionPanelRsp { Retcode = GetBattlePassMissionPanelRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetClientMailDataReq, CommandIds.GetClientMailDataRsp, GetClientMailDataReq.Parser, (session, request) => new GetClientMailDataRsp { Retcode = GetClientMailDataRsp.Types.Retcode.Succ, Start = request.Start, FilterType = request.FilterType, IsEnd = true, ClientMailInfo = new ClientMailInfo { TotalNum = 0 } });
        yield return Respond(CommandIds.GetRaffleActivityReq, CommandIds.GetRaffleActivityRsp, GetRaffleActivityReq.Parser, (session, request) => new GetRaffleActivityRsp { Retcode = GetRaffleActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetAdventureGroupReq, CommandIds.GetAdventureGroupRsp, GetAdventureGroupReq.Parser, (session, request) => new GetAdventureGroupRsp { Retcode = GetAdventureGroupRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetAdventureStorySweepInfoReq, CommandIds.GetAdventureStorySweepInfoRsp, GetAdventureStorySweepInfoReq.Parser, (session, request) => new GetAdventureStorySweepInfoRsp { Retcode = GetAdventureStorySweepInfoRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GeneralActivityGetScheduleReq, CommandIds.GeneralActivityGetScheduleRsp, GeneralActivityGetScheduleReq.Parser, (session, request) => new GeneralActivityGetScheduleRsp { Retcode = GeneralActivityGetScheduleRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GeneralActivityGetMainInfoReq, CommandIds.GeneralActivityGetMainInfoRsp, GeneralActivityGetMainInfoReq.Parser, (session, request) => new GeneralActivityGetMainInfoRsp { Retcode = GeneralActivityGetMainInfoRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetTowerRaidActivityReq, CommandIds.GetTowerRaidActivityRsp, GetTowerRaidActivityReq.Parser, (session, request) => new GetTowerRaidActivityRsp { Retcode = GetTowerRaidActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetChapterActivityDataReq, CommandIds.GetChapterActivityDataRsp, GetChapterActivityDataReq.Parser, (session, request) => new GetChapterActivityDataRsp { Retcode = GetChapterActivityDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetMissionThemeDataReq, CommandIds.GetMissionThemeDataRsp, GetMissionThemeDataReq.Parser, (session, request) => new GetMissionThemeDataRsp { Retcode = GetMissionThemeDataRsp.Types.Retcode.Succ, ThemeId = request.ThemeId, IsGetAll = request.IsGetAll });
        yield return Respond(CommandIds.GetOfflineResourceDataReq, CommandIds.GetOfflineResourceDataRsp, GetOfflineResourceDataReq.Parser, (session, request) => new GetOfflineResourceDataRsp { Retcode = GetOfflineResourceDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetWeeklyRoutineActivityReq, CommandIds.GetWeeklyRoutineActivityRsp, GetWeeklyRoutineActivityReq.Parser, (session, request) => new GetWeeklyRoutineActivityRsp { Retcode = GetWeeklyRoutineActivityRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetRankScheduleDataReq, CommandIds.GetRankScheduleDataRsp, GetRankScheduleDataReq.Parser, (session, request) => new GetRankScheduleDataRsp { Retcode = GetRankScheduleDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetSurveyDataReq, CommandIds.GetSurveyDataRsp, GetSurveyDataReq.Parser, (session, request) => new GetSurveyDataRsp { Retcode = GetSurveyDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetNewbieActivityReq, CommandIds.GetNewbieActivityRsp, GetNewbieActivityReq.Parser, (session, request) => new GetNewbieActivityRsp { Retcode = GetNewbieActivityRsp.Types.Retcode.Succ, ScheduleId = 0, EndTime = 0 });
        yield return Respond(CommandIds.GetTradingCardActivityReq, CommandIds.GetTradingCardActivityRsp, GetTradingCardActivityReq.Parser, (session, request) => new GetTradingCardActivityRsp { Retcode = GetTradingCardActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetGardenScheduleReq, CommandIds.GetGardenScheduleRsp, GetGardenScheduleReq.Parser, (session, request) => new GetGardenScheduleRsp { Retcode = GetGardenScheduleRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetDropLimitActivityReq, CommandIds.GetDropLimitActivityRsp, GetDropLimitActivityReq.Parser, (session, request) => new GetDropLimitActivityRsp { Retcode = GetDropLimitActivityRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.SanctuaryGetMainInfoReq, CommandIds.SanctuaryGetMainInfoRsp, SanctuaryGetMainInfoReq.Parser, (session, request) => new SanctuaryGetMainInfoRsp { Retcode = SanctuaryGetMainInfoRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetActivityRewardStatisticDataReq, CommandIds.GetActivityRewardStatisticDataRsp, GetActivityRewardStatisticDataReq.Parser, (session, request) => new GetActivityRewardStatisticDataRsp { Retcode = GetActivityRewardStatisticDataRsp.Types.Retcode.NotOpen, Id = request.Id });
        yield return Respond(CommandIds.GetSupportActivityReq, CommandIds.GetSupportActivityRsp, GetSupportActivityReq.Parser, (session, request) => new GetSupportActivityRsp { Retcode = GetSupportActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetMosaicActivityReq, CommandIds.GetMosaicActivityRsp, GetMosaicActivityReq.Parser, (session, request) => new GetMosaicActivityRsp { Retcode = GetMosaicActivityRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetMissionGroupMainInfoReq, CommandIds.GetMissionGroupMainInfoRsp, GetMissionGroupMainInfoReq.Parser, (session, request) => new GetMissionGroupMainInfoRsp { Retcode = GetMissionGroupMainInfoRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.ReunionCookGetActivityReq, CommandIds.ReunionCookGetActivityRsp, ReunionCookGetActivityReq.Parser, (session, request) => new ReunionCookGetActivityRsp { Retcode = ReunionCookGetActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetNinjaActivityReq, CommandIds.GetNinjaActivityRsp, GetNinjaActivityReq.Parser, (session, request) => new GetNinjaActivityRsp { Retcode = GetNinjaActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetNewOpenworldReq, CommandIds.GetNewOpenworldRsp, GetNewOpenworldReq.Parser, (session, request) => new GetNewOpenworldRsp { Retcode = GetNewOpenworldRsp.Types.Retcode.Succ, DataType = request.DataType });
        yield return Respond(CommandIds.OpenworldGetMechaTeamReq, CommandIds.OpenworldGetMechaTeamRsp, OpenworldGetMechaTeamReq.Parser, (session, request) => new OpenworldGetMechaTeamRsp { Retcode = OpenworldGetMechaTeamRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetOpenworldMechaDefenseReq, CommandIds.GetOpenworldMechaDefenseRsp, GetOpenworldMechaDefenseReq.Parser, (session, request) => new GetOpenworldMechaDefenseRsp { Retcode = GetOpenworldMechaDefenseRsp.Types.Retcode.Succ, MechaDefense = new OpenworldMechaDefense { LeftEnterTimes = 0 } });
        yield return Respond(CommandIds.GetOpenworldQuestActivityReq, CommandIds.GetOpenworldQuestActivityRsp, GetOpenworldQuestActivityReq.Parser, (session, request) => new GetOpenworldQuestActivityRsp { Retcode = GetOpenworldQuestActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.OpenworldHuntActivityGetDataReq, CommandIds.OpenworldHuntActivityGetDataRsp, OpenworldHuntActivityGetDataReq.Parser, (session, request) => new OpenworldHuntActivityGetDataRsp { Retcode = OpenworldHuntActivityGetDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetGachaDisplayReq, CommandIds.GetGachaDisplayRsp, GetGachaDisplayReq.Parser, (_, request) => GachaDisplay(request));
        yield return Respond(CommandIds.ClientReportReq, CommandIds.ClientReportRsp, ClientReportReq.Parser, (session, request) => new ClientReportRsp { Retcode = ClientReportRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetAuthkeyReq, CommandIds.GetAuthkeyRsp, GetAuthkeyReq.Parser, (session, request) => new GetAuthkeyRsp { Retcode = GetAuthkeyRsp.Types.Retcode.Fail, AuthAppid = request.AuthAppid, SignType = request.SignType, AuthkeyVer = request.AuthkeyVer });
        yield return Respond(CommandIds.GetSecurityPasswordReq, CommandIds.GetSecurityPasswordRsp, GetSecurityPasswordReq.Parser, (session, request) => new GetSecurityPasswordRsp { Retcode = GetSecurityPasswordRsp.Types.Retcode.Succ, Status = SecurityPasswordStatus.SecurityPasswordNotSet, DeviceStatus = SecurityPasswordDeviceStatus.SecurityPasswordDeviceUnlocked });
        yield return Respond(CommandIds.UltraEndlessGetMainDataReq, CommandIds.UltraEndlessGetMainDataRsp, UltraEndlessGetMainDataReq.Parser, (session, request) => new UltraEndlessGetMainDataRsp { Retcode = UltraEndlessGetMainDataRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetWarshipTrialDataReq, CommandIds.GetWarshipTrialDataRsp, GetWarshipTrialDataReq.Parser, (session, request) => new GetWarshipTrialDataRsp { Retcode = GetWarshipTrialDataRsp.Types.Retcode.Succ, IsAll = request.IsAll || request.SampleIdList.Count == 0 });
        yield return Respond(CommandIds.GetThemeWantedReq, CommandIds.GetThemeWantedRsp, GetThemeWantedReq.Parser, (session, request) => new GetThemeWantedRsp { Retcode = GetThemeWantedRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.ThemeWantedRefreshTicketReq, CommandIds.ThemeWantedRefreshTicketRsp, ThemeWantedRefreshTicketReq.Parser, (session, request) => new ThemeWantedRefreshTicketRsp { Retcode = ThemeWantedRefreshTicketRsp.Types.Retcode.Fail });
        yield return Respond(CommandIds.GetRewardLineActivityReq, CommandIds.GetRewardLineActivityRsp, GetRewardLineActivityReq.Parser, (session, request) => new GetRewardLineActivityRsp { Retcode = GetRewardLineActivityRsp.Types.Retcode.Succ, IsGetClosedActivity = request.IsGetClosedActivity });
        yield return Respond(CommandIds.BuffAssistGetActivityReq, CommandIds.BuffAssistGetActivityRsp, BuffAssistGetActivityReq.Parser, (session, request) => new BuffAssistGetActivityRsp { Retcode = BuffAssistGetActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetGodWarReq, CommandIds.GetGodWarRsp, GetGodWarReq.Parser, (session, request) => new GetGodWarRsp { Retcode = GetGodWarRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetShopListReq, CommandIds.GetShopListRsp, GetShopListReq.Parser, (session, request) => new GetShopListRsp { Retcode = GetShopListRsp.Types.Retcode.Succ, IsAll = true });
        yield return Respond(CommandIds.GetShoppingMallListReq, CommandIds.GetShoppingMallListRsp, GetShoppingMallListReq.Parser, (session, request) => new GetShoppingMallListRsp { Retcode = GetShoppingMallListRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetRecommendGoodsReq, CommandIds.GetRecommendGoodsRsp, GetRecommendGoodsReq.Parser, (session, request) => new GetRecommendGoodsRsp { Retcode = GetRecommendGoodsRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetVipRewardDataReq, CommandIds.GetVipRewardDataRsp, GetVipRewardDataReq.Parser, (session, request) => new GetVipRewardDataRsp { Retcode = GetVipRewardDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetCardProductInfoReq, CommandIds.GetCardProductInfoRsp, GetCardProductInfoReq.Parser, (session, request) => new GetCardProductInfoRsp { Retcode = GetCardProductInfoRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.ChapterBwWorldGetDataReq, CommandIds.ChapterBwWorldGetDataRsp, ChapterBwWorldGetDataReq.Parser, (session, request) => new ChapterBwWorldGetDataRsp { Retcode = ChapterBwWorldGetDataRsp.Types.Retcode.FeatureClosed });
        yield return Respond(CommandIds.PjmsGetMainDataReq, CommandIds.PjmsGetMainDataRsp, PjmsGetMainDataReq.Parser, (session, request) => new PjmsGetMainDataRsp { Retcode = PjmsGetMainDataRsp.Types.Retcode.Succ, World = new PjmsWorld(), Map = new PjmsMap(), WorldTime = 43200, WorldTransactionStr = "0" });
        yield return Respond(CommandIds.PjmsGetStoryDataReq, CommandIds.PjmsGetStoryDataRsp, PjmsGetStoryDataReq.Parser, (session, request) => new PjmsGetStoryDataRsp { Retcode = PjmsGetStoryDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.PjmsGetResidentStageDataReq, CommandIds.PjmsGetResidentStageDataRsp, PjmsGetResidentStageDataReq.Parser, (session, request) => new PjmsGetResidentStageDataRsp { Retcode = PjmsGetResidentStageDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.PjmsGetAchievementDataReq, CommandIds.PjmsGetAchievementDataRsp, PjmsGetAchievementDataReq.Parser, (session, request) => new PjmsGetAchievementDataRsp { Retcode = PjmsGetAchievementDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.PjmsGetChapterDataReq, CommandIds.PjmsGetChapterDataRsp, PjmsGetChapterDataReq.Parser, (session, request) => new PjmsGetChapterDataRsp { Retcode = PjmsGetChapterDataRsp.Types.Retcode.Succ, IsAll = request.IsAll });
        yield return Respond(CommandIds.PjmsGetActivityPanelReq, CommandIds.PjmsGetActivityPanelRsp, PjmsGetActivityPanelReq.Parser, (session, request) => new PjmsGetActivityPanelRsp { Retcode = PjmsGetActivityPanelRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.PjmsGetConditionDataReq, CommandIds.PjmsGetConditionDataRsp, PjmsGetConditionDataReq.Parser, (session, request) => new PjmsGetConditionDataRsp { Retcode = PjmsGetConditionDataRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.ChapterArkGetDataReq, CommandIds.ChapterArkGetDataRsp, ChapterArkGetDataReq.Parser, (session, request) => new ChapterArkGetDataRsp { Retcode = ChapterArkGetDataRsp.Types.Retcode.FeatureClosed });
        yield return Respond(CommandIds.ArkPlusActivityGetDataReq, CommandIds.ArkPlusActivityGetDataRsp, ArkPlusActivityGetDataReq.Parser, (session, request) => new ArkPlusActivityGetDataRsp { Retcode = ArkPlusActivityGetDataRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.RaidReplaceGetDataReq, CommandIds.RaidReplaceGetDataRsp, RaidReplaceGetDataReq.Parser, (session, request) => new RaidReplaceGetDataRsp { Retcode = RaidReplaceGetDataRsp.Types.Retcode.FeatureClosed });
        yield return Respond(CommandIds.ChapterKnightRichManGetDataReq, CommandIds.ChapterKnightRichManGetDataRsp, ChapterKnightRichManGetDataReq.Parser, (session, request) => new ChapterKnightRichManGetDataRsp { Retcode = ChapterKnightRichManGetDataRsp.Types.Retcode.NotOpen, RichManId = request.RichManId });
        yield return Respond(CommandIds.PjmsChapterKnightDurandalCubeGetDataReq, CommandIds.PjmsChapterKnightDurandalCubeGetDataRsp, PjmsChapterKnightDurandalCubeGetDataReq.Parser, (session, request) => new PjmsChapterKnightDurandalCubeGetDataRsp { Retcode = PjmsChapterKnightDurandalCubeGetDataRsp.Types.Retcode.NotOpen, ActivityId = request.ActivityId });
        yield return Respond(CommandIds.PjmsChapterKnightSpaceAdventureGetMainDataReq, CommandIds.PjmsChapterKnightSpaceAdventureGetMainDataRsp, PjmsChapterKnightSpaceAdventureGetMainDataReq.Parser, (session, request) => new PjmsChapterKnightSpaceAdventureGetMainDataRsp { Retcode = PjmsChapterKnightSpaceAdventureGetMainDataRsp.Types.Retcode.NotOpen, ActivityId = request.ActivityId });
        yield return Respond(CommandIds.SimplifiedGodWarGetActivityReq, CommandIds.SimplifiedGodWarGetActivityRsp, SimplifiedGodWarGetActivityReq.Parser, (_, _) => new SimplifiedGodWarGetActivityRsp { Retcode = SimplifiedGodWarGetActivityRsp.Types.Retcode.NotOpen });
        yield return Respond(CommandIds.GetWorldMapRecommendReq, CommandIds.GetWorldMapRecommendRsp, GetWorldMapRecommendReq.Parser, (_, _) => new GetWorldMapRecommendRsp { Retcode = GetWorldMapRecommendRsp.Types.Retcode.Succ });
        yield return Respond(CommandIds.GetOpenworldEndlessDataReq, CommandIds.GetOpenworldEndlessDataRsp, GetOpenworldEndlessDataReq.Parser, (_, request) => new GetOpenworldEndlessDataRsp { Retcode = GetOpenworldEndlessDataRsp.Types.Retcode.NoEndless, Type = request.Type });
        yield return Respond(CommandIds.GetOpenworldStoryReq, CommandIds.GetOpenworldStoryRsp, GetOpenworldStoryReq.Parser, (_, _) => new GetOpenworldStoryRsp { Retcode = GetOpenworldStoryRsp.Types.Retcode.Succ, IsAll = true });
        yield return Respond(CommandIds.GetWareHouseDataReq, CommandIds.GetWareHouseDataRsp, GetWareHouseDataReq.Parser, (_, _) => new GetWareHouseDataRsp { Retcode = GetWareHouseDataRsp.Types.Retcode.NotInArmada });
        yield return Respond(CommandIds.ChatworldGetDishInfoReq, CommandIds.ChatworldGetDishInfoRsp, ChatworldGetDishInfoReq.Parser, (_, _) => new ChatworldGetDishInfoRsp { Retcode = ChatworldGetDishInfoRsp.Types.Retcode.NotOpen });
    }

    private GetPlayerCardRsp PlayerCard(GameSession session, GetPlayerCardReq request) => new()
    {
        // 9.1 OnGetPlayerCardRsp indexes three avatars, two medals and one ELF even for an empty profile.
        // Zero denotes an empty display slot; it grants no ownership. MsgData is also dereferenced by the client.
        Retcode = GetPlayerCardRsp.Types.Retcode.Succ, Type = request.Type,
        AvatarIdList = { store.Read(Uid(session)).AvatarId, 0, 0 },
        MedalIdList = { 0, 0 }, MedalList = { new Medal { Id = 0 }, new Medal { Id = 0 } },
        ElfIdList = { 0 }, MsgData = new PlayerCardMsgData()
    };

    private static GetGachaDisplayRsp GachaDisplay(GetGachaDisplayReq request)
    {
        // The reference adds a synthetic NONE=0; the 9.1 protobuf-net decoder rejects it on the wire.
        // Preserve proto2 presence: an all-pools query has no type selector in its reply.
        bool hasType = request.HasType && (int)request.Type != 0;
        var response = new GetGachaDisplayRsp { Retcode = GetGachaDisplayRsp.Types.Retcode.Succ, IsAll = request.IsAll || !hasType };
        if (hasType)
        {
            if (!Enum.IsDefined(request.Type) || request.Type == GachaType.Error)
                response.Retcode = GetGachaDisplayRsp.Types.Retcode.Fail;
            else response.Type = request.Type;
        }
        return response;
    }

    private FinishGuideReportRsp FinishGuides(GameSession session, FinishGuideReportReq request)
    {
        try { store.CompleteGuides(Uid(session), request.GuideIdList); }
        catch (ArgumentException) { return new FinishGuideReportRsp { Retcode = FinishGuideReportRsp.Types.Retcode.Fail, IsFinish = false }; }
        return new FinishGuideReportRsp { Retcode = FinishGuideReportRsp.Types.Retcode.Succ,
            GuideIdList = { request.GuideIdList.Distinct() }, IsFinish = true };
    }

    private GetDormDataRsp Dorm(GameSession session, GetDormDataReq request)
    {
        uint avatar = store.Read(Uid(session)).AvatarId;
        return new GetDormDataRsp { Retcode = GetDormDataRsp.Types.Retcode.Succ, DataType = request.DataType,
            ShowHouse = 101, ShowRoom = 1012, VisitAvatar = avatar, IsAllowVisit = false,
            UnlockAvatarList = { avatar }, FacilityData = new DormFacilityData(),
            HouseList = { new DormHouse { Id = 101, Level = 1, Name = "休伯利安宿舍",
                RoomList = { new DormRoom { Id = 1011 }, new DormRoom { Id = 1012, AvatarList = { avatar } }, new DormRoom { Id = 1013 } } } } };
    }
}
