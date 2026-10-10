using System.Text;
using System.Text.Json;
using BH3.Protocol;
using BH3.Protocol.Messages;
using Google.Protobuf;

namespace Bh3Capture;

public sealed record AccountResponse(ushort CommandId, string Name, DateTimeOffset? Time, string BodyBase64, JsonElement? Parsed);

public sealed class AccountSnapshot
{
    public int Connection { get; set; }
    public uint SourceUid { get; set; }
    public bool TransportComplete { get; set; }
    public bool CoreSnapshotComplete { get; set; }
    public bool MainDataAll { get; set; }
    public bool AvatarDataAll { get; set; }
    public bool EquipmentDataAll { get; set; }
    public int AvatarCount { get; set; }
    public int WeaponCount { get; set; }
    public int StigmataCount { get; set; }
    public List<string> Issues { get; set; } = [];
    public List<AccountResponse> Responses { get; set; } = [];
    private readonly HashSet<uint> avatars = [], weapons = [], stigmata = [];

    public void Add(GamePacket packet, DateTimeOffset? time)
    {
        var (name, parser) = AccountExport.Commands[packet.CommandId];
        JsonElement? parsed = null;
        try
        {
            var message = parser.ParseFrom(packet.Body);
            parsed = JsonSerializer.Deserialize<JsonElement>(JsonFormatter.Default.Format(message));
            int retcode = Convert.ToInt32(message.Descriptor.FindFieldByName("retcode").Accessor.GetValue(message));
            if (retcode == 0)
            {
                switch (message)
                {
                    case GetMainDataRsp main: MainDataAll |= main.IsAll; break;
                    case GetAvatarDataRsp list:
                        AvatarDataAll |= list.IsAll;
                        foreach (var item in list.AvatarList) avatars.Add(item.AvatarId);
                        break;
                    case GetEquipmentDataRsp list:
                        EquipmentDataAll |= list.IsAll;
                        foreach (var item in list.WeaponList) weapons.Add(item.UniqueId);
                        foreach (var item in list.StigmataList) stigmata.Add(item.UniqueId);
                        break;
                }
            }
            else Issues.Add($"{name}返回非成功状态{retcode}，保留原始响应");
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidOperationException or JsonException or ArgumentException)
        { Issues.Add($"{name}字段解析失败，原始响应已保留：{ex.GetType().Name}"); }
        Responses.Add(new(packet.CommandId, name, time, Convert.ToBase64String(packet.Body), parsed));
    }
    public void Finish()
    {
        AvatarCount = avatars.Count; WeaponCount = weapons.Count; StigmataCount = stigmata.Count;
        if (!MainDataAll) Issues.Add("缺少完整基础数据响应11");
        if (!AvatarDataAll) Issues.Add("缺少女武神全量响应25");
        if (!EquipmentDataAll) Issues.Add("缺少装备全量响应27");
        if (SourceUid == 0) Issues.Add("响应UID为空，待结合样本确认账号归属");
        CoreSnapshotComplete = TransportComplete && SourceUid != 0 && Issues.Count == 0 && MainDataAll && AvatarDataAll && EquipmentDataAll;
    }
}

public static class AccountExport
{
    // Login/token messages and client requests are intentionally outside this gameplay export.
    public static readonly IReadOnlyDictionary<ushort, (string Name, MessageParser Parser)> Commands =
        new Dictionary<ushort, (string, MessageParser)>
        {
            [11] = (nameof(GetMainDataRsp), GetMainDataRsp.Parser),
            [25] = (nameof(GetAvatarDataRsp), GetAvatarDataRsp.Parser),
            [27] = (nameof(GetEquipmentDataRsp), GetEquipmentDataRsp.Parser),
            [48] = (nameof(GetAvatarTeamDataRsp), GetAvatarTeamDataRsp.Parser),
            [507] = (nameof(GetGrandKeyRsp), GetGrandKeyRsp.Parser),
            [602] = (nameof(GetDormDataRsp), GetDormDataRsp.Parser),
            [644] = (nameof(GetAvatarRollDataRsp), GetAvatarRollDataRsp.Parser),
            [2101] = (nameof(GetElfDataRsp), GetElfDataRsp.Parser)
        };
    public static bool IsAccountCommand(ushort command) => Commands.ContainsKey(command);
    public static void Write(string directory, CaptureReport report)
    {
        Directory.CreateDirectory(directory);
        SessionFiles.AtomicJson(Path.Combine(directory, "account-copy.json"), new
        {
            Format = "BH3Capture.AccountCopy", FormatVersion = 1, ClientVersion = "9.1.0", CreatedUtc = DateTimeOffset.UtcNow,
            CoreSnapshotComplete = report.Passed,
            Scope = "实际捕获的游戏数据响应；按连接和UID分开。保留每条原始protobuf与兼容模型JSON，不将增量列表误合并成完整账号。",
            report.Issues, report.Accounts
        });
        var text = new StringBuilder(report.ToText());
        foreach (var account in report.Accounts)
        {
            text.AppendLine($"\n连接 {account.Connection}：女武神 {account.AvatarCount}，武器 {account.WeaponCount}，圣痕 {account.StigmataCount}；核心快照完整：{account.CoreSnapshotComplete}");
            foreach (var issue in account.Issues) text.AppendLine("  " + issue);
        }
        text.AppendLine("\n女武神技能及装备关联保留在响应25中，武器/圣痕等级与词条保留在响应27中。JSON采用已有兼容协议；未知字段仍在BodyBase64中。此副本尚未写入本地服务器存档。");
        File.WriteAllText(Path.Combine(directory, "account-summary.txt"), text.ToString(), new UTF8Encoding(false));
    }
}
