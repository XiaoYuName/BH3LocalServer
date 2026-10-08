using BH3.Protocol;

namespace BH3.Protocol.Tests;

public sealed class TicketTests
{
    [Fact]
    public void TicketBindsUidNameExpiryAndSigningKey()
    {
        byte[] key = Enumerable.Range(0, 32).Select(n => (byte)n).ToArray(); var now = DateTimeOffset.FromUnixTimeSeconds(100000);
        string ticket = LocalTicket.Issue(key, 10001, "舰长", now);
        Assert.True(LocalTicket.TryValidate(key, ticket, now, out var identity)); Assert.Equal(10001u, identity!.Uid); Assert.Equal("舰长", identity.Name);
        Assert.False(LocalTicket.TryValidate(new byte[32], ticket, now, out _));
        Assert.False(LocalTicket.TryValidate(key, ticket, now.AddHours(1), out _));
        Assert.False(LocalTicket.TryValidate(key, "x" + ticket, now, out _));
        Assert.False(LocalTicket.TryValidate(key, "not-a-ticket", now, out _));
    }
    [Fact]
    public void CompoundGameFramesAreAllValidatedBeforeDispatch()
    {
        byte[] prefix = new byte[26]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(prefix, GamePacketCodec.Head);
        var packet = new GamePacket(prefix, 1, [], []); byte[] bytes = GamePacketCodec.Encode(packet);
        Assert.True(GamePacketCodec.TryDecodeBatch(bytes.Concat(bytes).ToArray(), out var batch)); Assert.Equal(2, batch.Count);
        Assert.False(GamePacketCodec.TryDecodeBatch(bytes.Concat(new byte[] { 1 }).ToArray(), out _));
    }
}
