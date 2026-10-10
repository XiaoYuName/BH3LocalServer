using System.Text.Json;
using BH3.Game.Players;
using BH3.Game.Operations;
using BH3.Persistence;

if(args.Length!=3)throw new ArgumentException("ImportSystems <new-package-db> <uid> <reviewed-account-copy>");
var plan=AccountCopyImport.Read(args[2],allowPartial:true);
string path=Path.GetFullPath(args[0]);
if(!File.Exists(path))throw new InvalidDataException("An existing migrated database is required.");
using var lease=new FileStream(path+".host.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
var db=new SqliteConnectionFactory(path);
string backup=path+".before-systems-"+Guid.NewGuid().ToString("N")+".bak";
using(var source=db.Open(readOnly:true))using(var destination=new SqliteConnectionFactory(backup).Open()){SchemaMigrator.Check(source);source.BackupDatabase(destination);}
var store=new LobbyStore(db);uint uid=uint.Parse(args[1]);
int added=AccountCopyImport.ApplySystems(store,uid,plan);
int replay=AccountCopyImport.ApplySystems(store,uid,plan);
var data=new SystemsService(store,TimeProvider.System).GrandKeys(uid,new());
Console.WriteLine(JsonSerializer.Serialize(new {added,replay,ids=data.KeyList.Select(x=>x.Id).ToArray(),plan.SourceSha256,backup}));
