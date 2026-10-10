using System.Text.Json;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Operations;

public sealed record CatalogItem(string Kind,uint Id,string Name,uint MaxLevel,uint Rarity,uint Weapon,uint Dress,uint Fragment,uint Card,uint[] Skills);
public sealed record Subskill(uint Id,uint Skill,uint MaxLevel);
public sealed record ItemCatalog(CatalogItem[] Items, Subskill[] Subskills);

public static class GrantService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static T Resource<T>(string name)
    {
        using var s=typeof(GrantService).Assembly.GetManifestResourceStream("BH3.Game.Operations."+name)!;
        return JsonSerializer.Deserialize<T>(s,Json)!;
    }
    public static readonly ItemCatalog Catalog=LoadCatalog();
    private static ItemCatalog LoadCatalog()
    {
        var catalog=Resource<ItemCatalog>("catalog.json");
        return catalog with {Items=catalog.Items.Concat(CompanionService.Items).DistinctBy(x=>(x.Kind,x.Id)).ToArray()};
    }
    public static readonly Dictionary<uint,uint[]> Dresses=Resource<Dictionary<uint,uint[]>>("dresses.json");
    private static readonly Dictionary<(string,uint),CatalogItem> Index=Catalog.Items.ToDictionary(x=>(x.Kind,x.Id));
    public static CatalogItem Find(string kind,uint id) => Index.GetValueOrDefault((kind,id)) ?? throw new ArgumentException("目录中没有此物品，请检查种类和 ID。");
    public static string Name(string kind,uint id) => Index.GetValueOrDefault((kind,id))?.Name ?? $"{kind} #{id}";
    public static void Validate(GrantItem item)
    {
        if(item.Num==0) throw new ArgumentException("数量必须大于 0。");
        if(item.Kind=="dress")
        { if(!Dresses.ContainsKey(item.Id)||item.Num!=1)throw new ArgumentException("服装 ID 无效或数量不为 1。");return; }
        if(item.Kind is "scoin" or "hcoin" or "stamina")
        { if(item.Num>(item.Kind=="stamina"?9999u:100_000_000u)) throw new ArgumentException("单次货币数量过大。"); return; }
        var spec=Find(item.Kind,item.Id);
        if(item.Level<1 || item.Level>spec.MaxLevel || item.Num>(item.Kind=="material"?1_000_000u:100u)) throw new ArgumentException("物品数量或等级超出范围。");
        if(item.Kind=="avatar" && item.Num!=1) throw new ArgumentException("每次解锁一名女武神。");
        if(item.Kind=="elf"&&item.Num!=1)throw new ArgumentException("每次解锁一名协同者 / 人偶。");
        if(item.Kind=="material"&&CompanionService.Catalog.Companions.Any(x=>x.Card==item.Id)&&item.Num>100)throw new ArgumentException("角色卡每次最多 100 张。");
    }
    public static (GetAvatarDataRsp Avatars,GetEquipmentDataRsp Equipment) Inventory(CampaignTransaction tx)
    {
        if(tx.Inventory is {} data)
        {
            var avatars=GetAvatarDataRsp.Parser.ParseFrom(data.Avatars);
            // Older local draws put duplicates in Materials while the client reads
            // Avatar.fragment. Merge once, preserving the imported fragment balance.
            bool changed=false;
            foreach(var avatar in avatars.AvatarList)
            {
                uint fragment=Index.GetValueOrDefault(("avatar",avatar.AvatarId))?.Fragment??0;
                if(fragment!=0&&tx.Campaign.Materials.TryGetValue(fragment,out uint count)&&count>0)
                {avatar.Fragment=Add(avatar.Fragment,count);tx.Campaign.Materials.Remove(fragment);changed=true;}
            }
            if(changed)tx.Inventory=data with{Avatars=avatars.ToByteArray()};
            return (avatars,GetEquipmentDataRsp.Parser.ParseFrom(data.Equipment));
        }
        var l=tx.Lobby;
        return(new(){Retcode=GetAvatarDataRsp.Types.Retcode.Succ,IsAll=true,AvatarList={new Avatar{AvatarId=l.AvatarId,Level=l.AvatarLevel,Exp=l.AvatarExp,Star=1,WeaponUniqueId=1,DressId=l.DressId,DressList={l.DressId}}}},
            new(){Retcode=GetEquipmentDataRsp.Types.Retcode.Succ,IsAll=true,WeaponList={new Weapon{Id=l.WeaponId,UniqueId=1,Level=1,IsProtected=true}}});
    }
    public static void Save(CampaignTransaction tx,GetAvatarDataRsp a,GetEquipmentDataRsp e,uint uid)
    {
        foreach(uint dress in tx.Campaign.Operations.OwnedDresses)
            foreach(var avatar in a.AvatarList.Where(x=>Dresses[dress].Contains(x.AvatarId)))
                if(!avatar.DressList.Contains(dress))avatar.DressList.Add(dress);
        var old=tx.Inventory;
        tx.Inventory=new(a.ToByteArray(),e.ToByteArray(),old?.SourceSha256??new string('0',64),old?.SourceUid??uid,old?.ImportedUtc??DateTimeOffset.UtcNow.ToString("O"));
    }
    private static uint Add(uint value,uint num,uint limit=999_999_999) => (ulong)value+num<=limit?value+num:throw new ArgumentException("余额或背包数量将超出上限，操作已取消。");
    private static uint Next(GetEquipmentDataRsp e) => checked(e.WeaponList.Select(x=>x.UniqueId).Concat(e.StigmataList.Select(x=>x.UniqueId)).DefaultIfEmpty(0u).Max()+1);
    public static GrantItem Apply(CampaignTransaction tx,uint uid,GrantItem item)
    {
        Validate(item);
        switch(item.Kind)
        {
            case "scoin": tx.Lobby=tx.Lobby with {Scoin=Add(tx.Lobby.Scoin,item.Num)};return item;
            case "hcoin": tx.Lobby=tx.Lobby with {Hcoin=Add(tx.Lobby.Hcoin,item.Num)};return item;
            case "stamina": tx.Lobby=tx.Lobby with {Stamina=Add(tx.Lobby.Stamina,item.Num,9999)};return item;
            case "elf":return CompanionService.Grant(tx,item.Id,item.Level);
            case "material":
                var companion=CompanionService.Catalog.Companions.SingleOrDefault(x=>x.Card==item.Id);
                if(companion is not null){GrantItem actual=item;for(uint i=0;i<item.Num;i++)actual=CompanionService.Grant(tx,companion.Id,1);return actual;}
                var avatarSpec=Catalog.Items.FirstOrDefault(x=>x.Kind=="avatar"&&x.Fragment==item.Id);
                if(avatarSpec is not null)
                {
                    var inv=Inventory(tx);var owned=inv.Avatars.AvatarList.FirstOrDefault(x=>x.AvatarId==avatarSpec.Id);
                    if(owned is not null){owned.Fragment=Add(owned.Fragment,item.Num);Save(tx,inv.Avatars,inv.Equipment,uid);return item;}
                }
                tx.Campaign.Materials[item.Id]=Add(tx.Campaign.Materials.GetValueOrDefault(item.Id),item.Num);return item;
            case "dress":
                tx.Campaign.Operations.OwnedDresses.Add(item.Id);
                var dressInventory=Inventory(tx);Save(tx,dressInventory.Avatars,dressInventory.Equipment,uid);return item;
        }
        var spec=Find(item.Kind,item.Id);var(a,e)=Inventory(tx);
        if(e.WeaponList.Count+e.StigmataList.Count+item.Num>2300) throw new ArgumentException("装备背包已满（2300 件）。");
        switch(item.Kind)
        {
            case "weapon": for(int i=0;i<item.Num;i++) e.WeaponList.Add(new Weapon{Id=item.Id,UniqueId=Next(e),Level=item.Level,IsProtected=true});break;
            case "stigmata": for(int i=0;i<item.Num;i++) e.StigmataList.Add(new Stigmata{Id=item.Id,UniqueId=Next(e),Level=item.Level,IsProtected=true});break;
            case "avatar":
                if(a.AvatarList.Any(x=>x.AvatarId==item.Id))
                {
                    if(spec.Fragment==0) throw new ArgumentException("该女武神已经拥有。");
                    return Apply(tx,uid,new("material",spec.Fragment,30));
                }
                uint weaponId=Next(e);e.WeaponList.Add(new Weapon{Id=spec.Weapon,UniqueId=weaponId,Level=1,IsProtected=true});
                var av=new Avatar{AvatarId=item.Id,Level=item.Level,Star=spec.Rarity,WeaponUniqueId=weaponId,DressId=spec.Dress,DressList={spec.Dress}};
                foreach(uint skill in spec.Skills) av.SkillList.Add(new AvatarSkill{SkillId=skill,SubSkillList={Catalog.Subskills.Where(s=>s.Skill==skill).Select(s=>new AvatarSubSkill{SubSkillId=s.Id,Level=1})}});
                a.AvatarList.Add(av);break;
        }
        Save(tx,a,e,uid);return item;
    }
    public static GrantItem FromMail(MailItem item)
    {
        string? kind=new[]{"weapon","stigmata","material"}.FirstOrDefault(k=>Index.ContainsKey((k,item.ItemId)));
        return new(kind??throw new ArgumentException("不支持的邮件附件 ID。"),item.ItemId,item.Num,Math.Max(1,item.Level));
    }
}
