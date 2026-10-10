using Google.Protobuf;
using Google.Protobuf.Reflection;
var set = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(args[0]));
var target = set.File.Single();
var source = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(args[1])).File.Single();
foreach(var message in source.MessageType.Where(m=>target.MessageType.All(t=>t.Name!=m.Name))) target.MessageType.Add(message.Clone());
foreach(var item in source.EnumType.Where(e=>target.EnumType.All(t=>t.Name!=e.Name))) target.EnumType.Add(item.Clone());
// Verified against 9.1 MissionDataItem.UpdateFromMission and UnLockByMission.
var status=target.EnumType.Single(x=>x.Name=="MissionStatus");
foreach(var value in status.Value) { if(value.Name=="DOING")value.Number=2; if(value.Name=="FINISH")value.Number=3; }
Console.WriteLine(string.Join(",",status.Value.Select(v=>$"{v.Name}={v.Number}")));
var elfSkill=target.MessageType.Single(x=>x.Name=="ElfSkill");
if(elfSkill.Field.All(f=>f.Number!=3))elfSkill.Field.Add(new FieldDescriptorProto{Name="is_mask",Number=3,Label=FieldDescriptorProto.Types.Label.Optional,Type=FieldDescriptorProto.Types.Type.Bool});
File.WriteAllBytes(args[0],set.ToByteArray());
