using System.Buffers.Binary;
using BH3.Protocol;

namespace BH3.Protocol.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void LegacyHandshakeHasKnownByteOrder()
    {
        byte[] request = Convert.FromHexString("000000ff00000000000000000000303900000000");
        Assert.True(HandshakeCodec.TryReadConnect(request, out var connect)); Assert.Equal(12345u, connect.ClientNonce);
        Assert.Equal("0000014500000000040302010000303914514545", Convert.ToHexString(HandshakeCodec.Accept(0x01020304, connect)).ToLowerInvariant());
    }
    [Theory]
    [InlineData(0)] [InlineData(4)] [InlineData(19)] [InlineData(21)] [InlineData(1400)]
    public void ConnectRequiresExactDatagramLength(int length)
    {
        var packet = new byte[length]; if (length >= 4) packet[3] = 255;
        Assert.False(HandshakeCodec.TryReadConnect(packet, out _));
    }
    [Fact]
    public void OtherControlCodesAreNotConnects()
    {
        byte[] packet = Convert.FromHexString("0000019400000000000000000000000000000000");
        Assert.False(HandshakeCodec.TryReadConnect(packet, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => HandshakeCodec.Accept(0, new(1)));
    }
    [Fact]
    public void OpaqueFieldsAndPayloadRoundTrip()
    {
        byte[] packet = Convert.FromHexString("012345670001102000000009000027110102030405060708334400040002000000034455aabbcc89abcdef");
        Assert.True(GamePacketCodec.TryDecode(packet, out var result));
        Assert.Equal((ushort)4, result!.CommandId); Assert.Equal(10001u, result.ClaimedUserId);
        Assert.Equal(new byte[] {0x44,0x55}, result.Metadata); Assert.Equal(new byte[] {0xaa,0xbb,0xcc}, result.Body);
        Assert.Equal(packet, GamePacketCodec.Encode(result));
    }
    [Fact]
    public void TruncatedTrailingAndOverflowFramesAreRejected()
    {
        byte[] packet = Convert.FromHexString("0123456700010000000000000000000000000000000000000000000400000000000089abcdef");
        Assert.True(GamePacketCodec.TryDecode(packet, out _));
        for (int i = 0; i < packet.Length; i++) Assert.False(GamePacketCodec.TryDecode(packet.AsSpan(0, i), out _));
        Assert.False(GamePacketCodec.TryDecode([..packet,0], out _));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(30), uint.MaxValue);
        Assert.False(GamePacketCodec.TryDecode(packet, out _));
    }
    [Fact]
    public void UnknownCommandCanBeDecodedWithoutBeingDeclaredSupported()
    {
        byte[] prefix = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(prefix, GamePacketCodec.Head);
        var encoded = GamePacketCodec.Encode(new(prefix, 65535, [], []));
        Assert.True(GamePacketCodec.TryDecode(encoded, out var packet)); Assert.Equal(ushort.MaxValue, packet!.CommandId);
        encoded[^1] ^= 1; Assert.False(GamePacketCodec.TryDecode(encoded, out _));
    }
}
