using System.Buffers.Binary;

namespace BH3.Server.Transport;

public enum KcpWireFormat { Standard32, Bh3BigEndian64 }

/// <summary>Bounded message-mode KCP transport. The host serializes Input/Update/Receive.</summary>
public sealed class KcpReliableChannel(uint conversation, KcpWireFormat format, Action<byte[]> output) : IReliableChannel
{
    private sealed class Segment(uint serial, byte fragment, byte[] body)
    {
        public uint Serial = serial, Deadline, Rto = 200;
        public byte Fragment = fragment;
        public byte[] Body = body;
        public int Transmissions;
    }
    private readonly Queue<(byte Fragment, byte[] Body)> waiting = new();
    private readonly Dictionary<uint, Segment> sending = [];
    private readonly Dictionary<uint, (byte Fragment, byte[] Body)> receiving = [];
    private readonly Queue<byte[]> messages = new();
    private readonly List<byte[]> fragments = [];
    private uint sendNext, receiveNext, now, probeAt;
    private int nextFragment = -1, remoteWindow = 256, queuedBytes;
    private bool disposed;
    private int ConvSize => format == KcpWireFormat.Standard32 ? 4 : 8;
    private int HeaderSize => ConvSize + 20;
    private int Mss => 1400 - HeaderSize;
    public const int MaxMessageSize = 128 * (1400 - 28);
    private static int Difference(uint a, uint b) => unchecked((int)(a - b));

    public static bool TryIdentify(ReadOnlySpan<byte> data, uint conversation, out KcpWireFormat format)
    {
        format = KcpWireFormat.Bh3BigEndian64;
        if (data.Length >= 28 && BinaryPrimitives.ReadUInt64BigEndian(data) == conversation && data[8] is >= 81 and <= 84) return true;
        format = KcpWireFormat.Standard32;
        return data.Length >= 24 && BinaryPrimitives.ReadUInt32LittleEndian(data) == conversation && data[4] is >= 81 and <= 84;
    }

    public void Input(ReadOnlySpan<byte> datagram, uint monotonicMilliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Validate the whole compound datagram before advancing acknowledgements or windows.
        for (int offset = 0; offset < datagram.Length;)
        {
            var part = datagram[offset..];
            if (part.Length < HeaderSize || !TryIdentify(part, conversation, out var actual) || actual != format)
                throw new InvalidDataException("Invalid KCP header/conversation.");
            var head = part[ConvSize..]; uint length = BinaryPrimitives.ReadUInt32LittleEndian(head[16..]);
            if (length > Mss || length > part.Length - HeaderSize || (head[0] != 81 && length != 0) || head[1] >= 128)
                throw new InvalidDataException("Invalid KCP segment length/fragment.");
            uint una = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            if (Difference(una, sendNext) > 0) throw new InvalidDataException("KCP acknowledgement exceeds sent sequence.");
            offset += HeaderSize + (int)length;
        }
        now = monotonicMilliseconds;
        while (!datagram.IsEmpty)
        {
            var head = datagram[ConvSize..]; byte command = head[0], fragment = head[1];
            remoteWindow = Math.Min(256, (int)BinaryPrimitives.ReadUInt16LittleEndian(head[2..]));
            uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            uint serial = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
            uint una = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(head[16..]);
            foreach (uint acknowledged in sending.Keys.Where(s => Difference(s, una) < 0).ToArray()) RemoveSent(acknowledged);
            if (command == 82) RemoveSent(serial);
            else if (command == 83) Emit(84, 0, 0, now, []);
            else if (command == 81 && Difference(serial, receiveNext + 256) < 0)
            {
                Emit(82, 0, serial, timestamp, []);
                if (Difference(serial, receiveNext) >= 0 && !receiving.ContainsKey(serial))
                    receiving.Add(serial, (fragment, datagram.Slice(HeaderSize, length).ToArray()));
                while (receiving.Remove(receiveNext, out var next))
                {
                    if (nextFragment < 0) nextFragment = next.Fragment;
                    if (next.Fragment != nextFragment) throw new InvalidDataException("Broken KCP fragment sequence.");
                    fragments.Add(next.Body); receiveNext++; nextFragment--;
                    if (next.Fragment == 0)
                    {
                        if (messages.Count >= 64) throw new InvalidDataException("KCP receive queue exhausted.");
                        messages.Enqueue(fragments.SelectMany(b => b).ToArray()); fragments.Clear(); nextFragment = -1;
                    }
                }
            }
            datagram = datagram[(HeaderSize + length)..];
        }
    }
    private void RemoveSent(uint serial)
    { if (sending.Remove(serial, out var segment)) queuedBytes -= segment.Body.Length; }

    public void Send(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (message.IsEmpty || message.Length > MaxMessageSize || queuedBytes + message.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("KCP outgoing message/queue limit exceeded.");
        int count = (message.Length + Mss - 1) / Mss;
        if (waiting.Count + sending.Count + count > 2048) throw new InvalidDataException("KCP send queue exhausted.");
        queuedBytes += message.Length;
        for (int i = 0; i < count; i++)
        { int length = Math.Min(Mss, message.Length); waiting.Enqueue(((byte)(count - i - 1), message[..length].ToArray())); message = message[length..]; }
    }
    public void Update(uint monotonicMilliseconds)
    {
        ObjectDisposedException.ThrowIf(disposed, this); now = monotonicMilliseconds;
        while (waiting.Count > 0 && sending.Count < Math.Min(128, remoteWindow))
        { var next = waiting.Dequeue(); var segment = new Segment(sendNext++, next.Fragment, next.Body); sending.Add(segment.Serial, segment); }
        foreach (var segment in sending.Values)
        {
            if (segment.Transmissions != 0 && Difference(now, segment.Deadline) < 0) continue;
            if (segment.Transmissions >= 20) throw new IOException("KCP peer timed out.");
            Emit(81, segment.Fragment, segment.Serial, now, segment.Body);
            if (segment.Transmissions++ > 0) segment.Rto = Math.Min(2000, segment.Rto + 100);
            segment.Deadline = now + segment.Rto;
        }
        if (remoteWindow == 0 && Difference(now, probeAt) >= 0)
        { Emit(83, 0, 0, now, []); probeAt = now + 1000; }
    }
    private void Emit(byte command, byte fragment, uint serial, uint timestamp, ReadOnlySpan<byte> body)
    {
        var packet = new byte[HeaderSize + body.Length];
        if (format == KcpWireFormat.Standard32) BinaryPrimitives.WriteUInt32LittleEndian(packet, conversation);
        else BinaryPrimitives.WriteUInt64BigEndian(packet, conversation);
        var head = packet.AsSpan(ConvSize); head[0] = command; head[1] = fragment;
        BinaryPrimitives.WriteUInt16LittleEndian(head[2..], (ushort)(256 - receiving.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], timestamp); BinaryPrimitives.WriteUInt32LittleEndian(head[8..], serial);
        BinaryPrimitives.WriteUInt32LittleEndian(head[12..], receiveNext); BinaryPrimitives.WriteUInt32LittleEndian(head[16..], (uint)body.Length);
        body.CopyTo(packet.AsSpan(HeaderSize)); output(packet);
    }
    public bool TryReceive(out byte[] message)
    { ObjectDisposedException.ThrowIf(disposed, this); if (messages.TryDequeue(out var next)) { message = next; return true; } message = []; return false; }
    public void Dispose()
    { disposed = true; waiting.Clear(); sending.Clear(); receiving.Clear(); fragments.Clear(); messages.Clear(); queuedBytes = 0; }
}
