using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BH3.Game.Messaging;
using BH3.Game.Sessions;
using BH3.Protocol;

namespace BH3.Server.Transport;

public sealed class UdpGameHost(SessionRegistry sessions, GameDispatcher? dispatcher = null, Action<string, object>? log = null) : IAsyncDisposable
{
    private sealed record Peer(GameSession Session, KcpReliableChannel Channel);
    private readonly Lock gate = new();
    private readonly Dictionary<string, Peer> peers = [];
    private readonly Queue<object> recentCommands = new();
    private UdpClient? socket;
    private CancellationTokenSource? stop;
    private Task? receive, sweep;
    private long received, handshakes, unsupported, rejected, messages;
    private static uint Tick => unchecked((uint)Environment.TickCount64);
    public int Port => ((IPEndPoint)socket!.Client.LocalEndPoint!).Port;
    public bool Running => stop is { IsCancellationRequested: false } && receive is { IsCompleted: false } && sweep is { IsCompleted: false };
    public object Counters
    {
        get { lock (gate) return new { received, handshakes, unsupported, rejected, messages, channels = peers.Count, recentCommands = recentCommands.ToArray() }; }
    }
    public T ApplyGm<T>(uint uid,Func<T> action,Func<GameSession,IReadOnlyList<GamePacket>> notifications)
    {
        lock(gate)
        {
            T result=action();
            foreach(var pair in peers.ToArray().Where(x=>x.Value.Session.PlayerId==uid&&x.Value.Session.State==SessionState.Authenticated))
            {
                try{foreach(var packet in notifications(pair.Value.Session))pair.Value.Channel.Send(GamePacketCodec.Encode(packet));pair.Value.Channel.Update(Tick);}
                catch(Exception ex) when(ex is not OutOfMemoryException)
                {log?.Invoke("gm.notify.failed",new {uid,error=ex.GetType().Name});pair.Value.Session.Close();Remove(pair.Key);}
            }
            return result;
        }
    }
    public void Start(IPAddress address, int port)
    {
        if (socket is not null) throw new InvalidOperationException("UDP host is already started.");
        var candidate = new UdpClient(address.AddressFamily);
        try { candidate.Client.ExclusiveAddressUse = true; candidate.Client.Bind(new IPEndPoint(address, port)); }
        catch { candidate.Dispose(); throw; }
        socket = candidate; stop = new(); receive = Receive(stop.Token); sweep = Sweep(stop.Token);
    }
    private async Task Receive(CancellationToken token)
    {
        try
        {
            while (true)
            {
                var datagram = await socket!.ReceiveAsync(token);
                lock (gate) Process(datagram);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
    }
    private void Process(UdpReceiveResult datagram)
    {
        received++; string key = datagram.RemoteEndPoint.ToString();
        try
        {
            if (HandshakeCodec.TryReadConnect(datagram.Buffer, out var request))
            {
                var session = sessions.Connect(key, request.ClientNonce);
                if (session is null) { rejected++; return; }
                if (peers.TryGetValue(key, out var old) && !ReferenceEquals(session, old.Session)) Remove(key);
                Send(HandshakeCodec.Accept(session.Conversation, request), datagram.RemoteEndPoint); handshakes++;
                log?.Invoke("session.connect", new { conversation = session.Conversation }); return;
            }
            var current = sessions.Find(key);
            if (current is null) { rejected++; return; }
            if (datagram.Buffer.Length == 20 && BinaryPrimitives.ReadUInt32BigEndian(datagram.Buffer) == 404
                && BinaryPrimitives.ReadUInt32LittleEndian(datagram.Buffer.AsSpan(8)) == current.Conversation)
            { current.Close(); Remove(key); return; }
            if (dispatcher is null) { unsupported++; return; }
            if (!peers.TryGetValue(key, out var peer))
            {
                if (!KcpReliableChannel.TryIdentify(datagram.Buffer, current.Conversation, out var format)) { unsupported++; return; }
                peer = new(current, new(current.Conversation, format, bytes => Send(bytes, datagram.RemoteEndPoint))); peers.Add(key, peer);
                log?.Invoke("session.transport", new { conversation = current.Conversation, format = format.ToString() });
            }
            peer.Channel.Input(datagram.Buffer, Tick); sessions.Touch(key);
            while (peer.Channel.TryReceive(out var message))
            {
                if (!GamePacketCodec.TryDecodeBatch(message, out var packets)) throw new InvalidDataException("Invalid reassembled game frame.");
                foreach (var packet in packets)
                {
                    messages++; var result = dispatcher.Dispatch(current, packet);
                    var observation = new { conversation = current.Conversation, command = packet.CommandId, bodyBytes = packet.Body.Length, status = result.Status.ToString(), replies = result.Replies.Select(r => r.CommandId).ToArray(), replyBodyBytes = result.Replies.Select(r => r.Body.Length).ToArray(), state = current.State.ToString() };
                    recentCommands.Enqueue(observation); if (recentCommands.Count > 100) recentCommands.Dequeue();
                    log?.Invoke("game.command", observation);
                    if (CampaignDiagnostics.Describe(packet, result) is { } gameplay)
                        log?.Invoke("game.campaign", new { conversation = current.Conversation, uid = current.PlayerId, gameplay });
                    foreach (var reply in result.Replies) peer.Channel.Send(GamePacketCodec.Encode(reply));
                    if (result.Status == DispatchStatus.Unsupported) unsupported++;
                }
            }
            peer.Channel.Update(Tick);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            rejected++; log?.Invoke("session.error", new { error = ex.GetType().Name, message = ex.Message });
            sessions.Find(key)?.Close(); Remove(key);
        }
    }
    private void Send(byte[] bytes, IPEndPoint endpoint) => socket!.Send(bytes, endpoint);
    private void Remove(string key) { if (peers.Remove(key, out var peer)) peer.Channel.Dispose(); }
    private async Task Sweep(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10)); uint lastSweep = Tick;
        try
        {
            while (await timer.WaitForNextTickAsync(token))
                lock (gate)
                {
                    uint now = Tick;
                    if (unchecked(now - lastSweep) >= 1000) { sessions.Expire(); lastSweep = now; }
                    foreach (var pair in peers.ToArray())
                    {
                        if (pair.Value.Session.State == SessionState.Closed) { Remove(pair.Key); continue; }
                        try { pair.Value.Channel.Update(now); }
                        catch (Exception ex) when (ex is IOException or SocketException)
                        { log?.Invoke("session.timeout", new { conversation = pair.Value.Session.Conversation }); pair.Value.Session.Close(); Remove(pair.Key); }
                    }
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (stop is null) return;
        await stop.CancelAsync();
        try { await Task.WhenAll(receive!, sweep!); }
        finally
        {
            lock (gate) { foreach (var peer in peers.Values) peer.Channel.Dispose(); peers.Clear(); sessions.CloseAll(); socket!.Dispose(); }
            stop.Dispose(); stop = null; socket = null;
        }
    }
}
