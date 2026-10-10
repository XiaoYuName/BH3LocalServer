using System.Security.Cryptography;
using System.Text.Json;
using BH3.Persistence;
using BH3.Protocol.Messages;

namespace BH3.Game.Operations;

// The source tables are retained as data. No client process or real payment service is involved.
public sealed partial class SystemsService(LobbyStore store, TimeProvider clock)
{
    public static readonly Dictionary<string,JsonElement> Data = GrantService.Resource<Dictionary<string,JsonElement>>("systems.json");
    public static IEnumerable<JsonElement> Rows(string name) => Data[name].EnumerateArray();
    public static uint N(JsonElement row,string field) => row.TryGetProperty(field,out var v) && v.TryGetUInt32(out var n) ? n : 0;
    private static readonly Dictionary<uint,JsonElement> Rewards = Rows("Rewards").ToDictionary(r=>N(r,"RewardID"));
    private uint Now => checked((uint)clock.GetUtcNow().ToUnixTimeSeconds());
    public static SystemsState State(CampaignTransaction tx,uint now)
    {
        var s=tx.Campaign.Systems; uint day=(now+14400)/86400, week=(day+3)/7;
        if(s.Day!=day){s.Day=day;s.DailyProgress.Clear();s.DailyClaims.Clear();s.DailyDuty=0;s.DailyDutyClaims.Clear();}
        if(s.Week!=week){s.Week=week;s.WeekExp=0;s.WeeklyDuty=0;s.WeeklyDutyClaims.Clear();}
        if(s.PassSchedule!=s.Pass.Schedule)
        {s.PassSchedule=s.Pass.Schedule;s.PassLevel=1;s.PassExp=0;s.PhaseExpTaken=false;s.Tickets=[1];s.PassClaims.Clear();}
        s.Activities ??= [new(16001,"本地作战活动","完成作战任务，领取每日奖励。活动标题、内容、时间和关联任务可在 GM 中配置。",true,1,2145916800,Missions:DailyIds)];
        if(s.ActivityCatalogVersion<1)
        {
            foreach(var a in CapturedActivities.Values)
                if(s.Activities.All(x=>x.Id!=a.ActivityId))
                    s.Activities.Add(new(a.ActivityId,a.TitleName,a.Description,true,1,2145916800,a.Weight,
                        Missions:a.ActivityType==4?a.TypeParamList.ToArray():[]));
            s.ActivityCatalogVersion=1;
        }
        return s;
    }
    public static RewardData Reward(uint id)
    {
        if(!Rewards.TryGetValue(id,out var r)) throw new ArgumentException($"奖励定义 {id} 不存在。");
        var result=new RewardData {Hcoin=N(r,"RewardHCoin"),Exp=N(r,"RewardExp"),Stamina=N(r,"RewardStamina")};
        for(int i=1;i<=6;i++)
        {
            uint item=N(r,$"RewardItem{i}ID"),num=N(r,$"RewardItem{i}Num");if(item==0||num==0)continue;
            if(item==100)result.Scoin=checked(result.Scoin+num);
            else result.ItemList.Add(new RewardItemData{Id=item,Num=num,Level=Math.Max(1,N(r,$"RewardItem{i}Level"))});
        }
        return result;
    }
    public static void Apply(CampaignTransaction tx,RewardData reward)
    {
        if(reward.Exp>0)
        {
            var levels=BH3.Game.Campaign.CampaignCatalog.Default;uint level=tx.Lobby.Level,exp=checked(tx.Lobby.Exp+reward.Exp),stamina=tx.Lobby.Stamina;
            while(level<levels.Levels.Length&&exp>=levels.Level(level).Exp){exp-=levels.Level(level).Exp;level++;stamina=checked(stamina+levels.Level(level).Bonus);}
            tx.Lobby=tx.Lobby with{Level=level,Exp=exp,Stamina=stamina};
        }
        if(reward.Hcoin>0)GrantService.Apply(tx,tx.Uid,new("hcoin",0,reward.Hcoin));
        if(reward.Scoin>0)GrantService.Apply(tx,tx.Uid,new("scoin",0,reward.Scoin));
        if(reward.Stamina>0)GrantService.Apply(tx,tx.Uid,new("stamina",0,reward.Stamina));
        foreach(var item in reward.ItemList)
        {
            if(item.Id==80016)continue; // Battle-pass EXP is settled with the mission, not bag inventory.
            if(GrantService.Dresses.ContainsKey(item.Id)){GrantService.Apply(tx,tx.Uid,new("dress",item.Id));continue;}
            if(Rows("PhonePendants").Any(x=>N(x,"PendantId")==item.Id))
            {tx.Campaign.Systems.PhonePendants.Add(item.Id);continue;}
            var spec=GrantService.Catalog.Items.FirstOrDefault(x=>x.Id==item.Id&&x.Kind is "material" or "weapon" or "stigmata");
            if(spec is null)throw new ArgumentException($"奖励物品 {item.Id} 不在目录。");
            GrantService.Apply(tx,tx.Uid,new(spec.Kind,item.Id,item.Num,Math.Max(1,item.Level)));
        }
    }
    public UseMaterialRsp Use(uint uid,UseMaterialReq r)
    {
        try{return store.Campaign(uid,tx=>
        {
            var fail=new UseMaterialRsp{Retcode=UseMaterialRsp.Types.Retcode.Fail,MaterialId=r.MaterialId,Num=r.Num};
            if(r.Num is 0 or >99){fail.Retcode=UseMaterialRsp.Types.Retcode.InvalidNum;return fail;}
            if(r.MaterialId!=3509){fail.Retcode=UseMaterialRsp.Types.Retcode.FeatureClosed;return fail;}
            if(r.Parameter!=0||r.ConsumeItemList is not null)return fail;
            uint count=tx.Campaign.Materials.GetValueOrDefault(r.MaterialId);
            if(count<r.Num){fail.Retcode=UseMaterialRsp.Types.Retcode.MaterialLack;return fail;}
            var state=State(tx,Now);var pool=Rows("Treasure").ToArray();int total=pool.Sum(x=>(int)N(x,"weight"));
            for(int i=0;i<r.Num;i++)
            {
                var chosen=pool[0];int roll=RandomNumberGenerator.GetInt32(total);
                if(state.TreasureMisses<3)foreach(var entry in pool){roll-=(int)N(entry,"weight");if(roll<0){chosen=entry;break;}}
                uint id=N(chosen,"id"),num=N(chosen,"num");state.TreasureMisses=id==1110?0:state.TreasureMisses+1;
                var reward=new RewardData();if(id==100)reward.Scoin=num;else reward.ItemList.Add(new RewardItemData{Id=id,Num=num,Level=1});
                Apply(tx,reward);fail.GiftRewardList.Add(reward);
            }
            tx.Campaign.Materials[r.MaterialId]=count-r.Num;fail.Retcode=UseMaterialRsp.Types.Retcode.Succ;return fail;
        });}catch(ArgumentException){return new(){Retcode=UseMaterialRsp.Types.Retcode.EquipmentFull,MaterialId=r.MaterialId,Num=r.Num};}
    }
    public GetWikiDataRsp Wiki(uint uid)=>store.Campaign(uid,tx=>new GetWikiDataRsp{Retcode=GetWikiDataRsp.Types.Retcode.Succ,HasTakeRatingRewardList={tx.Campaign.Systems.WikiClaims.Order()}});
    public TakeWikiRatingRewardRsp WikiClaim(uint uid,TakeWikiRatingRewardReq r)
    {
        try{return store.Campaign(uid,tx=>
        {
            var result=new TakeWikiRatingRewardRsp{Retcode=TakeWikiRatingRewardRsp.Types.Retcode.Fail,RatingId=r.RatingId};
            var rank=Rows("WikiCollectionRank").FirstOrDefault(x=>N(x,"RankID")==r.RatingId);
            if(rank.ValueKind==JsonValueKind.Undefined)return result;
            if(tx.Campaign.Systems.WikiClaims.Contains(r.RatingId)){result.Retcode=TakeWikiRatingRewardRsp.Types.Retcode.HasTkae;return result;}
            // Collection score is computed by the client from its CGs and item history.
            // Bound that report by the inventory-derived score, never accept arbitrary points.
            if(Math.Min(r.RatingScore,CollectionScore(tx))<N(rank,"RankScore")){result.Retcode=TakeWikiRatingRewardRsp.Types.Retcode.ScoreLack;return result;}
            var reward=Reward(N(rank,"Reward"));Apply(tx,reward);tx.Campaign.Systems.WikiClaims.Add(r.RatingId);
            result.RewardList.Add(reward);result.Retcode=TakeWikiRatingRewardRsp.Types.Retcode.Succ;return result;
        });}catch(ArgumentException){return new(){Retcode=TakeWikiRatingRewardRsp.Types.Retcode.Fail,RatingId=r.RatingId};}
    }
    public static uint CollectionScore(CampaignTransaction tx)
    {
        var inv=GrantService.Inventory(tx);
        var weapons=inv.Equipment.WeaponList.Select(x=>x.Id).ToHashSet();
        var stigmas=inv.Equipment.StigmataList.Select(x=>x.Id).ToHashSet();
        var dresses=tx.Campaign.Operations.OwnedDresses.Concat(inv.Avatars.AvatarList.Select(x=>x.DressId)).ToHashSet();
        // 9.1 Wiki constants: weapon 4 + rarity*4 + fully evolved bonus 4;
        // stigmata 4 + rarity*3, complete set bonus 10; dress 5 + rarity*6.
        // Deduplicate evolution variants by main ID. Avatars have coefficient zero.
        long score=Rows("Weapons").Where(x=>weapons.Contains(N(x,"id"))).GroupBy(x=>N(x,"main"))
            .Sum(g=>(long)g.Max(x=>4+N(x,"rarity")*4+(N(x,"rarity")==N(x,"maxRarity")?4u:0u)));
        var ownedStigmas=Rows("Stigmata").Where(x=>stigmas.Contains(N(x,"id"))).ToArray();
        score+=ownedStigmas.GroupBy(x=>N(x,"main")).Sum(g=>(long)g.Max(x=>4+N(x,"rarity")*3));
        score+=ownedStigmas.Where(x=>N(x,"set")!=0).GroupBy(x=>N(x,"set"))
            .Count(g=>g.Select(x=>N(x,"part")).Distinct().Count()==3)*10L;
        score+=Rows("WikiDresses").Where(x=>N(x,"show")!=0&&dresses.Contains(N(x,"id"))).Sum(x=>5L+N(x,"rarity")*6);
        return checked((uint)score);
    }
    private static readonly GetBulletinRsp BulletinTemplate=Google.Protobuf.JsonParser.Default.Parse<GetBulletinRsp>(
        GrantService.Resource<JsonElement>("bulletins.json").GetRawText());
    public GetBulletinRsp Bulletins(uint uid,GetBulletinReq r)=>store.Campaign(uid,tx=>
    {
        var result=new GetBulletinRsp{Retcode=GetBulletinRsp.Types.Retcode.Succ,IsAll=r.BulletinIdList.Count==0||r.BulletinIdList.Contains(0)};
        // Type 2 is the hall carousel, not the activities page. Preserve native
        // artwork and jump targets; GM activities remain in GetConfigRsp field 50.
        var news=BulletinTemplate.BulletinList.Select(x=>x.Clone()).ToList();
        news.AddRange(State(tx,Now).Activities!.Where(a=>!CapturedActivities.ContainsKey(a.Id)&&a.Enabled)
            .Select(a=>new Bulletin{Id=a.Id,Type=1,Weight=a.Weight,TitleButton=a.Title,Title=a.Title,Content=a.Content,
                BannerPath="event/BulletinBoard/Banner/fair",BeginTime=a.BeginTime,EndTime=a.EndTime,UpdateTime=a.BeginTime,ShowConfigId=a.Panel}));
        foreach(var b in news.Where(x=>x.BeginTime<=Now&&Now<x.EndTime&&(result.IsAll||r.BulletinIdList.Contains(x.Id))).OrderByDescending(x=>x.Weight))
        {
            b.ClientReqType=(uint)r.Type;if(b.UpdateTime==0)b.UpdateTime=b.BeginTime;
            if(r.Type==GetBulletinReq.Types.ReqBulletinType.BulletinUpdateTime)
                result.BulletinList.Add(new Bulletin{Id=b.Id,Type=b.Type,UpdateTime=b.UpdateTime,ShowConfigId=b.ShowConfigId,ClientReqType=(uint)r.Type});
            else result.BulletinList.Add(b);
        }
        return result;
    });
}
