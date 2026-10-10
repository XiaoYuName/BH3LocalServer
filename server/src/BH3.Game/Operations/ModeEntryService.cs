using System.Security.Cryptography;
using System.Text.Json;
using BH3.Game.Campaign;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Operations;

public sealed class ModeEntryService(LobbyStore store, TimeProvider clock)
{
    public const uint GodWarLobbyStage = 170199;
    private static readonly JsonElement Config = GrantService.Resource<JsonElement>("mode-entries.json");
    private uint Now => checked((uint)clock.GetUtcNow().ToUnixTimeSeconds());
    private static uint N(JsonElement row,string key)=>row.GetProperty(key).GetUInt32();
    private static IEnumerable<uint> Ids(JsonElement row,string key)=>row.GetProperty(key).EnumerateArray().Select(x=>x.GetUInt32());

    public GetGodWarRsp GodWar(uint uid, GetGodWarReq request) => store.Campaign(uid, tx =>
    {
        if (tx.Lobby.Level < 25 || request.GodWarId is not (0 or 1))
            return new GetGodWarRsp { Retcode = GetGodWarRsp.Types.Retcode.NotOpen };
        var owned=CampaignService.OwnedAvatars(tx);
        var god=new GodWar { GodWarId=1,BeginTime=1,EndTime=2145916800,LobbyId=2,CurChapterId=1,
            RoleInfo=new GodWarRoleInfo() };
        god.ChapterList.Add(Ids(Config,"chapterIds").Select(id=>new GodWarChapter{ChapterId=id}));
        foreach(var tale in Config.GetProperty("tales").EnumerateArray())
        {
            var data=new GodWarTale { TaleId=N(tale,"id"),ScheduleId=N(tale,"schedule"),
                AvatarScheduleId=N(tale,"avatarSchedule"),BeginTime=1,EndTime=2145916800,
                CurSiteId=0,IsLocked=false,Challenge=new GodWarChallenge(),CurAvatarScheduleInfo=new GodWarCurAvatarScheduleInfo() };
            // Identifiers are compatible with this client; account history/score is not a template.
            data.SiteList.Add(Ids(tale,"sites").Select(id=>new GodWarSite{SiteId=id,SiteStatus=GodWarSiteStatus.Unlocked}));
            data.OverallList.Add(Ids(tale,"overallIds").Select(id=>new GodWarOverall{OverallId=id,OverallVal=0}));
            god.TaleList.Add(data);
        }
        god.RoleInfo.MainAvatarIdList.Add(Ids(Config,"mainAvatars").Where(owned.Contains));
        god.RoleInfo.SupportAvatarIdList.Add(Ids(Config,"supportAvatars").Where(owned.Contains));
        god.RoleInfo.RoleRelationList.Add(Ids(Config,"roles").Select(id=>new GodWarRoleRelation{RoleId=id,Level=1,Exp=0,RewardHasTakeLevel=0}));
        return new GetGodWarRsp{Retcode=GetGodWarRsp.Types.Retcode.Succ,GodWarList={god}};
    });

    public GetGodWarLobbyRsp Lobby(uint uid,GetGodWarLobbyReq request) => new()
    {
        Retcode=store.Read(uid).Level>=25 && request.GodWarId==1 && request.LobbyId==2
            ? GetGodWarLobbyRsp.Types.Retcode.Succ : GetGodWarLobbyRsp.Types.Retcode.NotOpen,
        GodWarId=request.GodWarId,LobbyId=request.LobbyId
    };

    public GetThemeWantedRsp Mirage(uint uid) => store.Campaign(uid,tx=>
    {
        if(tx.Lobby.Level<28)return new GetThemeWantedRsp{Retcode=GetThemeWantedRsp.Types.Retcode.NotOpen};
        var activity=JsonParser.Default.Parse<ThemeWantedActivity>(Config.GetProperty("mirage").GetRawText());
        activity.EndTime=2145916800;
        activity.StageGroupInfoList.Add(activity.OpenStageGroupIdList.Select(id=>new ThemeWantedStageGroupInfo {
            StageGroupId=id,Progress=tx.Campaign.ModeEntries.MirageProgress.GetValueOrDefault(id) }));
        return new GetThemeWantedRsp{Retcode=GetThemeWantedRsp.Types.Retcode.Succ,ThemeWantedActivity=activity};
    });

    // The captured 9.1 refresh response is SUCC with no ticket fields. A refresh
    // acknowledges the inventory cache; it is not an unconditional ticket grant.
    public ThemeWantedRefreshTicketRsp RefreshMirage(uint uid)=>new(){Retcode=store.Read(uid).Level>=28
        ? ThemeWantedRefreshTicketRsp.Types.Retcode.Succ : ThemeWantedRefreshTicketRsp.Types.Retcode.Fail};

    public static StageBeginRsp BeginLobby(CampaignTransaction tx,StageBeginReq request,uint now)
    {
        StageBeginRsp Fail(StageBeginRsp.Types.Retcode code)=>new(){Retcode=code,StageId=request.StageId};
        if(tx.Lobby.Level<25)return Fail(StageBeginRsp.Types.Retcode.LevelLack);
        var avatars=request.AvatarIdList.Where(id=>id!=0).ToArray();
        var trials=request.AvatarTrialIdList.Where(id=>id!=0).ToArray();
        // 2214 is the automatic lobby sample in LevelTrialData[170199]. Unlike a
        // battle, a lobby can have no explicitly selected team.
        if(avatars.Length>3 || avatars.Distinct().Count()!=avatars.Length || trials.Length>1 || trials.Any(id=>id!=2214)
            || avatars.Any(id=>!CampaignService.OwnedAvatars(tx).Contains(id)) || !CompanionService.ValidTeam(tx,request.ElfIdList))
            return Fail(StageBeginRsp.Types.Retcode.AvatarError);
        if(request.AssistantUid!=0 || request.IsSpeedUpStage)return Fail(StageBeginRsp.Types.Retcode.NotMeetRestrict);
        var state=tx.Campaign.ModeEntries;
        if(state.GodWarLobbyBegin is {} previous && now>=state.GodWarLobbyEnteredAt && now-state.GodWarLobbyEnteredAt<86400)
            return StageBeginRsp.Parser.ParseFrom(previous);
        var result=new StageBeginRsp{Retcode=StageBeginRsp.Types.Retcode.Succ,StageId=GodWarLobbyStage,
            SignKey=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),StageTransactionStr=Guid.NewGuid().ToString("N"),IsCollectCheatData=false};
        state.GodWarLobbyBegin=result.ToByteArray();state.GodWarLobbyEnteredAt=now;
        return result;
    }

    public static StageEndRsp EndLobby(CampaignTransaction tx,StageEndReqBody body,string fingerprint,uint now)
    {
        var state=tx.Campaign.ModeEntries;
        if(state.GodWarLobbyBegin is null || now<state.GodWarLobbyEnteredAt || now-state.GodWarLobbyEnteredAt>=86400 || (int)body.EndStatus is <1 or >4)
            return new(){Retcode=StageEndRsp.Types.Retcode.StageError,StageId=body.StageId};
        var result=new StageEndRsp{Retcode=StageEndRsp.Types.Retcode.Succ,StageId=body.StageId,EndStatus=body.EndStatus};
        state.GodWarLobbyBegin=null;
        tx.SaveReceipt(fingerprint,result.ToByteArray());
        return result;
    }
}
