using System.Buffers.Binary;
using System.Text.Json.Serialization;
using BH3.Protocol;

namespace Bh3Capture;

public sealed class ConnectionReport
{
    public int Number { get; set; }
    public int ServerPort { get; set; }
    public bool ClientStartFound { get; set; }
    public bool ServerStartFound { get; set; }
    public long ClientBytes { get; set; }
    public long ServerBytes { get; set; }
    public int GameMessages { get; set; }
    public int UnrecognizedMessages { get; set; }
    public List<string> Issues { get; set; } = [];
}

public sealed class CaptureReport
{
    public bool Passed { get; set; }
    public string Scope { get; set; } = "BH3 UDP/KCP重组及已捕获账号快照；不代表完整官方账号或本地战斗已适配";
    public long PacketCount { get; set; }
    public DateTimeOffset? FirstPacketUtc { get; set; }
    public DateTimeOffset? LastPacketUtc { get; set; }
    public List<string> Issues { get; set; } = [];
    public List<ConnectionReport> Connections { get; set; } = [];
    public int AccountCount { get; set; }
    // Raw account responses belong only in the local account copy, not generic diagnostics.
    [JsonIgnore] public List<AccountSnapshot> Accounts { get; set; } = [];
    public string ToText() => (Passed ? "已捕获核心账号快照" : "采集完成，账号数据仍有缺项") + "\n" + Scope +
        $"\nUDP包：{PacketCount:N0}；游戏连接：{Connections.Count}；账号副本：{AccountCount}\n" + string.Join('\n', Issues) + "\n" +
        string.Join('\n', Connections.Select(c => $"连接 {c.Number}：请求 {c.ClientBytes:N0} 字节，响应 {c.ServerBytes:N0} 字节，" +
            $"已解析 {c.GameMessages} 条，未识别 {c.UnrecognizedMessages} 条；" + string.Join("；", c.Issues)));
}

public static class CaptureInspector
{
    private sealed record Segment(uint Serial, byte Fragment, byte[] Body, DateTimeOffset? Time);
    private sealed class Flow
    {
        public SortedDictionary<long, Segment> Parts { get; } = [];
        public bool Conflict;
        private long? maximum;
        public long Add(Segment segment)
        {
            long sequence = maximum is { } n ? n + unchecked((int)(segment.Serial - (uint)n)) : segment.Serial;
            maximum = Math.Max(maximum ?? sequence, sequence);
            if (Parts.TryGetValue(sequence, out var previous))
            {
                if (previous.Fragment != segment.Fragment || !previous.Body.AsSpan().SequenceEqual(segment.Body)) Conflict = true;
                return 0;
            }
            Parts.Add(sequence, segment); return segment.Body.Length;
        }
        public IEnumerable<(byte[] Body, DateTimeOffset? Time)> Messages(List<string> issues, string direction)
        {
            if (Conflict) { issues.Add(direction + " KCP重传内容冲突，未导出此方向数据"); yield break; }
            long? expectedSequence = null;
            int expectedFragment = -1;
            bool discard = false;
            using var body = new MemoryStream();
            foreach (var (sequence, segment) in Parts)
            {
                if (expectedSequence is null && (uint)sequence != 0) issues.Add(direction + " 缺少会话开始，可能错过登录快照");
                if (expectedSequence is { } expected && sequence != expected)
                {
                    issues.Add(direction + " KCP存在序号缺口");
                    body.SetLength(0); expectedFragment = -1; discard = true;
                }
                expectedSequence = sequence + 1;
                if (discard) { if (segment.Fragment == 0) discard = false; continue; }
                if (expectedFragment < 0) expectedFragment = segment.Fragment;
                if (segment.Fragment != expectedFragment || body.Length + segment.Body.Length > GamePacketCodec.MaxPacketSize)
                {
                    issues.Add(direction + " KCP分片不连续或消息超限"); body.SetLength(0); expectedFragment = -1;
                    discard = segment.Fragment != 0; continue;
                }
                body.Write(segment.Body); expectedFragment--;
                if (segment.Fragment == 0)
                { yield return (body.ToArray(), segment.Time); body.SetLength(0); expectedFragment = -1; }
            }
            if (body.Length > 0) issues.Add(direction + " 最后一条消息分片未收全");
        }
    }
    private sealed class Connection(string source, string destination, ulong conversation, bool wide)
    {
        public string Source = source, Destination = destination;
        public ulong Conversation = conversation;
        public bool Wide = wide;
        public Flow[] Flows { get; } = [new(), new()];
        public string? Server;
        public bool RequestFound, ResponseFound;
        public List<string> Issues { get; } = [];
    }
    private sealed record Handshake(string Client, string Server, uint Nonce, uint Conversation, bool Request);

