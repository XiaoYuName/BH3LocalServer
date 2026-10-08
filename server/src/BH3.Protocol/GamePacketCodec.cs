using System.Buffers.Binary;

namespace BH3.Protocol;

public sealed record GamePacket(byte[] Prefix, ushort CommandId, byte[] Metadata, byte[] Body)
{
    public uint ClaimedUserId => BinaryPrimitives.ReadUInt32BigEndian(Prefix.AsSpan(12));
}

// Called after reliable transport reassembly; never on raw UDP.
public static class GamePacketCodec
{
    public const int PrefixSize = 26;
    public const int HeaderSize = 34;
    public const int MaxPacketSize = 1024 * 1024;
    public const uint Head = 0x01234567, Tail = 0x89abcdef;

    public static GamePacket Reply(GamePacket request, ushort command, byte[] body, uint uid)
    {
        var prefix = new byte[PrefixSize];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, Head);
        BinaryPrimitives.WriteUInt16BigEndian(prefix.AsSpan(4), 1);
        request.Prefix.AsSpan(8, 4).CopyTo(prefix.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(12), uid);
        return new(prefix, command, [], body);
    }

    public static bool TryDecodeBatch(ReadOnlySpan<byte> data, out IReadOnlyList<GamePacket> packets)
    {
        var result = new List<GamePacket>(); packets = result;
        while (!data.IsEmpty)
        {
            if (data.Length < HeaderSize + 4 || result.Count >= 256) return false;
            long length = HeaderSize + 4L + BinaryPrimitives.ReadUInt16BigEndian(data[28..]) + BinaryPrimitives.ReadUInt32BigEndian(data[30..]);
            if (length > data.Length || !TryDecode(data[..(int)length], out var packet)) return false;
            result.Add(packet!); data = data[(int)length..];
        }
        return result.Count > 0;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out GamePacket? packet)
    {
        packet = null;
        if (data.Length < HeaderSize + 4 || data.Length > MaxPacketSize) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != Head) return false;
        int metadata = BinaryPrimitives.ReadUInt16BigEndian(data[28..]);
        uint body = BinaryPrimitives.ReadUInt32BigEndian(data[30..]);
        if ((long)HeaderSize + metadata + body + 4 != data.Length) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data[^4..]) != Tail) return false;
        packet = new(data[..PrefixSize].ToArray(), BinaryPrimitives.ReadUInt16BigEndian(data[26..]),
            data.Slice(HeaderSize, metadata).ToArray(), data.Slice(HeaderSize + metadata, (int)body).ToArray());
        return true;
    }

    public static byte[] Encode(GamePacket packet)
    {
        if (packet.Prefix.Length != PrefixSize || BinaryPrimitives.ReadUInt32BigEndian(packet.Prefix) != Head)
            throw new ArgumentException("Invalid opaque prefix.");
        long size = (long)HeaderSize + packet.Metadata.Length + packet.Body.Length + 4;
        if (packet.Metadata.Length > ushort.MaxValue || size > MaxPacketSize) throw new ArgumentException("Packet exceeds framing limits.");
        var data = new byte[(int)size];
        packet.Prefix.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(26), packet.CommandId);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(28), (ushort)packet.Metadata.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(30), (uint)packet.Body.Length);
        packet.Metadata.CopyTo(data, HeaderSize); packet.Body.CopyTo(data, HeaderSize + packet.Metadata.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(data.Length - 4), Tail);
        return data;
    }
}
