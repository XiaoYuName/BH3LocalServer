namespace BH3.Server.Transport;

// Extension contract for a verified BH3 KCP implementation. There is deliberately
// no pass-through implementation: a UDP datagram is not a reassembled game packet.
public interface IReliableChannel : IDisposable
{
    void Input(ReadOnlySpan<byte> datagram, uint monotonicMilliseconds);
    void Update(uint monotonicMilliseconds);
    bool TryReceive(out byte[] message);
    void Send(ReadOnlySpan<byte> message);
}
