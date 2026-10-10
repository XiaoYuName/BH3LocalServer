using BH3.Persistence;
using BH3.Protocol.Messages;

namespace BH3.Game.Operations;

public sealed record CompanionCost(uint Id,uint Num);
public sealed record CompanionPrerequisite(uint Id,uint Level);
public sealed record CompanionSkillCost(uint Level,uint Star,uint ElfLevel,CompanionPrerequisite[] Prerequisites,CompanionCost[] Materials);
public sealed record CompanionSkillDefinition(uint Id,uint MaxLevel,uint UnlockStar,bool LocalRules,CompanionSkillCost[] Costs);
public sealed record CompanionStar(uint Star,uint Fragments,uint Level,bool LocalRules);
public sealed record CompanionDefinition(uint Id,string Name,uint Type,uint MaxLevel,uint InitialStar,uint MaxStar,uint Card,uint Fragment,
    uint DuplicateFragments,CompanionSkillDefinition[] Skills,CompanionStar[] Stars,uint ReturnMaterial,uint ReturnNum,uint ReturnMinStar,bool LocalFragmentRule);
public sealed record CompanionLevel(uint Level,uint Exp);
public sealed record CompanionExpMaterial(uint Id,uint Exp,uint Scoin);
public sealed record CompanionCatalog(CompanionDefinition[] Companions,CompanionLevel[] Levels,CompanionExpMaterial[] ExpMaterials);

