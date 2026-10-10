using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Operations;

public sealed partial class GmService
{
    private static readonly GetGachaDisplayRsp Templates=LoadTemplates();
    private static GetGachaDisplayRsp LoadTemplates()
    {
        var result=JsonParser.Default.Parse<GetGachaDisplayRsp>(GrantService.Resource<JsonElement>("display.json").GetRawText());
        result.GachaDisplayInfoList.Add(JsonParser.Default.Parse<GachaDisplayInfo>(GrantService.Resource<JsonElement>("companion-display.json").GetRawText()));
        return result;
    }
    private static uint NewRandom()=>(uint)RandomNumberGenerator.GetInt32(1,int.MaxValue);
    private OperationsState State(CampaignTransaction tx)
    {
        var s=tx.Campaign.Operations;
        if(s.GachaRandom==0)s.GachaRandom=NewRandom();
        s.Pools??=Templates.GachaDisplayInfoList.Select(d=>
        {
            int type=(int)d.GachaType;bool equip=type==46,elf=type==48;
            var ids=elf?d.CommonData.UpElfList:equip?d.CommonData.UpWeaponList.Select(x=>x.Id):d.CommonData.UpAvatarList;
            string kind=elf?"elf":equip?"weapon":"avatar";
            var allowed=ids.Where(id=>GrantService.Catalog.Items.Any(x=>x.Kind==kind&&x.Id==id)).Take(6).ToArray();
            if(allowed.Length==0)allowed=[elf?180u:equip?20001u:101u];
            var entries=allowed.Select((id,i)=>new PoolEntry(new(kind,id),i==0?15u:100u,i==0)).ToList();
            if(equip)entries.AddRange(d.CommonData.UpStigmataList.Where(x=>GrantService.Catalog.Items.Any(c=>c.Kind=="stigmata"&&c.Id==x.Id)).Select(x=>new PoolEntry(new("stigmata",x.Id),100,false)));
            entries.Add(new(new("material",3000,10),1000,false));
            return new LocalPool(type,type==20?"家园补给":elf?"协同者补给":equip?"装备补给":"角色补给",true,type==20?200u:280u,type==20?1110u:equip||elf?1102u:1103u,type==20?100u:equip||elf?60u:90u,entries.ToArray());
        }).ToList();
        // One-time 1.4.0 upgrade: that release seeded both current supplies disabled.
        // Later explicit GM disable operations are retained after this marker is saved.
        if(s.DisplayVersion==0){s.Pools=s.Pools.Select(p=>p with{Enabled=true}).ToList();s.DisplayVersion=1;}
        if(s.DisplayVersion<2)
        {
            if(s.Pools.All(p=>p.Type!=48))s.Pools.Add(new(48,"协同者补给",true,280,1102,60,[new(new("elf",180),15,true),new(new("material",3000,10),1000,false)]));
            s.DisplayVersion=2;
        }
        if(s.DisplayVersion<3)
        {
            // Repair only the old seed that discarded unknown 9.1 avatar 21101.
            // Keep every local draw/pity counter and unrelated GM pool choice.
            s.Pools=s.Pools.Select(p=>p.Type==44&&p.Entries.Length==5&&
                p.Entries.Select(x=>x.Item.Id).SequenceEqual(new uint[]{801,112,603,302,3000})&&p.Entries[0].Rare
                ?p with{Entries=p.Entries.Select((x,i)=>i==0?x with{Item=x.Item with{Id=21101}}:x).ToArray()}:p).ToList();
            foreach(var p in s.Pools.Where(x=>x.Type is 20 or 44))
                s.TenPullPity[p.Type]=(uint)s.DrawLog.Where(x=>x.Type==p.Type).Reverse()
                    .TakeWhile(x=>!GrantService.Catalog.Items.Any(i=>i.Kind=="avatar"&&i.Card==x.Id&&i.Rarity>=2)).Take(9).Count();
            s.DisplayVersion=3;
        }
        return s;
    }
    private static void ValidatePool(LocalPool p)
    {
        if(p.Type is not (20 or 44 or 46 or 48)||string.IsNullOrWhiteSpace(p.Name)||p.Name.Length>40||p.Cost is <1 or >10000||p.Pity is <1 or >1000||p.Entries is null||p.Entries.Length is <1 or >100||!p.Entries.Any(x=>x.Rare))throw new ArgumentException("奖池需包含稀有奖励，保底 1–1000 抽，单抽价格 1–10000 水晶。");
        uint ticket=p.Type==20?1110u:p.Type==44?1103u:1102u;
        if(p.Ticket!=ticket)throw new ArgumentException("补给券与奖池类型不一致。");
        if(p.BeginTime>=p.EndTime||p.EndTime>2147483647)throw new ArgumentException("补给结束时间必须晚于开始时间，且不超过 2038 年。");
        foreach(var e in p.Entries){GrantService.Validate(e.Item);if(e.Weight is <1 or >100000||e.Item.Kind is "scoin" or "hcoin" or "stamina")throw new ArgumentException("补给奖品必须为角色、装备或材料；权重 1–100000。");}
        if(p.Entries.Where(x=>x.Rare).Any(x=>p.Type==48?x.Item.Kind!="elf":p.Type==46?x.Item.Kind is not ("weapon" or "stigmata"):x.Item.Kind!="avatar"))throw new ArgumentException("协同者补给稀有奖励须为协同者 / 人偶，角色补给须为女武神，装备补给须为武器或圣痕。");
        if(p.Type is 20 or 44&&p.Entries.Where(x=>x.Rare).Any(x=>GrantService.Find("avatar",x.Item.Id).Rarity<3))throw new ArgumentException("角色保底奖励必须为初始 S 级女武神。");
    }
    private GetGachaDisplayRsp Display(CampaignTransaction tx,GetGachaDisplayReq r)
    {
        var s=State(tx);bool has=r.HasType&&(int)r.Type!=0;
        var result=new GetGachaDisplayRsp{Retcode=GetGachaDisplayRsp.Types.Retcode.Succ,IsAll=r.IsAll||!has,GachaRandom=s.GachaRandom};
        if(has){if(!Enum.IsDefined(r.Type)||r.Type==GachaType.Error){result.Retcode=GetGachaDisplayRsp.Types.Retcode.Fail;return result;}result.Type=r.Type;}
        foreach(var p in s.Pools!.Where(x=>x.Enabled&&x.BeginTime<=Now&&Now<x.EndTime&&(!has||x.Type==(int)r.Type)))
        {
            var d=Templates.GachaDisplayInfoList.Single(x=>(int)x.GachaType==p.Type).Clone();
            d.CommonData.DataBeginTime=p.BeginTime;d.CommonData.DataEndTime=p.EndTime;
            d.CommonData.Title=p.Name;d.CommonData.Content=$"本地自定义补给；{p.Pity} 抽内至少获得一项稀有奖励。已拥有的角色转换为 30 碎片。";
            d.CommonData.UpAvatarList.Clear();d.CommonData.UpWeaponList.Clear();d.CommonData.UpStigmataList.Clear();d.CommonData.UpElfList.Clear();
            foreach(var e in p.Entries.Where(x=>x.Rare))
            {
                if(e.Item.Kind=="avatar")d.CommonData.UpAvatarList.Add(e.Item.Id);
                if(e.Item.Kind=="elf")d.CommonData.UpElfList.Add(e.Item.Id);
                if(e.Item.Kind=="weapon")d.CommonData.UpWeaponList.Add(new WeaponDetailData{Id=e.Item.Id,Level=e.Item.Level});
                if(e.Item.Kind=="stigmata")d.CommonData.UpStigmataList.Add(new StigmataDetailData{Id=e.Item.Id,Level=e.Item.Level});
            }
            if(d.AdventureGachaData is {} a){a.TicketHcoinCost=p.Cost;a.TicketMaterialId=p.Ticket;a.DisplayMaxTimes=p.Pity;a.NoProtectGachaTimes=s.Pity.GetValueOrDefault(p.Type);a.GachaTimes=s.Draws.GetValueOrDefault(p.Type);a.IsProtectDisplay=true;}
            if(d.PjmsGachaData is {} b){b.TicketHcoinCost=p.Cost;b.TicketMaterialId=p.Ticket;b.DisplayProtectTimes=p.Pity;b.NoProtectGachaTimes=s.Pity.GetValueOrDefault(p.Type);b.GachaTimes=s.Draws.GetValueOrDefault(p.Type);b.ProtectDisplayInfo=new(){NoProtectGachaTimes=b.NoProtectGachaTimes};var rare=p.Entries.First(x=>x.Rare).Item;if(rare.Kind=="avatar")b.ProtectDisplayInfo.DisplayKeyAvatar=rare.Id;else if(rare.Kind=="elf")b.ProtectDisplayInfo.DisplayKeyElfCardId=CompanionService.Find(rare.Id).Card;else b.ProtectDisplayInfo.DisplayKeyItemList.Add(rare.Id);}
            result.GachaDisplayInfoList.Add(d);
        }
        return result;
    }
    public GetGachaDisplayRsp GachaDisplay(uint uid,GetGachaDisplayReq r)=>store.Campaign(uid,tx=>Display(tx,r));
    public GachaRsp Draw(uint uid,GachaReq r)
    {
        try{return store.Campaign(uid,tx=>
        {
            var s=State(tx);GachaRsp Fail(GachaRsp.Types.Retcode code)=>new(){Retcode=code,Type=r.Type,Display=Display(tx,new(){IsAll=true})};
            if((int)r.Type==0||!Enum.IsDefined(r.Type))return new GachaRsp{Retcode=GachaRsp.Types.Retcode.GachaClosed};
            if(r.Num is not (1 or 10)||r.IsUseFreeGacha||r.SimulateMagic!=0)return Fail(GachaRsp.Types.Retcode.Fail);
            string key=$"draw:{r.GachaRandom}";
            if(tx.Receipt(key) is {} old)
            {
                var saved=JsonSerializer.Deserialize<DrawReceipt>(old)!;
                return saved.Request==Convert.ToBase64String(r.ToByteArray())?GachaRsp.Parser.ParseFrom(saved.Response):Fail(GachaRsp.Types.Retcode.Fail);
            }
            var p=s.Pools!.FirstOrDefault(p=>p.Type==(int)r.Type&&p.Enabled&&p.BeginTime<=Now&&Now<p.EndTime);
            if(p is null)return Fail(GachaRsp.Types.Retcode.GachaClosed);
            if(r.GachaRandom==0||r.GachaRandom!=s.GachaRandom)return Fail(GachaRsp.Types.Retcode.Fail);
            if(r.IsUseHcoin){uint cost=checked(p.Cost*r.Num);if(tx.Lobby.Hcoin<cost)return Fail(GachaRsp.Types.Retcode.HcoinLack);tx.Lobby=tx.Lobby with{Hcoin=tx.Lobby.Hcoin-cost};}
            else {uint tickets=tx.Campaign.Materials.GetValueOrDefault(p.Ticket);if(tickets<r.Num)return Fail(GachaRsp.Types.Retcode.TicketLack);tx.Campaign.Materials[p.Ticket]=tickets-r.Num;}
            var result=new GachaRsp{Retcode=GachaRsp.Types.Retcode.Succ,Type=r.Type};
            for(int n=0;n<r.Num;n++)
            {
                uint pity=s.Pity.GetValueOrDefault(p.Type)+1;
                bool tenDue=p.Type is 20 or 44&&s.TenPullPity.GetValueOrDefault(p.Type)>=9;
                bool Qualifying(PoolEntry x)=>x.Item.Kind=="avatar"&&GrantService.Find("avatar",x.Item.Id).Rarity>=2;
                var entries=p.Entries.Where(x=>pity>=p.Pity?x.Rare:!tenDue||Qualifying(x)).ToArray();
                int pick=RandomNumberGenerator.GetInt32(checked((int)entries.Sum(x=>(long)x.Weight)));var entry=entries[^1];
                foreach(var candidate in entries){pick-=(int)candidate.Weight;if(pick<0){entry=candidate;break;}}
                var actual=GrantService.Apply(tx,uid,entry.Item);uint id=entry.Item.Kind is "avatar" or "elf"?GrantService.Find(entry.Item.Kind,entry.Item.Id).Card:entry.Item.Id;
                result.ItemList.Add(new GachaItem{ItemId=id,Num=entry.Item.Num,Level=entry.Item.Level,IsRareDrop=entry.Rare,SplitFragmentNum=actual.Kind=="material"&&entry.Item.Kind is "avatar" or "elf"?actual.Num:0});
                s.Pity[p.Type]=entry.Rare?0:pity;s.Draws[p.Type]=checked(s.Draws.GetValueOrDefault(p.Type)+1);s.DrawLog.Add(new(p.Type,Now,id,entry.Item.Num,entry.Rare));
                if(p.Type is 20 or 44)s.TenPullPity[p.Type]=Qualifying(entry)?0:s.TenPullPity.GetValueOrDefault(p.Type)+1;
            }
            if(s.DrawLog.Count>1000)s.DrawLog.RemoveRange(0,s.DrawLog.Count-1000);
            s.GachaRandom=NewRandom();s.Revision++;result.GachaRandom=s.GachaRandom;result.Display=Display(tx,new(){IsAll=true});
            tx.SaveReceipt(key,JsonSerializer.SerializeToUtf8Bytes(new DrawReceipt(Convert.ToBase64String(r.ToByteArray()),result.ToByteArray())));return result;
        });}catch(ArgumentException){return new(){Retcode=GachaRsp.Types.Retcode.EquipmentFull};}
    }
    private sealed record DrawReceipt(string Request,byte[] Response);
    public BuyGachaTicketRsp BuyTickets(uint uid,BuyGachaTicketReq r)=>store.Campaign(uid,tx=>
    {
        var p=State(tx).Pools!.FirstOrDefault(x=>x.Enabled&&x.BeginTime<=Now&&Now<x.EndTime&&x.Ticket==r.MaterialId);var rsp=new BuyGachaTicketRsp{MaterialId=r.MaterialId,Num=r.Num};
        if(p is null||r.Num is <1 or >100){rsp.Retcode=BuyGachaTicketRsp.Types.Retcode.MaterialIdError;return rsp;}
        uint cost=checked(p.Cost*r.Num);if(tx.Lobby.Hcoin<cost){rsp.Retcode=BuyGachaTicketRsp.Types.Retcode.HcoinLack;return rsp;}
        GrantService.Apply(tx,uid,new("material",r.MaterialId,r.Num));tx.Lobby=tx.Lobby with {Hcoin=tx.Lobby.Hcoin-cost};rsp.Retcode=BuyGachaTicketRsp.Types.Retcode.Succ;rsp.HcoinCost=cost;return rsp;
    });
    public GetGachaLogRsp GachaLog(uint uid)=>store.Campaign(uid,tx=>new GetGachaLogRsp{Retcode=GetGachaLogRsp.Types.Retcode.Succ,
        LogInfoList={tx.Campaign.Operations.DrawLog.GroupBy(x=>x.Type).Select(g=>new GetGachaLogRsp.Types.GachaLogInfo{GachaType=(GachaType)g.Key,LogList={g.Reverse().Select(x=>new GachaLog{Time=x.Time,Item=new GachaItem{ItemId=x.Id,Num=x.Num,Level=1,IsRareDrop=x.Rare}})}})}});
    public GetGachaProbRsp Probability(uint uid,GetGachaProbReq r)=>store.Campaign(uid,tx=>
    {
        var p=State(tx).Pools!.FirstOrDefault(x=>x.Enabled&&x.BeginTime<=Now&&Now<x.EndTime&&x.Type==r.GachaType);var rsp=new GetGachaProbRsp{GachaType=r.GachaType,Retcode=p is null?GetGachaProbRsp.Types.Retcode.Fail:GetGachaProbRsp.Types.Retcode.Succ};
        if(p is null)return rsp;double total=p.Entries.Sum(x=>(double)x.Weight);
        foreach(var e in p.Entries)rsp.DetailProbList.Add(new GachaDetailProb{Name=GrantService.Name(e.Item.Kind,e.Item.Id),Content=$"数量 {e.Item.Num}",Prob=(e.Weight/total*100).ToString("0.####",CultureInfo.InvariantCulture)+"%",Star=e.Rare?"稀有":"普通",IsUp=e.Rare});
        rsp.TotalProbList.Add(new GachaTotalProb{Name=$"本地基础概率；{p.Pity} 抽保底",Prob="100%"});return rsp;
    });
}
