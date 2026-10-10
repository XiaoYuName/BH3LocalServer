using System.Security.Cryptography;
using System.Text.Json;
using BH3.Game.Campaign;
using BH3.Game.Operations;
using BH3.Persistence;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace BH3.Game.Players;

public sealed record AccountCopyPlan(uint SourceUid, string SourceSha256, bool TransportComplete,
    GetMainDataRsp Main, GetAvatarDataRsp Avatars, GetEquipmentDataRsp Equipment, string[] Warnings,GetElfDataRsp? Companions=null,GetGrandKeyRsp? GrandKeys=null);
public sealed record AccountImportResult(uint LocalUid, uint SourceUid, string SourceSha256, int Avatars, int Weapons, int Stigmata,
    uint Level, bool AlreadyImported, bool BalancesPreserved, string[] Warnings);

public static class AccountCopyImport
{
    public static AccountCopyPlan Read(string path, bool allowPartial)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Account copy is missing or exceeds 64 MiB.");
        byte[] bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.GetProperty("Format").GetString() != "BH3Capture.AccountCopy" || root.GetProperty("FormatVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported account-copy format.");
        if (!(root.GetProperty("ClientVersion").GetString() ?? "").Contains("9.1.0", StringComparison.Ordinal))
            throw new InvalidDataException("Only captured 9.1.0 account data is supported.");
        var accounts = root.GetProperty("Accounts");
        if (accounts.GetArrayLength() != 1) throw new InvalidDataException("Import requires exactly one source account; ambiguous account copies are rejected.");
        var account = accounts[0]; uint sourceUid = account.GetProperty("SourceUid").GetUInt32();
        if (sourceUid == 0) throw new InvalidDataException("Source UID is missing.");
        bool complete = account.GetProperty("TransportComplete").GetBoolean() && account.GetProperty("CoreSnapshotComplete").GetBoolean();
        if (!complete && !allowPartial) throw new InvalidDataException("Capture is incomplete; reviewed imports require --allow-partial explicitly.");
        var avatars = new Dictionary<uint, Avatar>(); var weapons = new Dictionary<uint, Weapon>(); var stigmata = new Dictionary<uint, Stigmata>();
        bool avatarAll = false, equipmentAll = false, mainAll = false;
        var main = new GetMainDataRsp(); DateTimeOffset? priorTime = null;
        GetElfDataRsp? companions=null; GetGrandKeyRsp? grandKeys=null;
        foreach (var response in account.GetProperty("Responses").EnumerateArray())
        {
            var stamp = response.GetProperty("Time").GetDateTimeOffset();
            if (priorTime is { } last && stamp < last) throw new InvalidDataException("Responses are not in capture order.");
            priorTime = stamp;
            int command = response.GetProperty("CommandId").GetInt32();
            if (command is not (11 or 25 or 27 or 2101 or 507)) continue;
            byte[] body = Convert.FromBase64String(response.GetProperty("BodyBase64").GetString() ?? "");
            if (body.Length > 4 * 1024 * 1024) throw new InvalidDataException("Response exceeds import limit.");
            switch (command)
            {
                case 507:
                    var g=GetGrandKeyRsp.Parser.ParseFrom(body);
                    if(g.Retcode!=GetGrandKeyRsp.Types.Retcode.Succ||!g.IsAll||g.KeyList.Select(k=>k.Id).Distinct().Count()!=g.KeyList.Count
                        ||g.KeyList.Any(k=>k.Id is <201 or >209||k.Level is <1 or >10))throw new InvalidDataException("Invalid grand-key snapshot.");
                    grandKeys=g;break;
                case 2101:
                    companions=GetElfDataRsp.Parser.ParseFrom(body);
                    ValidateCompanions(companions);
                    break;
                case 11:
                    var m = GetMainDataRsp.Parser.ParseFrom(body);
                    if (!m.HasRetcode || m.Retcode != GetMainDataRsp.Types.Retcode.Succ) throw new InvalidDataException("Main response is not an explicit success.");
                    if (m.IsAll) { main = m.Clone(); mainAll = true; }
                    else if (mainAll) { if (m.HasLevel) main.Level = m.Level; if (m.HasExp) main.Exp = m.Exp; }
                    break;
                case 25:
                    var a = GetAvatarDataRsp.Parser.ParseFrom(body);
                    if (!a.HasRetcode || a.Retcode != GetAvatarDataRsp.Types.Retcode.Succ) throw new InvalidDataException("Avatar response is not an explicit success.");
                    if (a.IsAll) { avatars.Clear(); avatarAll = true; }
                    if (avatarAll) Merge(a.AvatarList, avatars, x => x.AvatarId, x => x.Clone());
                    break;
                case 27:
                    var e = GetEquipmentDataRsp.Parser.ParseFrom(body);
                    if (!e.HasRetcode || e.Retcode != GetEquipmentDataRsp.Types.Retcode.Succ) throw new InvalidDataException("Equipment response is not an explicit success.");
                    if (e.IsAll) { weapons.Clear(); stigmata.Clear(); equipmentAll = true; }
                    if (equipmentAll)
                    {
                        Merge(e.WeaponList, weapons, x => x.UniqueId, x => x.Clone());
                        Merge(e.StigmataList, stigmata, x => x.UniqueId, x => x.Clone());
                    }
                    break;
            }
        }
        if (!mainAll || !avatarAll || !equipmentAll || avatars.Count is 0 or > 2048 || weapons.Count > 20000 || stigmata.Count > 20000)
            throw new InvalidDataException("Successful main/avatar/equipment full snapshots are required even for partial transport.");
        if (main.Level is < 1 or > 88 || avatars.Values.Any(a => a.Level is < 1 or > 80 || a.Star > 6))
            throw new InvalidDataException("Captured levels or avatar ranks are outside supported limits.");
        if (weapons.Values.Any(w => w.Id == 0 || w.Level == 0) || stigmata.Values.Any(s => s.Id == 0 || s.Level == 0))
            throw new InvalidDataException("Equipment identity or level is missing.");
        foreach (var a in avatars.Values)
        {
            if (a.WeaponUniqueId != 0 && !weapons.ContainsKey(a.WeaponUniqueId)) throw new InvalidDataException($"Avatar {a.AvatarId} references a missing weapon.");
            foreach (uint id in new[] { a.StigmataUniqueId1, a.StigmataUniqueId2, a.StigmataUniqueId3 })
                if (id != 0 && !stigmata.ContainsKey(id)) throw new InvalidDataException($"Avatar {a.AvatarId} references missing stigmata.");
        }
        var warnings = new List<string>();
        if (!complete) warnings.Add("Capture transport is incomplete; only verified full lists and their observed increments are imported.");
        foreach (var a in avatars.Values.Where(a => a.SkillList.Count == 0)) warnings.Add($"Avatar {a.AvatarId} has no captured skill list; no skills were invented.");
        foreach (var a in avatars.Values.Where(a => a.Star == 0)) warnings.Add($"Avatar {a.AvatarId} has captured rank 0; preserved as observed, gameplay availability is not inferred.");
        warnings.Add("Local identity, wallet, consumables, team, guides, campaign and reward receipts are preserved. Other official systems are not imported.");
        return new(sourceUid, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), complete, main,
            new() { Retcode = GetAvatarDataRsp.Types.Retcode.Succ, IsAll = true, AvatarList = { avatars.OrderBy(x => x.Key).Select(x => x.Value) } },
            new() { Retcode = GetEquipmentDataRsp.Types.Retcode.Succ, IsAll = true,
                WeaponList = { weapons.OrderBy(x => x.Key).Select(x => x.Value) }, StigmataList = { stigmata.OrderBy(x => x.Key).Select(x => x.Value) } }, warnings.ToArray(),companions,grandKeys);
    }
    private static void Merge<T>(IEnumerable<T> items, Dictionary<uint,T> target, Func<T,uint> id, Func<T,T> clone)
    {
        var seen = new HashSet<uint>();
        foreach (var item in items)
        {
            uint key = id(item);
            if (key == 0 || !seen.Add(key)) throw new InvalidDataException("Missing or duplicate identity in one response.");
            // Each captured entity is a replacement snapshot, not additive inventory.
            // Clone retains unknown protobuf fields within the entity.
            target[key] = clone(item);
        }
    }
    public static AccountImportResult Apply(LobbyStore store, uint uid, AccountCopyPlan plan, bool syncLevel = true) => store.Campaign<AccountImportResult>(uid, tx =>
    {
        bool same = tx.Inventory?.SourceSha256 == plan.SourceSha256;
        if (tx.Inventory is not null && !same) throw new InvalidDataException("A different inventory is already imported. Restore a backup before replacing it.");
        var owned = plan.Avatars.AvatarList.Select(a => a.AvatarId).ToHashSet();
        if (!owned.Contains(tx.Lobby.AvatarId) || tx.Campaign.Team.Any(id => id != 0 && !owned.Contains(id)))
            throw new InvalidDataException("Captured roster does not contain the existing local captain/team.");
        if (tx.Campaign.Run is not null) throw new InvalidDataException("Finish the active local stage before importing.");
        if (!same)
        {
            var primary = plan.Avatars.AvatarList.Single(a => a.AvatarId == tx.Lobby.AvatarId);
            var weapon = plan.Equipment.WeaponList.SingleOrDefault(w => w.UniqueId == primary.WeaponUniqueId);
            tx.Inventory = new(plan.Avatars.ToByteArray(), plan.Equipment.ToByteArray(), plan.SourceSha256, plan.SourceUid, DateTimeOffset.UtcNow.ToString("O"));
            tx.Lobby = tx.Lobby with { Level = syncLevel ? plan.Main.Level : tx.Lobby.Level, Exp = syncLevel ? plan.Main.Exp : tx.Lobby.Exp,
                AvatarLevel = primary.Level, AvatarExp = primary.Exp, WeaponId = weapon?.Id ?? 0, DressId = primary.DressId };
        }
        ImportCompanions(tx,plan);
        ImportSystems(tx,plan);
        return new(uid, plan.SourceUid, plan.SourceSha256, plan.Avatars.AvatarList.Count, plan.Equipment.WeaponList.Count,
            plan.Equipment.StigmataList.Count, tx.Lobby.Level, same, true, plan.Warnings);
    });
    private static void ValidateCompanions(GetElfDataRsp data)
    {
        if(!data.HasRetcode||data.Retcode!=GetElfDataRsp.Types.Retcode.Succ||data.ElfList.Count>100||data.ElfList.Select(x=>x.ElfId).Distinct().Count()!=data.ElfList.Count)
            throw new InvalidDataException("Invalid companion snapshot.");
        foreach(var elf in data.ElfList)
        {
            var spec=CompanionService.Find(elf.ElfId);
            if(elf.Level<1||elf.Level>spec.MaxLevel||elf.Star<spec.InitialStar||elf.Star>spec.MaxStar||elf.SkillList.Select(x=>x.SkillId).Distinct().Count()!=elf.SkillList.Count)
                throw new InvalidDataException("Invalid companion level, rank or skills.");
            if(elf.SkillList.Any(s=>s.SkillLevel>100||!spec.Skills.Any(x=>x.Id==s.SkillId)))throw new InvalidDataException("Unknown companion skill in capture.");
        }
        foreach(var fragment in data.ElfFragmentList){CompanionService.Find(fragment.ElfId);if(fragment.FragmentNum>999999999)throw new InvalidDataException("Companion fragment limit exceeded.");}
    }
    private static int ImportCompanions(CampaignTransaction tx,AccountCopyPlan plan)
    {
        if(plan.Companions is not {} data || tx.Campaign.CompanionImports.Contains(plan.SourceSha256))return 0;
        ValidateCompanions(data);int added=0;
        foreach(var elf in data.ElfList)
            if(tx.Campaign.Companions.TryAdd(elf.ElfId,new(){Level=elf.Level,Star=elf.Star,Exp=elf.Exp,Skills=elf.SkillList.ToDictionary(x=>x.SkillId,x=>x.SkillLevel),MaskedSkills=elf.SkillList.Where(x=>x.IsMask).Select(x=>x.SkillId).ToHashSet()}))added++;
        foreach(var fragment in data.ElfFragmentList)
            tx.Campaign.Materials.TryAdd(CompanionService.Find(fragment.ElfId).Fragment,fragment.FragmentNum);
        tx.Campaign.CompanionImports.Add(plan.SourceSha256);return added;
    }
    // Enrich only the already-matched imported account. No wallet, roster or progress replacement.
    public static int ApplyCompanions(LobbyStore store,uint uid,AccountCopyPlan plan)=>store.Campaign(uid,tx=>
    {
        if(tx.Inventory?.SourceUid!=plan.SourceUid||tx.Inventory.SourceSha256!=plan.SourceSha256)throw new InvalidDataException("Companion capture does not match this local inventory.");
        return ImportCompanions(tx,plan);
    });
    public static int ApplySystems(LobbyStore store,uint uid,AccountCopyPlan plan)=>store.Campaign(uid,tx=>
    {
        if(tx.Inventory?.SourceUid!=plan.SourceUid||tx.Inventory.SourceSha256!=plan.SourceSha256)
            throw new InvalidDataException("System capture does not match the imported local account.");
        return ImportSystems(tx,plan);
    });
    private static int ImportSystems(CampaignTransaction tx,AccountCopyPlan plan)
    {
        if(plan.GrandKeys is not {} keys)return 0;
        int count=0;
        foreach(var key in keys.KeyList)
            if(tx.Campaign.Systems.GrandKeys.TryAdd(key.Id,key.ToByteArray()))count++;
        return count;
    }

}
