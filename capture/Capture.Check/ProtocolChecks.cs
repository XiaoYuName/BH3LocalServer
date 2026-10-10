using System.Buffers.Binary;
using BH3.Protocol;
using BH3.Protocol.Messages;
using Google.Protobuf;
using Bh3Capture;

internal static class ProtocolChecks
{
    internal static string Run(string sandbox, Action<bool, string> assert)
    {
        string Write(string name, IEnumerable<(bool Server, byte[] Data)> packets, bool little = true, bool ipv6 = false)
        { var path = Path.Combine(sandbox, name + ".pcapng"); Fixtures.Write(path, packets, little, ipv6); return path; }
        var all = Fixtures.Good();
        var good = Write("good", all);
        var report = CaptureInspector.Inspect([good]);
        assert(report.Passed && report.Connections.Count == 1 && report.Accounts.Count == 1, "BH3 64位KCP及账号核心快照");
        var account = report.Accounts[0];
        assert(account.SourceUid == 10001 && account.AvatarCount == 1 && account.WeaponCount == 1 && account.StigmataCount == 1, "保留账号角色/武器/圣痕，不伪造条目");
        var response = account.Responses.Single(p => p.CommandId == 25);
        var avatar = GetAvatarDataRsp.Parser.ParseFrom(Convert.FromBase64String(response.BodyBase64)).AvatarList.Single();
        assert(avatar.Level == 80 && avatar.WeaponUniqueId == 71 && avatar.SkillList.Single().SkillId == 10101, "女武神等级、技能和装备关联逐字保留");
        assert(account.Responses.All(p => p.CommandId is not (4 or 5 or 6 or 7)), "账号导出排除登录令牌及认证消息");
        assert(report.FirstPacketUtc == DateTimeOffset.FromUnixTimeSeconds(1700000000), "pcapng时间戳");
        assert(CaptureInspector.Inspect([Write("little", Fixtures.Good(false))]).Passed, "标准32位KCP");
        assert(CaptureInspector.Inspect([Write("big-pcap", all, false)]).Passed, "大端pcapng");
        assert(CaptureInspector.Inspect([Write("ipv6", all, ipv6: true)]).Passed, "IPv6 UDP");
        assert(CaptureInspector.Inspect([Write("reorder", all.Take(2).Concat(all.Skip(2).Reverse()))]).Passed, "KCP乱序重组");
        assert(CaptureInspector.Inspect([Write("duplicates", all.SelectMany(p => new[] { p, p }))]).Passed, "多组件重复捕获和KCP重传去重");
        var fullAvatar = Fixtures.Game(25, new GetAvatarDataRsp { Retcode = 0, IsAll = true,
            AvatarList = { new Avatar { AvatarId = 101, Level = 80, DressList = { Enumerable.Range(1, 1000).Select(x => (uint)x) } } } });
        var fragmented = all.Take(4).Concat(new[] { (true, Fixtures.Kcp(1, fullAvatar[..800], fragment: 2)),
            (true, Fixtures.Kcp(2, fullAvatar[800..1600], fragment: 1)), (true, Fixtures.Kcp(3, fullAvatar[1600..])),
            (true, Fixtures.Kcp(4, Fixtures.Equipment())) }).ToArray();
        assert(CaptureInspector.Inspect([Write("fragments-a", fragmented.Take(5)), Write("fragments-b", fragmented.Skip(5))]).Passed, "跨分卷多片消息完整重组");
        var gap = CaptureInspector.Inspect([Write("gap", fragmented.Where((_, i) => i != 5))]);
        assert(!gap.Passed && gap.Connections[0].Issues.Any(s => s.Contains("缺口")), "缺失KCP分片不能伪装完整账号");
        var conflict = all.Concat(new[] { (true, Fixtures.Kcp(0, Fixtures.Game(11, new GetMainDataRsp { Retcode = 0, IsAll = true, Level = 5 }))) });
        assert(!CaptureInspector.Inspect([Write("conflict", conflict)]).Passed, "冲突重传拒绝导出该方向");
        var partial = CaptureInspector.Inspect([Write("partial", all.Where((_, i) => i != 5))]);
        assert(!partial.Passed && partial.Accounts.Single().Issues.Any(s => s.Contains("27")), "缺少装备全量响应必须标记缺项");
        var late = CaptureInspector.Inspect([Write("late", all.Skip(2))]);
        assert(!late.Passed && late.Accounts.Count == 1, "中途开始允许保留数据但不标完整");
        var encrypted = new[] { all[0], all[1], (true, Fixtures.Kcp(0, new byte[] { 9, 8, 7, 6 })) };
        var unknown = CaptureInspector.Inspect([Write("unknown", encrypted)]);
        assert(!unknown.Passed && unknown.Accounts.Count == 0 && unknown.Connections[0].UnrecognizedMessages == 1, "未识别官方载荷不伪报账号复制成功");
        var bad = Path.Combine(sandbox, "truncated.pcapng"); File.WriteAllBytes(bad, File.ReadAllBytes(good)[..^1]);
        assert(!CaptureInspector.Inspect([bad]).Passed, "截断pcapng明确失败");
        assert(!CaptureInspector.Inspect([good], 8).Passed, "资源上限明确失败");
        assert(!CaptureInspector.Inspect([]).Passed, "无输入不能成功");
        var separate = all.Concat(Fixtures.Good(uid: 10002, conv: 5678));
        var separated = CaptureInspector.Inspect([Write("two-accounts", separate)]);
        assert(separated.Passed && separated.Accounts.Count == 2 && separated.Accounts.Select(a => a.SourceUid).Distinct().Count() == 2, "重连和不同账号数据分开保存");
        var saved = Path.Combine(sandbox, "account-export"); AccountExport.Write(saved, report);
        var json = File.ReadAllText(Path.Combine(saved, "account-copy.json"));
        assert(json.Contains("BH3Capture.AccountCopy") && !json.Contains("secret-login-token"), "实际写出账号副本且不含登录令牌");
        assert(File.ReadAllText(Path.Combine(saved, "account-summary.txt")).Contains("圣痕 1"), "账号摘要包含可复核数量");
        var fromJson = System.Text.Json.JsonSerializer.Deserialize<CaptureReport>(System.Text.Json.JsonSerializer.Serialize(report))!;
        assert(fromJson.Accounts.Count == 0 && fromJson.AccountCount == 1, "通用检查记录只含数量，不复制账号响应");
        return good;
    }
}

