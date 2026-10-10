using System.Buffers.Binary;
using System.Text.Json;
using BH3.Game.Campaign;
using BH3.Game.Lobby;
using BH3.Game.Messaging;
using BH3.Game.Players;
using BH3.Game.Sessions;
using BH3.Persistence;
using BH3.Protocol;
using BH3.Protocol.Messages;
using Google.Protobuf;

if (args.Length != 3) throw new ArgumentException("Usage: AccountImportProbe <isolated-db-copy> <account-copy.json> <local-uid>");
string db = Path.GetFullPath(args[0]); uint uid = uint.Parse(args[2]);
var factory = new SqliteConnectionFactory(db); var store = new LobbyStore(factory);
// This probe mutates a temporary copy to exercise stage settlement, never the
// passed database. Its final hash/content can still be compared by the caller.
string work = Path.Combine(Path.GetTempPath(), "bh3-import-probe-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
string copy = Path.Combine(work,"probe.db");
using (var c = factory.Open(readOnly:true)) using (var d = new SqliteConnectionFactory(copy).Open()) c.BackupDatabase(d);
factory = new(copy); store = new(factory); SchemaMigrator.Initialize(factory);
var plan = AccountCopyImport.Read(args[1], true);
var results = new List<string>();
void Check(string name, bool ok) { if (!ok) throw new InvalidDataException(name); results.Add(name); }
GamePacket Query(ushort command, IMessage input)
{
    var session = new GameSession(1, "offline-probe",1); session.Authenticate(uid);
    byte[] prefix = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(prefix,GamePacketCodec.Head);
    var dispatcher = new GameDispatcher(new LobbyHandlers(new LobbyStore(factory),new SqlitePlayerStore(factory),new byte[32]).Create());
    return dispatcher.Dispatch(session,new(prefix,command,[],input.ToByteArray())).Replies.Single();
}
var avatars = GetAvatarDataRsp.Parser.ParseFrom(Query(24,new GetAvatarDataReq { AvatarIdList = { 0 } }).Body);
var equipment = GetEquipmentDataRsp.Parser.ParseFrom(Query(26,new GetEquipmentDataReq {
    WeaponUniqueIdList = { 0 }, StigmataUniqueIdList = { 0 }, MaterialIdList = { 0 }, MechaUniqueIdList = { 0 } }).Body);
Check("Full/zero-ID roster equals captured entity bytes",avatars.IsAll && avatars.AvatarList.Count==plan.Avatars.AvatarList.Count &&
    avatars.AvatarList.Zip(plan.Avatars.AvatarList).All(pair=>pair.First.ToByteArray().SequenceEqual(pair.Second.ToByteArray())));
Check("Full equipment equals captured weapon and stigmata bytes",equipment.IsAll && equipment.WeaponList.Count==plan.Equipment.WeaponList.Count &&
    equipment.StigmataList.Count==plan.Equipment.StigmataList.Count && equipment.WeaponList.Zip(plan.Equipment.WeaponList).All(p=>p.First.ToByteArray().SequenceEqual(p.Second.ToByteArray())) &&
    equipment.StigmataList.Zip(plan.Equipment.StigmataList).All(p=>p.First.ToByteArray().SequenceEqual(p.Second.ToByteArray())));
uint target = avatars.AvatarList.Last(a=>a.SkillList.Count>0).AvatarId;
var selected=GetAvatarDataRsp.Parser.ParseFrom(Query(24,new GetAvatarDataReq { AvatarIdList={target} }).Body);
Check("Selected roster query",!selected.IsAll && selected.AvatarList.Single().AvatarId==target);
var wallet=store.Read(uid); var material=store.Campaign(uid,tx=>tx.Campaign.Materials);
Check("Local materials preserved in equipment reply",equipment.MaterialList.Count==material.Count && equipment.MaterialList.All(m=>material[m.Id]==m.Num));
Check("Every nonzero equipment reference resolves",avatars.AvatarList.All(a=>(a.WeaponUniqueId==0 || equipment.WeaponList.Any(w=>w.UniqueId==a.WeaponUniqueId)) &&
    new[]{a.StigmataUniqueId1,a.StigmataUniqueId2,a.StigmataUniqueId3}.All(id=>id==0 || equipment.StigmataList.Any(s=>s.UniqueId==id))));
var game=new CampaignService(store,TimeProvider.System);
uint[] team=avatars.AvatarList.Where(a=>a.SkillList.Count>0 && a.AvatarId!=wallet.AvatarId).Take(2).Select(a=>a.AvatarId).Prepend(wallet.AvatarId).ToArray();
Check("Three owned members available",team.Length==3);
game.SetTeam(uid,new UpdateAvatarTeamNotify {Team=new AvatarTeam {StageType=1,AvatarIdList={team}}});
Check("Team persists across service restart",new CampaignService(new LobbyStore(factory),TimeProvider.System).Team(uid).SequenceEqual(team));
var begin=game.Begin(uid,new StageBeginReq { StageId=10101,AvatarIdList={team} });
Check("Imported three-member team starts a local stage",begin.Retcode==StageBeginRsp.Types.Retcode.Succ);
var endInput=new StageEndReq { Body=new StageEndReqBody { StageId=10101 }.ToByteString(),Sign="import-probe" };
var end=game.End(uid,endInput); Check("Omitted WIN settles imported team",end.Retcode==StageEndRsp.Types.Retcode.Succ);
byte[] rosterAfter=store.Inventory(uid)!.Avatars; string walletAfter=JsonSerializer.Serialize(store.Read(uid));
Check("Settlement retry returns same receipt",game.End(uid,endInput).ToByteArray().SequenceEqual(end.ToByteArray()));
Check("Retry does not duplicate rewards or avatar XP",walletAfter==JsonSerializer.Serialize(store.Read(uid)) && rosterAfter.SequenceEqual(store.Inventory(uid)!.Avatars));
var after=GetAvatarDataRsp.Parser.ParseFrom(rosterAfter);
Check("Captured max-level avatars never downgraded",avatars.AvatarList.Where(a=>a.Level>=80).All(a=>after.AvatarList.Single(v=>v.AvatarId==a.AvatarId).Level==a.Level));
Check("Foreign avatar rejected",game.Begin(uid,new StageBeginReq { StageId=10101,AvatarIdList={uint.MaxValue} }).Retcode==StageBeginRsp.Types.Retcode.AvatarError);
Console.WriteLine(JsonSerializer.Serialize(new {passed=true, input="captured-account-import", avatars=avatars.AvatarList.Count,weapons=equipment.WeaponList.Count,
    stigmata=equipment.StigmataList.Count,level=wallet.Level,checks=results,isolated_database=copy},new JsonSerializerOptions {WriteIndented=true}));
