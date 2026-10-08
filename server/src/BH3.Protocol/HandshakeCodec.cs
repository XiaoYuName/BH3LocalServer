using System.Buffers.Binary;

namespace BH3.Protocol;

// Compatibility with the retained local prototype, not a confirmed 9.1 KCP dialect.
public readonly record struct ConnectRequest(uint ClientNonce);
public static class HandshakeCodec
{
    public const int PacketSize = 20;
    public static bool TryReadConnect(ReadOnlySpan<byte> data, out ConnectRequest request)
    {
        request = default;
        if (data.Length != PacketSize || BinaryPrimitives.ReadUInt32BigEndian(data) != 0xff) return false;
        request = new(BinaryPrimitives.ReadUInt32BigEndian(data[12..]));
        return true;
    }

    public static byte[] Accept(uint conversation, ConnectRequest request)
    {
        if (conversation == 0) throw new ArgumentOutOfRangeException(nameof(conversation));
        var packet = new byte[PacketSize];
        BinaryPrimitives.WriteUInt32BigEndian(packet, 0x145);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), conversation);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), request.ClientNonce);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), 0x14514545);
        return packet;
    }
}
