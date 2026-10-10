using System.Text.Json;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Operations;

public sealed partial class SystemsService
{
    private static uint[] KeyWeaponIds(JsonElement spec)=>spec.GetProperty("mainWeaponIdList").EnumerateArray()
        .Concat(spec.GetProperty("SubWeaponmainIDlist").EnumerateArray()).Select(x=>x.GetUInt32()).Where(x=>x!=0).Distinct().ToArray();
    private static Dictionary<uint,GrandKey> Keys(CampaignTransaction tx)
    {
        var keys=tx.Campaign.Systems.GrandKeys.ToDictionary(x=>x.Key,x=>GrandKey.Parser.ParseFrom(x.Value));
        var weaponMap=Rows("Weapons").ToDictionary(x=>N(x,"id"),x=>N(x,"main"));
        var owned=GrantService.Inventory(tx).Equipment.WeaponList.Select(x=>weaponMap.GetValueOrDefault(x.Id)).ToHashSet();
        foreach(var row in Rows("GrandKey"))
        {
            uint id=N(row,"grandKeyID");
            if(KeyWeaponIds(row).Any(owned.Contains)&&!keys.ContainsKey(id))
                keys[id]=new GrandKey{Id=id,Level=1,UnlockLevel=50};
        }
        SaveKeys(tx,keys);return keys;
    }
    private static void SaveKeys(CampaignTransaction tx,Dictionary<uint,GrandKey> keys)
    {foreach(var (id,key) in keys)tx.Campaign.Systems.GrandKeys[id]=key.ToByteArray();}
    public GetGrandKeyRsp GrandKeys(uint uid,GetGrandKeyReq r)=>store.Campaign(uid,tx=>
    {
        bool all=r.KeyIdList.Count==0||r.KeyIdList.Contains(0);
        return new GetGrandKeyRsp{Retcode=GetGrandKeyRsp.Types.Retcode.Succ,IsAll=all,KeyList={Keys(tx).Values.Where(x=>all||r.KeyIdList.Contains(x.Id)).OrderBy(x=>x.Id)}};
    });
    private static bool CanPay(CampaignTransaction tx,Dictionary<uint,uint> costs)=>costs.All(x=>x.Value<=(x.Key==100?tx.Lobby.Scoin:tx.Campaign.Materials.GetValueOrDefault(x.Key)));
    private static void Pay(CampaignTransaction tx,Dictionary<uint,uint> costs)
    {
        foreach(var (id,num) in costs)if(id==100)tx.Lobby=tx.Lobby with{Scoin=tx.Lobby.Scoin-num};else tx.Campaign.Materials[id]-=num;
    }
    public GrandKeyLevelUpRsp KeyLevel(uint uid,GrandKeyLevelUpReq r)=>store.Campaign(uid,tx=>
    {
        var result=new GrandKeyLevelUpRsp{Retcode=GrandKeyLevelUpRsp.Types.Retcode.Fail,KeyId=r.KeyId};var keys=Keys(tx);
        if(!keys.TryGetValue(r.KeyId,out var key))return result;
        var spec=Rows("GrandKey").FirstOrDefault(x=>N(x,"grandKeyID")==r.KeyId);
        if(spec.ValueKind==JsonValueKind.Undefined||key.Level>=N(spec,"maxGrandKeyLv"))return result;
        var level=Rows("GrandKeyLevel").FirstOrDefault(x=>N(x,"grandKeyID")==r.KeyId&&N(x,"level")==key.Level);
        if(level.ValueKind==JsonValueKind.Undefined||tx.Lobby.Level<N(level,"playerLevel"))return result;
        var maps=Rows("Weapons").ToDictionary(x=>N(x,"id"),x=>N(x,"main"));var main=KeyWeaponIds(spec).ToHashSet();
        if(!GrantService.Inventory(tx).Equipment.WeaponList.Any(x=>main.Contains(maps.GetValueOrDefault(x.Id))&&x.Level>=N(level,"unlockWeaponLevel")))return result;
        var costs=level.GetProperty("upgradeMaterial").EnumerateArray().GroupBy(x=>N(x,"ID")).ToDictionary(g=>g.Key,g=>(uint)g.Sum(x=>(long)N(x,"Num")));
        if(!CanPay(tx,costs))return result;
        Pay(tx,costs);key.Level++;SaveKeys(tx,keys);result.Level=key.Level;result.Retcode=GrandKeyLevelUpRsp.Types.Retcode.Succ;return result;
    });
    public GrandKeyBreachRsp KeyBreach(uint uid,GrandKeyBreachReq r)=>store.Campaign(uid,tx=>
    {
        var result=new GrandKeyBreachRsp{Retcode=GrandKeyBreachRsp.Types.Retcode.WeaponLimit,KeyId=r.KeyId};var keys=Keys(tx);
        if(!keys.TryGetValue(r.KeyId,out var key))return result;
        var spec=Rows("GrandKey").FirstOrDefault(x=>N(x,"grandKeyID")==r.KeyId);if(spec.ValueKind==JsonValueKind.Undefined)return result;
        var maps=Rows("Weapons").ToDictionary(x=>N(x,"id"),x=>N(x,"main"));var owned=GrantService.Inventory(tx).Equipment.WeaponList.Select(x=>maps.GetValueOrDefault(x.Id)).ToHashSet();
        var variants=spec.GetProperty("mainWeaponIdList").EnumerateArray().Select(x=>x.GetUInt32()).ToArray();
        var sub=spec.GetProperty("SubWeaponmainIDlist").EnumerateArray().Select(x=>x.GetUInt32()).ToArray();
        uint count=(uint)variants.Where((id,i)=>owned.Contains(id)||(i<sub.Length&&sub[i]!=0&&owned.Contains(sub[i]))).Count();
        uint max=count==0?0:count-1;if(key.BreachLevel>=max)return result;
        key.BreachLevel++;SaveKeys(tx,keys);result.BreachLevel=key.BreachLevel;result.Retcode=GrandKeyBreachRsp.Types.Retcode.Succ;return result;
    });
    private static bool SkillAllowed(GrandKey key,uint skill)
    {
        var row=Rows("GrandKeyBuff").FirstOrDefault(x=>N(x,"grandKeySkill")==skill&&N(x,"grandKeyID")==key.Id);
        return row.ValueKind!=JsonValueKind.Undefined?key.Level>=N(row,"unlockgrandKeyLevel")&&key.BreachLevel>=N(row,"breachLevel")
            :key.UnlockSkillList.Contains(skill);
    }
    public GrandKeySetSkillRsp KeySelect(uint uid,GrandKeySetSkillReq r)=>store.Campaign(uid,tx=>
    {
        var result=new GrandKeySetSkillRsp{Retcode=GrandKeySetSkillRsp.Types.Retcode.Fail};var keys=Keys(tx);
        if(r.KeyList.Count is 0 or >4||r.KeyList.Select(x=>x.KeyId).Distinct().Count()!=r.KeyList.Count||r.KeyList.Any(x=>!keys.TryGetValue(x.KeyId,out var key)||!SkillAllowed(key,x.SkillId)))return result;
        foreach(var item in r.KeyList)keys[item.KeyId].Skill=item.Clone();SaveKeys(tx,keys);result.Retcode=GrandKeySetSkillRsp.Types.Retcode.Succ;return result;
    });
    public GrandKeyActivateSkillRsp KeyActivate(uint uid,GrandKeyActivateSkillReq r)=>store.Campaign(uid,tx=>
    {
        var result=new GrandKeyActivateSkillRsp{Retcode=GrandKeyActivateSkillRsp.Types.Retcode.Fail};var keys=Keys(tx);var costs=new Dictionary<uint,uint>();
        if(r.KeyList.Count is 0 or >4||r.KeyList.Select(x=>x.KeyId).Distinct().Count()!=r.KeyList.Count)return result;
        foreach(var skill in r.KeyList)
        {
            if(!keys.TryGetValue(skill.KeyId,out var key)||!SkillAllowed(key,skill.SkillId))return result;
            var row=Rows("GrandKeyBuffActiveInfo").FirstOrDefault(x=>N(x,"UnlockGrandKeyLevel")==key.Level&&N(x,"Duration")==skill.LastTime);
            if(row.ValueKind==JsonValueKind.Undefined)return result;
            foreach(var part in row.GetProperty("Cost").EnumerateArray())
            {var v=part.GetString()!.Split(':');uint id=uint.Parse(v[0]),num=uint.Parse(v[1]);costs[id]=checked(costs.GetValueOrDefault(id)+num);}
        }
        if(keys.Values.Count(k=>k.EndTime>Now&&!r.KeyList.Any(s=>s.KeyId==k.Id))+r.KeyList.Count>4)return result;
        if(!CanPay(tx,costs))return result;Pay(tx,costs);
        foreach(var skill in r.KeyList){var key=keys[skill.KeyId];key.Skill=skill.Clone();key.EndTime=checked(Now+skill.LastTime);key.ActivateLevel=key.Level;}
        SaveKeys(tx,keys);result.Retcode=GrandKeyActivateSkillRsp.Types.Retcode.Succ;return result;
    });
    public GrandKeyResetRsp KeyReset(uint uid,GrandKeyResetReq r)=>store.Campaign(uid,tx=>
    {
        var result=new GrandKeyResetRsp{Retcode=GrandKeyResetRsp.Types.Retcode.Fail};var keys=Keys(tx);
        if(r.KeyIdList.Count is 0 or >9||r.KeyIdList.Any(x=>!keys.ContainsKey(x)))return result;
        foreach(uint id in r.KeyIdList){keys[id].EndTime=0;keys[id].ActivateLevel=0;result.KeyIdList.Add(id);}SaveKeys(tx,keys);result.Retcode=GrandKeyResetRsp.Types.Retcode.Succ;return result;
    });
    public GrandKeyUnlockSkillRsp KeyUnlock(uint uid,GrandKeyUnlockSkillReq r)=>store.Campaign(uid,tx=>
    {
        var result=new GrandKeyUnlockSkillRsp{Retcode=GrandKeyUnlockSkillRsp.Types.Retcode.NotMeetCondition};var keys=Keys(tx);
        if(r.SkillList.Count is 0 or >32||r.SkillList.Any(x=>!keys.TryGetValue(x.KeyId,out var key)||!SkillAllowed(key,x.SkillId)))return result;
        foreach(var s in r.SkillList){var key=keys[s.KeyId];if(!key.UnlockSkillList.Contains(s.SkillId))key.UnlockSkillList.Add(s.SkillId);}
        SaveKeys(tx,keys);result.Retcode=GrandKeyUnlockSkillRsp.Types.Retcode.Succ;return result;
    });
    public GrandKeyContrastRsp KeyTune(uint uid,GrandKeyContrastReq r)=>store.Campaign(uid,tx=>
    {
        var result=new GrandKeyContrastRsp{Retcode=GrandKeyContrastRsp.Types.Retcode.Fail,UniqueId=r.UniqueId};var inv=GrantService.Inventory(tx);
        var weapon=inv.Equipment.WeaponList.FirstOrDefault(x=>x.UniqueId==r.UniqueId);if(weapon is null)return result;
        var row=Rows("GrandKeyWeaponContrast").FirstOrDefault(x=>N(x,"weaponIDBefore")==weapon.Id);if(row.ValueKind==JsonValueKind.Undefined)return result;
        weapon.Id=N(row,"weaponIDAfter");GrantService.Save(tx,inv.Avatars,inv.Equipment,uid);Keys(tx);
        result.UniqueIdAfter=weapon.UniqueId;result.Retcode=GrandKeyContrastRsp.Types.Retcode.Succ;return result;
    });
}
