using System.Text.Json;
using BH3.Game.Players;
using BH3.Game.Operations;
using BH3.Persistence;

if(args.Length!=3)throw new ArgumentException("ImportCompanions <new-package-db> <uid> <reviewed-account-copy>");
var plan=AccountCopyImport.Read(args[2],allowPartial:true);
string path=Path.GetFullPath(args[0]);
if(!File.Exists(path))throw new InvalidDataException("An existing migrated database is required.");
using var lease=new FileStream(path+".host.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
var db=new SqliteConnectionFactory(path);
string backup=path+".before-companions-"+Guid.NewGuid().ToString("N")+".bak";
using(var source=db.Open(readOnly:true))using(var destination=new SqliteConnectionFactory(backup).Open()){SchemaMigrator.Check(source);source.BackupDatabase(destination);}
var store=new LobbyStore(db);uint uid=uint.Parse(args[1]);
int added=AccountCopyImport.ApplyCompanions(store,uid,plan);
int replay=AccountCopyImport.ApplyCompanions(store,uid,plan);
var data=new CompanionService(store).Get(uid);
Console.WriteLine(JsonSerializer.Serialize(new {added,replay,ids=data.ElfList.Select(x=>x.ElfId).ToArray(),skills=data.ElfList.Sum(x=>x.SkillList.Count),fragments=data.ElfFragmentList.Count,plan.SourceSha256,backup}));
