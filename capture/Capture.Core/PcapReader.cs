using System.Buffers.Binary;
using System.Net;
namespace Bh3Capture;
public sealed record UdpPacket(DateTimeOffset? Time, string Source, int SourcePort, string Destination, int DestinationPort, byte[] Payload);
public static class PcapReader
{
    private sealed record Interface(int Link, double Resolution, long Offset);
    public static IEnumerable<UdpPacket> ReadPcapng(string file)
    {
        using var input = File.OpenRead(file);
        bool little = true; bool section = false;
        var interfaces = new List<Interface>();
        var header = new byte[12];
        while (input.Position < input.Length)
        {
            input.ReadExactly(header);
            var sectionHeader = header.AsSpan(0, 4).SequenceEqual(new byte[] { 10, 13, 13, 10 });
            if (sectionHeader)
            {
                little = header.AsSpan(8, 4).SequenceEqual(new byte[] { 0x4d, 0x3c, 0x2b, 0x1a });
                if (!little && !header.AsSpan(8, 4).SequenceEqual(new byte[] { 0x1a, 0x2b, 0x3c, 0x4d })) throw new InvalidDataException("无效的pcapng字节序。");
                interfaces.Clear(); section = true;
            }
            if (!section) throw new InvalidDataException("需要pcapng格式和Section Header。");
            uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
            ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
            var length = U32(header.AsSpan(4));
            if (length < 12 || length % 4 != 0 || length > 16 * 1024 * 1024 || length - 12 > input.Length - input.Position) throw new InvalidDataException("pcapng块长度无效或文件被截断。");
            var block = new byte[(int)length];
            header.CopyTo(block, 0);
            input.ReadExactly(block.AsSpan(12));
            if (U32(block.AsSpan(block.Length - 4)) != length) throw new InvalidDataException("pcapng块尾长度不一致。");
            var type = U32(block);
            if (type == 1)
            {
                if (length < 20) throw new InvalidDataException("接口块被截断。");
                var resolution = 1e-6; long offset = 0;
                for (var at = 16; at + 4 <= block.Length - 4;)
                {
                    var code = U16(block.AsSpan(at)); var size = U16(block.AsSpan(at + 2)); at += 4;
                    if (code == 0) break;
                    if (at + size > block.Length - 4) throw new InvalidDataException("接口选项被截断。");
                    if (code == 9 && size == 1) resolution = (block[at] & 128) == 0 ? Math.Pow(10, -block[at]) : Math.Pow(2, -(block[at] & 127));
                    if (code == 14 && size == 8) offset = little ? BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(at)) : BinaryPrimitives.ReadInt64BigEndian(block.AsSpan(at));
                    at += (size + 3) & ~3;
                }
                interfaces.Add(new(U16(block.AsSpan(8)), resolution, offset));
            }
            else if (type == 6)
            {
                if (length < 32) throw new InvalidDataException("数据包块被截断。");
                var id = U32(block.AsSpan(8));
                if (id >= interfaces.Count) throw new InvalidDataException("数据包引用未知接口。");
                var size = U32(block.AsSpan(20)); var original = U32(block.AsSpan(24));
                if (size > length - 32 || size < original) throw new InvalidDataException("数据包被截断；必须采集完整包长。");
                var iface = interfaces[(int)id];
                var stamp = ((ulong)U32(block.AsSpan(12)) << 32) | U32(block.AsSpan(16));
                var ticks = checked((long)(stamp * iface.Resolution * TimeSpan.TicksPerSecond)) + checked(iface.Offset * TimeSpan.TicksPerSecond);
                var time = DateTimeOffset.UnixEpoch.AddTicks(ticks);
                var packet = Parse(block.AsSpan(28, (int)size), iface.Link, time);
                if (packet is not null) yield return packet;
            }
            else if (type is 2 or 3) throw new InvalidDataException("此pcapng含旧式数据包块，未完成检查。");
        }
    }

    public static UdpPacket? Parse(ReadOnlySpan<byte> raw, int link, DateTimeOffset time)
    {
        if (link == 1)
        {
            if (raw.Length < 14) throw new InvalidDataException("以太网头被截断。");
            var ether = BinaryPrimitives.ReadUInt16BigEndian(raw[12..]); var at = 14;
            while (ether is 0x8100 or 0x88a8)
            { if (raw.Length < at + 4) throw new InvalidDataException("VLAN头被截断。"); ether = BinaryPrimitives.ReadUInt16BigEndian(raw[(at + 2)..]); at += 4; }
            if (ether is not (0x0800 or 0x86dd)) return null;
            raw = raw[at..];
        }
        else if (link == 0) { if (raw.Length < 4) throw new InvalidDataException("Loopback头被截断。"); raw = raw[4..]; }
        else if (link is not (101 or 228 or 229)) throw new InvalidDataException("不支持链路类型 " + link);
        if (raw.Length < 1) throw new InvalidDataException("IP头为空。");
        string source, destination; int udpAt, length;
        if (raw[0] >> 4 == 4)
        {
            if (raw.Length < 20) throw new InvalidDataException("IPv4头被截断。");
            udpAt = (raw[0] & 15) * 4;
            length = BinaryPrimitives.ReadUInt16BigEndian(raw[2..]);
            if (udpAt < 20 || length < udpAt || raw.Length < length) throw new InvalidDataException("IPv4长度无效。");
            if (raw[9] != 17) return null;
            if ((BinaryPrimitives.ReadUInt16BigEndian(raw[6..]) & 0x3fff) != 0) throw new InvalidDataException("遇到IP分片，基础检查不支持IP分片重组。");
            source = new IPAddress(raw.Slice(12, 4)).ToString(); destination = new IPAddress(raw.Slice(16, 4)).ToString();
        }
        else if (raw[0] >> 4 == 6)
        {
            if (raw.Length < 40) throw new InvalidDataException("IPv6头被截断。");
            length = 40 + BinaryPrimitives.ReadUInt16BigEndian(raw[4..]); udpAt = 40;
            if (raw.Length < length) throw new InvalidDataException("IPv6包被截断。");
            byte next = raw[6];
            while (next is 0 or 43 or 60)
            { if (udpAt + 2 > length) throw new InvalidDataException("IPv6扩展头被截断。"); var size = (raw[udpAt + 1] + 1) * 8; next = raw[udpAt]; udpAt += size; }
            if (next == 44) throw new InvalidDataException("遇到IPv6分片，基础检查不支持IP分片重组。");
            if (next != 17) return null;
            source = new IPAddress(raw.Slice(8, 16)).ToString(); destination = new IPAddress(raw.Slice(24, 16)).ToString();
        }
        else return null;
        if (length < udpAt + 8) throw new InvalidDataException("UDP头被截断。");
        var udp = raw.Slice(udpAt, length - udpAt);
        int udpLength = BinaryPrimitives.ReadUInt16BigEndian(udp[4..]);
        if (udpLength < 8 || udpLength > udp.Length) throw new InvalidDataException("UDP长度无效或被截断。");
        return new(time, source, BinaryPrimitives.ReadUInt16BigEndian(udp), destination, BinaryPrimitives.ReadUInt16BigEndian(udp[2..]), udp[8..udpLength].ToArray());
    }
}