    public static CaptureReport Inspect(IEnumerable<string> files, long memoryLimit = 256L * 1024 * 1024)
    {
        var report = new CaptureReport();
        var handshakes = new Dictionary<string, Handshake>();
        var requests = new Dictionary<string, uint>();
        var flows = new Dictionary<string, Connection>();
        long retained = 0; int fileCount = 0;
        try
        {
            foreach (var file in files)
            {
                fileCount++;
                foreach (var packet in PcapReader.ReadPcapng(file))
                {
                    report.PacketCount++;
                    if (packet.Time is { } time)
                    { report.FirstPacketUtc = report.FirstPacketUtc is { } first && first < time ? first : time; report.LastPacketUtc = report.LastPacketUtc is { } last && last > time ? last : time; }
                    if (report.PacketCount > 5_000_000) throw new InvalidDataException("包数达到检查上限，原始分卷仍已保留");
                    string src = packet.Source + ":" + packet.SourcePort, dst = packet.Destination + ":" + packet.DestinationPort;
                    bool forward = string.CompareOrdinal(src, dst) < 0;
                    string tuple = forward ? src + "|" + dst : dst + "|" + src;
                    var data = packet.Payload.AsSpan();
                    if (data.Length == 20 && BinaryPrimitives.ReadUInt32BigEndian(data) == 255 && BinaryPrimitives.ReadUInt32BigEndian(data[16..]) == uint.MaxValue)
                    { requests[src + "|" + dst] = BinaryPrimitives.ReadUInt32BigEndian(data[12..]); continue; }
                    if (data.Length == 20 && BinaryPrimitives.ReadUInt32BigEndian(data) == 325 && BinaryPrimitives.ReadUInt32BigEndian(data[16..]) == 0x14514545)
                    {
                        uint nonce = BinaryPrimitives.ReadUInt32BigEndian(data[12..]), conv = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
                        handshakes[tuple + "|" + conv] = new(dst, src, nonce, conv, requests.TryGetValue(dst + "|" + src, out var n) && n == nonce);
                        continue;
                    }
                    if (!TrySegments(data, packet.Time, out var conversation, out var wide, out var segments)) continue;
                    string key = tuple + "|" + conversation + "|" + wide;
                    if (!flows.TryGetValue(key, out var connection))
                    {
                        if (flows.Count >= 4096) throw new InvalidDataException("连接数量达到检查上限");
                        flows.Add(key, connection = new(forward ? src : dst, forward ? dst : src, conversation, wide));
                    }
                    if (handshakes.TryGetValue(tuple + "|" + conversation, out var handshake))
                    { connection.Server = handshake.Server; connection.RequestFound = handshake.Request; connection.ResponseFound = true; }
                    foreach (var segment in segments) retained += connection.Flows[forward ? 0 : 1].Add(segment);
                    if (retained > memoryLimit) throw new InvalidDataException("KCP重组达到内存上限，原始分卷仍已保留");
                }
            }
            if (fileCount == 0) report.Issues.Add("没有pcapng分卷");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        { report.Issues.Add("读取未完成：" + ex.Message); }
        foreach (var flow in flows.Values)
        {
            var info = new ConnectionReport { Number = report.Connections.Count + 1,
                ClientStartFound = flow.RequestFound, ServerStartFound = flow.ResponseFound };
            var packets = new List<(GamePacket Packet, DateTimeOffset? Time, int Direction)>();
            for (int direction = 0; direction < 2; direction++)
            {
                foreach (var message in flow.Flows[direction].Messages(info.Issues, direction == 0 ? "方向A" : "方向B"))
                {
                    if (GamePacketCodec.TryDecodeBatch(message.Body, out var decoded))
                        packets.AddRange(decoded.Select(p => (p, message.Time, direction)));
                    else info.UnrecognizedMessages++;
                }
            }
            // Only report a BH3 flow after a handshake or valid game frame was observed.
            if (!flow.ResponseFound && packets.Count == 0) continue;
            int serverDirection = flow.Server == flow.Source ? 0 : flow.Server == flow.Destination ? 1 : -1;
            if (serverDirection < 0)
            {
                var candidates = packets.Where(p => AccountExport.IsAccountCommand(p.Packet.CommandId)).Select(p => p.Direction).Distinct().ToArray();
                if (candidates.Length == 1) serverDirection = candidates[0];
            }
            string server = serverDirection == 0 ? flow.Source : flow.Destination;
            info.ServerPort = int.Parse(server[(server.LastIndexOf(':') + 1)..]);
            info.GameMessages = packets.Count;
            info.ServerBytes = serverDirection < 0 ? 0 : flow.Flows[serverDirection].Parts.Values.Sum(p => (long)p.Body.Length);
            info.ClientBytes = serverDirection < 0 ? 0 : flow.Flows[1 - serverDirection].Parts.Values.Sum(p => (long)p.Body.Length);
            if (!info.ClientStartFound || !info.ServerStartFound) info.Issues.Add("握手未收全；请先开始抓包再登录");
            if (info.UnrecognizedMessages > 0) info.Issues.Add("存在未识别的游戏载荷，可能需适配官方封装；已保留原始分卷");
            report.Connections.Add(info);
            foreach (var group in packets.Where(p => p.Direction == serverDirection && AccountExport.IsAccountCommand(p.Packet.CommandId)).GroupBy(p => p.Packet.ClaimedUserId))
            {
                var account = new AccountSnapshot { Connection = info.Number, SourceUid = group.Key };
                foreach (var packet in group) account.Add(packet.Packet, packet.Time);
                account.TransportComplete = info.Issues.Count == 0 && report.Issues.Count == 0;
                account.Finish(); report.Accounts.Add(account);
            }
        }
        if (report.Connections.Count == 0) report.Issues.Add("未识别到BH3游戏连接；原始UDP已保留供后续分析");
        if (report.Accounts.Count == 0) report.Issues.Add("未提取到账号响应，不能把抓包成功视为账号复制成功");
        report.AccountCount = report.Accounts.Count;
        report.Passed = report.Issues.Count == 0 && report.Connections.All(c => c.Issues.Count == 0) && report.Accounts.Count > 0 && report.Accounts.All(a => a.CoreSnapshotComplete);
        return report;
    }

    private static bool TrySegments(ReadOnlySpan<byte> data, DateTimeOffset? time, out ulong conversation, out bool wide, out List<Segment> segments)
    {
        conversation = 0; wide = false; segments = [];
        // Validate the complete datagram before retaining any segment.
        foreach (int convSize in new[] { 8, 4 })
        {
            int header = convSize + 20;
            if (data.Length < header) continue;
            var candidate = convSize == 8 ? BinaryPrimitives.ReadUInt64BigEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
            var parts = new List<Segment>(); int offset = 0;
            while (offset < data.Length)
            {
                var part = data[offset..];
                if (part.Length < header) break;
                ulong conv = convSize == 8 ? BinaryPrimitives.ReadUInt64BigEndian(part) : BinaryPrimitives.ReadUInt32LittleEndian(part);
                var h = part[convSize..]; uint length = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
                if (conv != candidate || h[0] is < 81 or > 84 || length > part.Length - header || (h[0] != 81 && length != 0)) break;
                if (h[0] == 81) parts.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(h[8..]), h[1], part.Slice(header, (int)length).ToArray(), time));
                offset += header + (int)length;
            }
            if (offset != data.Length) continue;
            conversation = candidate; wide = convSize == 8; segments = parts; return true;
        }
        return false;
    }
}
