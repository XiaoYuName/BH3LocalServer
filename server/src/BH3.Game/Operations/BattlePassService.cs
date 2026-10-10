using System.Text.Json;
using BH3.Persistence;
using BH3.Protocol.Messages;

namespace BH3.Game.Operations;

public sealed partial class SystemsService
{
    private static readonly uint[] DailyIds=[38169,38170,38171,38161,38241,38242,38243,38251,38252,38253,38278];
    public static JsonElement[] DailyMissions=>Rows("Missions").Where(x=>DailyIds.Contains(N(x,"id"))).ToArray();
    private static bool PassOpen(SystemsState s,uint now)=>s.Pass.Enabled&&s.Pass.BeginTime<=now&&now<s.Pass.EndTime;
    public static void Track(CampaignTransaction tx,uint now,string action,uint count=1)
    {
        var s=State(tx,now);
        foreach(var m in DailyMissions)
        {
            uint id=N(m,"id");bool match=action switch
            {"stamina"=>id==38161,"story"=>id is 38241 or 38242 or 38243,"stage"=>id is 38251 or 38252 or 38253,"abyss"=>id==38278,_=>false};
            if(match)s.DailyProgress[id]=Math.Min(N(m,"totalProgress"),checked(s.DailyProgress.GetValueOrDefault(id)+count));
        }
    }
    private static uint Progress(SystemsState s,JsonElement m,uint now)
    {
        uint id=N(m,"id"),hour=(now/3600+8)%24;
        return id switch{38169=>1,38170=>hour>=11?1u:0,38171=>hour>=18?1u:0,_=>s.DailyProgress.GetValueOrDefault(id)};
    }
    public static void AppendMissions(CampaignTransaction tx,GetMissionDataRsp result,uint now)
    {
        var s=State(tx,now);result.CloseMissionList.Add(s.DailyClaims);
        foreach(var m in DailyMissions.Where(x=>!s.DailyClaims.Contains(N(x,"id"))))
        {
            uint p=Progress(s,m,now),goal=Math.Max(1,N(m,"totalProgress"));
            result.MissionList.Add(new Mission{MissionId=N(m,"id"),Status=p>=goal?MissionStatus.Finish:MissionStatus.Doing,
                Progress=p,BeginTime=s.Day*86400-14400,EndTime=(s.Day+1)*86400-14400,FinishedTimesLimit=1});
        }
    }
    public static bool DailyClaimable(CampaignTransaction tx,uint id,uint now)
    {
        var s=State(tx,now);var m=DailyMissions.Single(x=>N(x,"id")==id);
        return !s.DailyClaims.Contains(id)&&Progress(s,m,now)>=Math.Max(1,N(m,"totalProgress"));
    }
    public static RewardData ClaimDaily(CampaignTransaction tx,uint id,uint now)
    {
        var s=State(tx,now);var m=DailyMissions.Single(x=>N(x,"id")==id);uint rewardId=N(m,"rewardId");
        var reward=Reward(rewardId);Apply(tx,reward);s.DailyClaims.Add(id);
        uint duty=(uint)reward.ItemList.Where(x=>x.Id==80016).Sum(x=>(long)x.Num);
        if(duty==0)duty=N(Rewards[rewardId],"RewardDutyPoint");
        s.DailyDuty=checked(s.DailyDuty+duty);s.WeeklyDuty=checked(s.WeeklyDuty+duty);
        AddPassExp(s,duty,now);return reward;
    }
    private static void AddPassExp(SystemsState s,uint value,uint now,bool weekly=true)
    {
        if(!PassOpen(s,now))return;
        uint amount=weekly?Math.Min(value,s.Pass.WeeklyLimit-Math.Min(s.WeekExp,s.Pass.WeeklyLimit)):value;
        if(weekly)s.WeekExp=checked(s.WeekExp+amount);
        s.PassExp=checked(s.PassExp+amount);
        while(s.PassLevel<100&&s.PassExp>=1000){s.PassLevel++;s.PassExp-=1000;}
        if(s.PassLevel==100)s.PassExp=0;
    }
    public GetBattlePassRsp Pass(uint uid)=>store.Campaign(uid,tx=>
    {
        var s=State(tx,Now);return new GetBattlePassRsp{Retcode=PassOpen(s,Now)?GetBattlePassRsp.Types.Retcode.Succ:GetBattlePassRsp.Types.Retcode.NotOpen,
            ScheduleId=s.Pass.Schedule,Level=s.PassLevel,Exp=s.PassExp,PhaseMaxExp=s.Pass.WeeklyLimit,PhaseExp=s.WeekExp,
            HasTakeRewardLevel=s.PassClaims.GetValueOrDefault(1u),HasGotTicketList={s.Tickets.Order()},IsTakePhaseFreeExp=s.PhaseExpTaken};
    });
    public SyncDutyNotify Duty(uint uid)=>store.Campaign(uid,tx=>
    {
        var s=State(tx,Now);return new SyncDutyNotify{DailyDutyPoint=s.DailyDuty,WeeklyDutyPoint=s.WeeklyDuty,
            HasTakeDailyDutyIdList={s.DailyDutyClaims.Order()},HasTakeWeeklyDutyIdList={s.WeeklyDutyClaims.Order()}};
    });
    public TakeDutyRewardRsp DutyClaim(uint uid,TakeDutyRewardReq r)=>store.Campaign(uid,tx=>
    {
        var result=new TakeDutyRewardRsp{Retcode=TakeDutyRewardRsp.Types.Retcode.Fail,DutyType=r.DutyType};
        bool weekly=(int)r.DutyType==2;var s=State(tx,Now);var claims=weekly?s.WeeklyDutyClaims:s.DailyDutyClaims;
        var rows=Rows(weekly?"DutyWeeklyData":"DutyDailyData").ToDictionary(x=>N(x,"dutyId"));
        var ids=r.DutyIdList.ToArray();if((int)r.DutyType is not (1 or 2)||ids.Length is 0 or >10||ids.Distinct().Count()!=ids.Length||ids.Any(x=>!rows.ContainsKey(x)))return result;
        if(ids.Any(claims.Contains)){result.Retcode=TakeDutyRewardRsp.Types.Retcode.HasTake;return result;}
        if(ids.Any(x=>(weekly?s.WeeklyDuty:s.DailyDuty)<N(rows[x],"needDuty"))){result.Retcode=TakeDutyRewardRsp.Types.Retcode.DutyPointLack;return result;}
        foreach(uint id in ids){var reward=Reward(N(rows[id],"rewardId"));Apply(tx,reward);claims.Add(id);result.RewardList.Add(reward);result.DutyIdList.Add(id);}
        result.Retcode=TakeDutyRewardRsp.Types.Retcode.Succ;return result;
    });
    public GetBattlePassMissionPanelRsp Panel(uint uid,GetBattlePassMissionPanelReq r)=>store.Campaign(uid,tx=>
    {
        var s=State(tx,Now);return new GetBattlePassMissionPanelRsp{Retcode=GetBattlePassMissionPanelRsp.Types.Retcode.Succ,
            MissionList={DailyMissions.Select(m=>new PanelMissionData{MissionId=N(m,"id"),CycleList={new PanelMissionData.Types.PanelMissionCycleData{CycleId=s.Day,BeginTime=s.Day*86400-14400,EndTime=(s.Day+1)*86400-14400}}})}};
    });
    public BuyBattlePassTicketRsp BuyTicket(uint uid,BuyBattlePassTicketReq r)=>store.Campaign(uid,tx=>
    {
        var result=new BuyBattlePassTicketRsp{Retcode=BuyBattlePassTicketRsp.Types.Retcode.Fail};var s=State(tx,Now);uint type=(uint)r.Type;
        if(!PassOpen(s,Now)){result.Retcode=BuyBattlePassTicketRsp.Types.Retcode.NotOpen;return result;}
        if(type is not (2 or 3)){result.Retcode=BuyBattlePassTicketRsp.Types.Retcode.TypeError;return result;}
        if(s.Tickets.Contains(type)){result.Retcode=BuyBattlePassTicketRsp.Types.Retcode.HasGot;return result;}
        uint cost=type==2?600u:s.Tickets.Contains(2)?680u:1280u;
        if(tx.Campaign.Operations.Mcoin<cost){result.Retcode=BuyBattlePassTicketRsp.Types.Retcode.LackMcoin;return result;}
        result.PrevHasGotTicketList.Add(s.Tickets);tx.Campaign.Operations.Mcoin-=cost;s.Tickets.Add(2);if(type==3)s.Tickets.Add(3);
        result.Retcode=BuyBattlePassTicketRsp.Types.Retcode.Succ;return result;
    });
    public TakeBattlePassLevelRewardRsp PassClaim(uint uid,TakeBattlePassLevelRewardReq r)=>store.Campaign(uid,tx=>
    {
        var s=State(tx,Now);var result=new TakeBattlePassLevelRewardRsp{Retcode=TakeBattlePassLevelRewardRsp.Types.Retcode.Fail};
        if(!PassOpen(s,Now))return result;
        foreach(uint tier in s.Tickets.Order())
        {
            var list=tier==1?result.BasicRewardList:tier==2?result.AdvancedRewardList:result.LuxuryRewardList;
            string field=tier==1?"BasicRewardID":tier==2?"AdvancedRewardID":"EliteRewardID";
            foreach(var row in Rows("BpLevels").Where(x=>N(x,"SeasonLevel")>s.PassClaims.GetValueOrDefault(tier)&&N(x,"SeasonLevel")<=s.PassLevel))
            {uint id=N(row,field);if(id==0)continue;var reward=Reward(id);Apply(tx,reward);list.Add(reward);}
            s.PassClaims[tier]=s.PassLevel;
        }
        result.Retcode=TakeBattlePassLevelRewardRsp.Types.Retcode.Succ;return result;
    });
    public BuyBattlePassLevelRsp BuyLevel(uint uid,BuyBattlePassLevelReq r)=>store.Campaign(uid,tx=>
    {
        var s=State(tx,Now);var result=new BuyBattlePassLevelRsp{Retcode=BuyBattlePassLevelRsp.Types.Retcode.Fail};
        if(!PassOpen(s,Now)||r.TargetLevel<=s.PassLevel||r.TargetLevel>100)return result;
        uint need=checked((r.TargetLevel-s.PassLevel)*1000-s.PassExp),cost=checked((need+4)/5);
        if(r.McoinCost!=0||r.HcoinCost!=cost||tx.Lobby.Hcoin<cost)return result;
        tx.Lobby=tx.Lobby with{Hcoin=tx.Lobby.Hcoin-cost};s.PassLevel=r.TargetLevel;s.PassExp=0;result.Retcode=BuyBattlePassLevelRsp.Types.Retcode.Succ;return result;
    });
    public TakeBattlePassPhaseExpRsp PhaseExp(uint uid,TakeBattlePassPhaseExpReq r)=>store.Campaign(uid,tx=>
    {
        var s=State(tx,Now);var result=new TakeBattlePassPhaseExpRsp{Retcode=TakeBattlePassPhaseExpRsp.Types.Retcode.Fail};
        if(!PassOpen(s,Now)||s.PhaseExpTaken)return result;
        s.PhaseExpTaken=true;AddPassExp(s,s.Pass.FreeExp,Now,false);result.AddExp=s.Pass.FreeExp;result.Retcode=TakeBattlePassPhaseExpRsp.Types.Retcode.Succ;return result;
    });
}