internal static class Fixtures
{
    public static byte[] Game(ushort id, IMessage body, uint uid = 10001)
    {
        var prefix = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(prefix, GamePacketCodec.Head);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(12), uid);
        return GamePacketCodec.Encode(new(prefix, id, [], body.ToByteArray()));
    }
    public static byte[] Equipment(uint uid = 10001) => Game(27, new GetEquipmentDataRsp { Retcode = 0, IsAll = true,
        WeaponList = { new Weapon { UniqueId = 71, Id = 20001, Level = 50 } },
        StigmataList = { new Stigmata { UniqueId = 81, Id = 30001, Level = 50 } } }, uid);
    public static (bool Server, byte[] Data)[] Good(bool wide = true, uint uid = 10001, uint conv = 1234)
    {
        var request = new byte[20]; BinaryPrimitives.WriteUInt32BigEndian(request, 255);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(12), 91); BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(16), uint.MaxValue);
        var reply = new byte[20]; BinaryPrimitives.WriteUInt32BigEndian(reply, 325);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), conv); BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), 91);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(16), 0x14514545);
        return [(false, request), (true, reply),
            (false, Kcp(0, Game(4, new GetPlayerTokenReq { Token = "secret-login-token" }, uid), wide, conv)),
            (true, Kcp(0, Game(11, new GetMainDataRsp { Retcode = 0, IsAll = true, Level = 88 }, uid), wide, conv)),
            (true, Kcp(1, Game(25, new GetAvatarDataRsp { Retcode = 0, IsAll = true,
                AvatarList = { new Avatar { AvatarId = 101, Level = 80, WeaponUniqueId = 71, SkillList = { new AvatarSkill { SkillId = 10101 } } } } }, uid), wide, conv)),
            (true, Kcp(2, Equipment(uid), wide, conv))];
    }
    public static byte[] Kcp(uint seq, byte[] data, bool wide = true, uint conv = 1234, byte fragment = 0)
    {
        int n = wide ? 8 : 4; var result = new byte[n + 20 + data.Length];
        if (wide) BinaryPrimitives.WriteUInt64BigEndian(result, conv); else BinaryPrimitives.WriteUInt32LittleEndian(result, conv);
        result[n] = 81; result[n + 1] = fragment; BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(n + 2), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(n + 8), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(n + 16), (uint)data.Length); data.CopyTo(result, n + 20); return result;
    }
    public static void Write(string path, IEnumerable<(bool Server, byte[] Data)> packets, bool little = true, bool ipv6 = false)
    {
        using var stream = File.Create(path);
        void U16(Span<byte> span, ushort x) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(span, x); else BinaryPrimitives.WriteUInt16BigEndian(span, x); }
        void U32(Span<byte> span, uint x) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(span, x); else BinaryPrimitives.WriteUInt32BigEndian(span, x); }
        void Block(uint type, byte[] body)
        { var block = new byte[body.Length + 12]; U32(block, type); U32(block.AsSpan(4), (uint)block.Length); body.CopyTo(block, 8); U32(block.AsSpan(block.Length - 4), (uint)block.Length); stream.Write(block); }
        var shb = new byte[16]; U32(shb, 0x1a2b3c4d); U16(shb.AsSpan(4), 1); shb.AsSpan(8).Fill(255); Block(0x0a0d0d0a, shb);
        var idb = new byte[8]; U16(idb, 101); U32(idb.AsSpan(4), 65535); Block(1, idb);
        foreach (var packet in packets)
        {
            int ip = ipv6 ? 40 : 20;
            var raw = new byte[ip + 8 + packet.Data.Length]; raw[0] = ipv6 ? (byte)0x60 : (byte)0x45;
            if (ipv6)
            {
                BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(4), (ushort)(raw.Length - 40)); raw[6] = 17;
                raw[8] = 0x20; raw[9] = 1; raw[23] = packet.Server ? (byte)2 : (byte)1;
                raw[24] = 0x20; raw[25] = 1; raw[39] = packet.Server ? (byte)1 : (byte)2;
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(2), (ushort)raw.Length); raw[9] = 17;
                raw[12] = 10; raw[15] = packet.Server ? (byte)2 : (byte)1; raw[16] = 10; raw[19] = packet.Server ? (byte)1 : (byte)2;
            }
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(ip), packet.Server ? (ushort)16100 : (ushort)55000);
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(ip + 2), packet.Server ? (ushort)55000 : (ushort)16100);
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(ip + 4), (ushort)(8 + packet.Data.Length)); packet.Data.CopyTo(raw, ip + 8);
            var body = new byte[20 + ((raw.Length + 3) & ~3)]; const ulong stamp = 1700000000000000;
            U32(body.AsSpan(4), (uint)(stamp >> 32)); U32(body.AsSpan(8), unchecked((uint)stamp));
            U32(body.AsSpan(12), (uint)raw.Length); U32(body.AsSpan(16), (uint)raw.Length); raw.CopyTo(body, 20); Block(6, body);
        }
    }
}