/// <summary>One persisted inventory for ELF and AstralOps, including shared Dreamseeker fragments.</summary>
public sealed class CompanionService(LobbyStore store)
{
    public static readonly CompanionCatalog Catalog=Load();
    private static CompanionCatalog Load()
    {
        using var source=typeof(CompanionService).Assembly.GetManifestResourceStream("BH3.Game.Operations.companions.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<CompanionCatalog>(source,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
    }
    public static CompanionDefinition Find(uint id)=>Catalog.Companions.SingleOrDefault(x=>x.Id==id)??throw new ArgumentException("未知的协同者 / 人偶 ID。");
    public static IEnumerable<CatalogItem> Items=>Catalog.Companions.Select(x=>new CatalogItem("elf",x.Id,x.Name,x.MaxLevel,x.InitialStar,0,0,x.Fragment,x.Card,x.Skills.Select(s=>s.Id).ToArray()))
        .Concat(Catalog.Companions.SelectMany(x=>new[]{new CatalogItem("material",x.Fragment,x.Name+" · 碎片",1,1,0,0,0,0,[]),new CatalogItem("material",x.Card,x.Name+" · 角色卡",1,1,0,0,0,0,[])}));
    public static bool ValidTeam(CampaignTransaction tx,IEnumerable<uint> ids)
    {
        var team=ids.Where(x=>x!=0).ToArray();
        return team.Length<=1 && team.All(tx.Campaign.Companions.ContainsKey);
    }
    private static void InitialSkills(CompanionDefinition spec,CompanionState state)
    {
        foreach(var skill in spec.Skills.Where(x=>x.UnlockStar<=state.Star))
        {
            var allowed=skill.Costs.Where(c=>c.Star<=state.Star&&c.ElfLevel<=state.Level).Select(c=>c.Level).DefaultIfEmpty(0u).Max();
            if(spec.Type!=1) state.Skills[skill.Id]=Math.Max(state.Skills.GetValueOrDefault(skill.Id),Math.Min(skill.MaxLevel,allowed));
            else if(!state.Skills.ContainsKey(skill.Id) && skill.Costs.Any(c=>c.Level==1&&c.Star<=state.Star&&c.ElfLevel<=state.Level&&c.Materials.Length==0&&c.Prerequisites.All(p=>state.Skills.GetValueOrDefault(p.Id)>=p.Level))) state.Skills[skill.Id]=1;
        }
    }
    private static void SyncShared(CampaignTransaction tx,CompanionDefinition spec,CompanionState state)
    {
        foreach(var other in Catalog.Companions.Where(x=>x.Id!=spec.Id&&x.Fragment==spec.Fragment))
            if(tx.Campaign.Companions.TryGetValue(other.Id,out var target))
            {target.Level=state.Level;target.Star=state.Star;target.Exp=state.Exp;target.Skills=new(state.Skills);target.MaskedSkills=new(state.MaskedSkills);}
    }
    private static Elf Message(uint id,CompanionState state)=>new(){ElfId=id,Star=state.Star,Level=state.Level,Exp=state.Exp,
        SkillList={state.Skills.OrderBy(x=>x.Key).Select(x=>new ElfSkill{SkillId=x.Key,SkillLevel=x.Value,IsMask=state.MaskedSkills.Contains(x.Key)})}};
    public static GetElfDataRsp Snapshot(CampaignTransaction tx)=>new(){Retcode=GetElfDataRsp.Types.Retcode.Succ,IsTakeCompensation=true,
        ElfList={tx.Campaign.Companions.OrderBy(x=>x.Key).Select(x=>Message(x.Key,x.Value))},
        ElfFragmentList={Catalog.Companions.DistinctBy(x=>x.Fragment).Select(x=>new ElfFragment{ElfId=x.Id,FragmentNum=tx.Campaign.Materials.GetValueOrDefault(x.Fragment)})}};
    public GetElfDataRsp Get(uint uid)=>store.Campaign(uid,Snapshot);
    public static GrantItem Grant(CampaignTransaction tx,uint id,uint level)
    {
        var spec=Find(id);
        if(tx.Campaign.Companions.ContainsKey(id))
        {
            AddMaterial(tx,spec.Fragment,spec.DuplicateFragments);
            return new("material",spec.Fragment,spec.DuplicateFragments);
        }
        var sibling=Catalog.Companions.FirstOrDefault(x=>x.Id!=id&&x.Fragment==spec.Fragment&&tx.Campaign.Companions.ContainsKey(x.Id));
        var state=sibling is null?new CompanionState{Level=level,Star=spec.InitialStar}:tx.Campaign.Companions[sibling.Id];
        var created=new CompanionState{Level=state.Level,Star=state.Star,Exp=state.Exp,Skills=new(state.Skills),MaskedSkills=new(state.MaskedSkills)};
        InitialSkills(spec,created);tx.Campaign.Companions[id]=created;return new("elf",id,1,level);
    }
    public static void Set(CampaignTransaction tx,uint id,uint level,uint star,bool maxSkills)
    {
        var spec=Find(id);
        if(level<1||level>spec.MaxLevel||star<spec.InitialStar||star>spec.MaxStar)throw new ArgumentException($"等级 1–{spec.MaxLevel}，阶级 {spec.InitialStar}–{spec.MaxStar}。");
        if(!tx.Campaign.Companions.TryGetValue(id,out var state))throw new ArgumentException("请先解锁该协同者 / 人偶。");
        state.Level=level;state.Exp=0;state.Star=star;
        foreach(var skill in spec.Skills)
        {
            uint cap=skill.UnlockStar>star?0:skill.Costs.Where(c=>c.Star<=star&&c.ElfLevel<=level).Select(c=>c.Level).DefaultIfEmpty(0u).Max();
            uint value=maxSkills?Math.Min(cap,skill.MaxLevel):Math.Min(state.Skills.GetValueOrDefault(skill.Id),cap);
            if(value==0){state.Skills.Remove(skill.Id);state.MaskedSkills.Remove(skill.Id);}else state.Skills[skill.Id]=value;
        }
        InitialSkills(spec,state);SyncShared(tx,spec,state);
    }
    private static void AddMaterial(CampaignTransaction tx,uint id,uint num)
    {
        ulong total=(ulong)tx.Campaign.Materials.GetValueOrDefault(id)+num;
        if(total>999999999)throw new ArgumentException("材料数量超出上限。");
        tx.Campaign.Materials[id]=(uint)total;
    }
    public ElfStarUpRsp StarUp(uint uid,ElfStarUpReq r)=>store.Campaign(uid,tx=>
    {
        var result=new ElfStarUpRsp{ElfId=r.ElfId,Retcode=ElfStarUpRsp.Types.Retcode.ElfNotExist};
        var spec=Catalog.Companions.SingleOrDefault(x=>x.Id==r.ElfId);if(spec is null)return result;
        tx.Campaign.Companions.TryGetValue(r.ElfId,out var state);uint star=state?.Star??0;
        if(star>=spec.MaxStar){result.Retcode=ElfStarUpRsp.Types.Retcode.StarFull;return result;}
        var cost=spec.Stars.Single(x=>x.Star==star);
        if(state is not null&&state.Level<cost.Level){result.Retcode=ElfStarUpRsp.Types.Retcode.LevelLack;return result;}
        uint fragments=tx.Campaign.Materials.GetValueOrDefault(spec.Fragment);
        if(fragments<cost.Fragments){result.Retcode=ElfStarUpRsp.Types.Retcode.FragmentLack;return result;}
        tx.Campaign.Materials[spec.Fragment]=fragments-cost.Fragments;result.IsUnlock=state is null;
        if(state is null){Grant(tx,spec.Id,1);state=tx.Campaign.Companions[spec.Id];}else state.Star++;
        InitialSkills(spec,state);SyncShared(tx,spec,state);result.Retcode=ElfStarUpRsp.Types.Retcode.Succ;return result;
    });
    public AddElfExpByMaterialRsp AddExp(uint uid,AddElfExpByMaterialReq r)=>store.Campaign(uid,tx=>
    {
        var result=new AddElfExpByMaterialRsp{Retcode=AddElfExpByMaterialRsp.Types.Retcode.ElfNotExist};
        if(!tx.Campaign.Companions.TryGetValue(r.ElfId,out var state))return result;
        var spec=Find(r.ElfId);result.OldLevel=state.Level;result.OldExp=state.Exp;
        uint cap=Math.Min(spec.MaxLevel,tx.Lobby.Level);
        if(state.Level>=cap){result.Retcode=AddElfExpByMaterialRsp.Types.Retcode.ElfLevelFull;return result;}
        var material=Catalog.ExpMaterials.SingleOrDefault(x=>x.Id==r.MaterialId);
        if(material is null||r.MaterialNum is 0 or >10000){result.Retcode=AddElfExpByMaterialRsp.Types.Retcode.MaterialCanNotAddExp;return result;}
        uint count=tx.Campaign.Materials.GetValueOrDefault(r.MaterialId);
        if(count<r.MaterialNum){result.Retcode=AddElfExpByMaterialRsp.Types.Retcode.MaterialNotEnough;return result;}
        ulong coin=(ulong)material.Scoin*r.MaterialNum;
        if(tx.Lobby.Scoin<coin){result.Retcode=AddElfExpByMaterialRsp.Types.Retcode.ScoinLack;return result;}
        ulong exp=state.Exp+(ulong)material.Exp*r.MaterialNum;
        tx.Campaign.Materials[r.MaterialId]=count-r.MaterialNum;tx.Lobby=tx.Lobby with{Scoin=tx.Lobby.Scoin-(uint)coin};
        while(state.Level<cap&&exp>=Catalog.Levels.Single(x=>x.Level==state.Level).Exp){exp-=Catalog.Levels.Single(x=>x.Level==state.Level).Exp;state.Level++;}
        state.Exp=state.Level>=cap?0:checked((uint)exp);InitialSkills(spec,state);SyncShared(tx,spec,state);
        result.Retcode=AddElfExpByMaterialRsp.Types.Retcode.Succ;return result;
    });
    private static ElfSkillLevelUpRsp.Types.Retcode SkillCost(CampaignTransaction tx,CompanionState state,CompanionSkillDefinition spec,out CompanionSkillCost? cost)
    {
        cost=null;uint current=state.Skills.GetValueOrDefault(spec.Id);
        if(current>=spec.MaxLevel)return ElfSkillLevelUpRsp.Types.Retcode.LevelFull;
        cost=spec.Costs.SingleOrDefault(x=>x.Level==current+1);
        if(cost is null)return ElfSkillLevelUpRsp.Types.Retcode.NoValidSkill;
        if(state.Star<Math.Max(spec.UnlockStar,cost.Star))return ElfSkillLevelUpRsp.Types.Retcode.ElfStarNotEnough;
        if(state.Level<cost.ElfLevel)return ElfSkillLevelUpRsp.Types.Retcode.ElfLevelNotEnough;
        if(cost.Prerequisites.Any(x=>state.Skills.GetValueOrDefault(x.Id)<x.Level))return ElfSkillLevelUpRsp.Types.Retcode.PreSkillLevelNotEnough;
        if(cost.Materials.Where(x=>x.Id==100).Sum(x=>(long)x.Num)>tx.Lobby.Scoin)return ElfSkillLevelUpRsp.Types.Retcode.ScoinLack;
        if(cost.Materials.Where(x=>x.Id!=100).GroupBy(x=>x.Id).Any(g=>g.Sum(x=>(long)x.Num)>tx.Campaign.Materials.GetValueOrDefault(g.Key)))return ElfSkillLevelUpRsp.Types.Retcode.MaterialNotEnough;
        return ElfSkillLevelUpRsp.Types.Retcode.Succ;
    }
    public ElfSkillLevelUpRsp SkillUp(uint uid,ElfSkillLevelUpReq r)=>store.Campaign(uid,tx=>
    {
        var result=new ElfSkillLevelUpRsp{Retcode=ElfSkillLevelUpRsp.Types.Retcode.ElfLocked};
        if(!tx.Campaign.Companions.TryGetValue(r.ElfId,out var state))return result;
        var spec=Find(r.ElfId);
        if(spec.Type!=1){result.Retcode=ElfSkillLevelUpRsp.Types.Retcode.NotNormalElf;return result;}
        var skill=spec.Skills.SingleOrDefault(x=>x.Id==r.ElfSkillId);
        if(skill is null){result.Retcode=ElfSkillLevelUpRsp.Types.Retcode.NoValidSkill;return result;}
        result.Retcode=SkillCost(tx,state,skill,out var cost);if(result.Retcode!=ElfSkillLevelUpRsp.Types.Retcode.Succ)return result;
        do
        {
            foreach(var item in cost!.Materials)
                if(item.Id==100)tx.Lobby=tx.Lobby with{Scoin=tx.Lobby.Scoin-item.Num};else tx.Campaign.Materials[item.Id]-=item.Num;
            state.Skills[skill.Id]=cost.Level;
        }while(r.IsLevelUpAll&&SkillCost(tx,state,skill,out cost)==ElfSkillLevelUpRsp.Types.Retcode.Succ);
        result.ElfSkill=new(){SkillId=skill.Id,SkillLevel=state.Skills[skill.Id],IsMask=state.MaskedSkills.Contains(skill.Id)};return result;
    });
    public SwitchElfSkillRsp Switch(uint uid,SwitchElfSkillReq r)=>store.Campaign(uid,tx=>
    {
        if(!tx.Campaign.Companions.TryGetValue(r.ElfId,out var state)||state.Skills.GetValueOrDefault(r.SkillId)==0)return new SwitchElfSkillRsp{Retcode=SwitchElfSkillRsp.Types.Retcode.Fail};
        if(r.IsMask)state.MaskedSkills.Add(r.SkillId);else state.MaskedSkills.Remove(r.SkillId);
        SyncShared(tx,Find(r.ElfId),state);return new SwitchElfSkillRsp{Retcode=SwitchElfSkillRsp.Types.Retcode.Succ};
    });
    public ElfFragmentTransformRsp Transform(uint uid,ElfFragmentTransformReq r)
    {
        try{return store.Campaign(uid,tx=>
        {
            var result=new ElfFragmentTransformRsp{Retcode=ElfFragmentTransformRsp.Types.Retcode.Fail};
            var list=r.FragmentList.Select(x=>new CompanionCost(x.Id,x.Num)).ToList();
            if(r.ElfFragmentId!=0||r.ElfFragmentNum!=0)list.Add(new(r.ElfFragmentId,r.ElfFragmentNum));
            if(list.Count is 0 or >100||list.Any(x=>x.Num==0)||list.Select(x=>x.Id).Distinct().Count()!=list.Count)return result;
            var returns=new Dictionary<uint,uint>();
            foreach(var item in list)
            {
                var spec=Catalog.Companions.FirstOrDefault(x=>x.Fragment==item.Id);if(spec is null)return result;
                if(tx.Campaign.Companions.GetValueOrDefault(spec.Id)?.Star<spec.ReturnMinStar || spec.ReturnMinStar>0&&!tx.Campaign.Companions.ContainsKey(spec.Id)) {result.Retcode=ElfFragmentTransformRsp.Types.Retcode.StarLack;return result;}
                if(tx.Campaign.Materials.GetValueOrDefault(item.Id)<item.Num){result.Retcode=ElfFragmentTransformRsp.Types.Retcode.FragmentLack;return result;}
                returns[spec.ReturnMaterial]=checked(returns.GetValueOrDefault(spec.ReturnMaterial)+checked(spec.ReturnNum*item.Num));
            }
            foreach(var item in returns)if((ulong)tx.Campaign.Materials.GetValueOrDefault(item.Key)+item.Value>999999999){result.Retcode=ElfFragmentTransformRsp.Types.Retcode.MaterialFull;return result;}
            foreach(var item in list)tx.Campaign.Materials[item.Id]-=item.Num;
            foreach(var item in returns){AddMaterial(tx,item.Key,item.Value);result.ReturnList.Add(new GenericItemNum{Id=item.Key,Num=item.Value});}
            if(returns.Count==1){result.AddMaterialId=returns.Single().Key;result.AddMaterialNum=returns.Single().Value;}
            result.Retcode=ElfFragmentTransformRsp.Types.Retcode.Succ;return result;
        });}catch(OverflowException){return new(){Retcode=ElfFragmentTransformRsp.Types.Retcode.MaterialFull};}
    }
}
