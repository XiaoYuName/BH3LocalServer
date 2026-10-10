using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;
using System.Text.Json;

namespace BH3.Game.Operations;

public sealed partial class SystemsService
{
    // 9.1 ActivityPage consumes GetConfigRsp.BulletinActivityList, not bulletin news.
    public static readonly Dictionary<string,JsonElement> CapturedLobby=GrantService.Resource<Dictionary<string,JsonElement>>("captured-lobby.json");
    public static readonly Dictionary<uint,BulletinActivityConfig> CapturedActivities=CapturedLobby["activities"].EnumerateArray()
        .Select(x=>JsonParser.Default.Parse<BulletinActivityConfig>(x.GetRawText())).ToDictionary(x=>x.ActivityId);
    // 9.1 AIINCJJCGPE.get_IsEventAvailable parses TypeParamStr as an integer.
    // Empty strings break both ActivityPage and the main-page mall red-dot refresh.
    public BulletinActivityConfig[] ActivityConfigs(uint uid)=>store.Campaign(uid,tx=>
        State(tx,Now).Activities!.Select(a=>
        {
            var result=CapturedActivities.TryGetValue(a.Id,out var template)?template.Clone():new BulletinActivityConfig
            {ActivityId=a.Id,ActivityType=4,TypeParamStr="0",MinPlayerLevel=1,MaxPlayerLevel=99};
            result.BeginTime=a.BeginTime;result.EndTime=a.Enabled?a.EndTime:1;
            result.TitleName=a.Title;result.Description=a.Content;result.Weight=a.Weight;
            if(result.ActivityType==4)
            {
                result.TypeParamList.Clear();result.TypeParamList.Add(a.Missions??[]);
                result.MissionIds.Clear();result.MissionIds.Add(a.Missions??[]);
            }
            return result;
        }).ToArray());

    public static void AppendActivityMissions(CampaignTransaction tx,GetMissionDataRsp result,uint now)
    {
        var known=result.MissionList.Select(m=>m.MissionId).Concat(result.CloseMissionList).ToHashSet();
        foreach(var id in State(tx,now).Activities!.Where(a=>a.Enabled&&a.BeginTime<=now&&now<a.EndTime)
            .SelectMany(a=>a.Missions??[]).Distinct())
            if(known.Add(id)) result.MissionList.Add(new Mission{MissionId=id,Status=MissionStatus.Doing,Progress=0,FinishedTimesLimit=1});
    }

    public GetBulletinActivityMissionRsp ActivityMissions(uint uid,GetBulletinActivityMissionReq request)=>store.Campaign(uid,tx=>
    {
        var s=State(tx,Now);var result=new GetBulletinActivityMissionRsp{Retcode=GetBulletinActivityMissionRsp.Types.Retcode.Succ};
        foreach(var a in s.Activities!.Where(a=>a.Enabled&&a.BeginTime<=Now&&Now<a.EndTime
            &&(request.ActivityIdList.Count==0||request.ActivityIdList.Contains(0)||request.ActivityIdList.Contains(a.Id))))
            result.MissionGroupList.Add(new BulletinMissionGroup{ActivityId=a.Id,MissionList={(a.Missions??[]).Select(id=>new PanelMissionData
            {MissionId=id,CycleList={new PanelMissionData.Types.PanelMissionCycleData{CycleId=s.Day,
                BeginTime=Math.Max(a.BeginTime,s.Day*86400-14400),EndTime=Math.Min(a.EndTime,(s.Day+1)*86400-14400)}}})}});
        return result;
    });
}
