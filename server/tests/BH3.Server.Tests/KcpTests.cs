using System.Buffers.Binary;
using BH3.Server.Transport;

namespace BH3.Server.Tests;

public sealed class KcpTests
{
    [Theory]
    [InlineData(KcpWireFormat.Standard32)]
    [InlineData(KcpWireFormat.Bh3BigEndian64)]
    public void FragmentedMessagesSurviveLossReorderDuplicatesAndClockWrap(KcpWireFormat format)
    {
        var ab = new List<byte[]>(); var ba = new List<byte[]>();
        using var a = new KcpReliableChannel(7, format, ab.Add);
        using var b = new KcpReliableChannel(7, format, ba.Add);
        var random = new Random(91); byte[] payload = new byte[90000]; random.NextBytes(payload);
        a.Send(payload); var received = new List<byte[]>(); var echoed = new List<byte[]>(); int serial = 0;
        for (uint elapsed = 0; elapsed < 20000; elapsed += 10)
        {
            uint now = unchecked(uint.MaxValue - 500 + elapsed); a.Update(now); b.Update(now);
            void Deliver(List<byte[]> queue, KcpReliableChannel target)
            {
                var packets = queue.AsEnumerable().Reverse().ToArray(); queue.Clear();
                foreach (var packet in packets)
                {
                    serial++; if (serial % 7 == 0) continue;
                    target.Input(packet, now); if (serial % 11 == 0) target.Input(packet, now);
                }
            }
            Deliver(ab, b); Deliver(ba, a);
            while (b.TryReceive(out var message)) { received.Add(message); b.Send(message); }
            while (a.TryReceive(out var message)) echoed.Add(message);
        }
        Assert.Equal(payload, Assert.Single(received)); Assert.Equal(payload, Assert.Single(echoed));
    }
    [Fact]
    public void MalformedCompoundDatagramCannotAcknowledgeQueuedMessage()
    {
        var outgoing = new List<byte[]>(); using var channel = new KcpReliableChannel(1, KcpWireFormat.Standard32, outgoing.Add);
        channel.Send([1, 2, 3]); channel.Update(1000); Assert.Single(outgoing); outgoing.Clear();
        var input = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(input, 1); input[4] = 82;
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(16), 1);
        Assert.Throws<InvalidDataException>(() => channel.Input(input, 1050));
        channel.Update(1300); Assert.Single(outgoing);
        channel.Dispose(); Assert.Throws<ObjectDisposedException>(() => channel.Send([1]));
    }
}
