using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace BH3.Launcher;

internal sealed class LocalStack(LauncherConfig config, CertificateService certificates, AppLog log) : IAsyncDisposable
{
    private readonly LocalApi api = new(config);
    private readonly ConcurrentDictionary<int, TcpClient> connections = new();
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private readonly List<Task> loops = [];
    private readonly HttpClient upstream = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromMinutes(5) };
    private CancellationTokenSource? stop;
    private TcpListener? http, proxy;
    private UdpClient? udp;
    private int serial;
    internal bool Running => stop is { IsCancellationRequested: false };
    internal int HttpPort => ((IPEndPoint)http!.LocalEndpoint).Port;
    internal int ProxyPort => ((IPEndPoint)proxy!.LocalEndpoint).Port;
    internal int UdpPort => ((IPEndPoint)udp!.Client.LocalEndPoint!).Port;
    internal async Task Start()
    {
        if (Running) return;
        try
        {
            stop = new();
            http = new(IPAddress.Loopback, config.HttpPort); http.Start();
            proxy = new(IPAddress.Loopback, config.ProxyPort); proxy.Start();
            if (config.EnableHandshake && config.ServerExe.Length == 0) udp = new(new IPEndPoint(IPAddress.Loopback, config.GamePort));
            loops.Add(Accept(http, false, stop.Token)); loops.Add(Accept(proxy, true, stop.Token));
            if (udp is not null) loops.Add(Handshake(udp, stop.Token));
            log.Write("服务", $"本地接口 127.0.0.1:{HttpPort} · 代理 127.0.0.1:{ProxyPort} 已就绪。");
            if (udp is not null) log.Write("服务", "UDP 仅处理握手；完整游戏服尚未实现。");
        }
        catch { await Stop(); throw; }
    }
    private async Task Accept(TcpListener listener, bool isProxy, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token); int id = Interlocked.Increment(ref serial);
                if (connections.Count >= 128) { client.Dispose(); continue; }
                connections[id] = client;
                var task = Serve(client, isProxy, token); clients[id] = task;
                _ = task.ContinueWith(completed => { connections.TryRemove(id, out _); clients.TryRemove(id, out _); client.Dispose(); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { } catch (SocketException) when (token.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient client, bool isProxy, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(5)); token = deadline.Token;
        try
        {
            var stream = client.GetStream(); var request = await HttpWire.Read(stream, token); if (request is null) return;
            if (!isProxy) { await Local(stream, request, token); return; }
            if (request.Method == "CONNECT")
            {
                if (!Uri.TryCreate("https://" + request.Target, UriKind.Absolute, out var target)) throw new InvalidDataException("无效 CONNECT 目标。");
                if (!InterceptHost(target.Host))
                {
                    using var remote = new TcpClient(); await remote.ConnectAsync(target.Host, target.Port, token);
                    await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                    using var relay = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var a = stream.CopyToAsync(remote.GetStream(), relay.Token); var b = remote.GetStream().CopyToAsync(stream, relay.Token);
                    await Task.WhenAny(a, b); await relay.CancelAsync();
                    try { await Task.WhenAll(a, b); } catch (OperationCanceledException) { }
                    return;
                }
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                using var tls = new SslStream(stream, true);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificates.ForHost(target.Host), EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, ApplicationProtocols = [SslApplicationProtocol.Http11] }, token);
                request = await HttpWire.Read(tls, token); if (request is null) return;
                if (ShouldRoute(target.Host, request.Path)) await Local(tls, request, token);
                else await Forward(tls, new Uri(target, request.Path), request, token);
            }
            else
            {
                if (!Uri.TryCreate(request.Target, UriKind.Absolute, out var target) || target.Scheme != "http") throw new InvalidDataException("代理请求需包含完整 HTTP 地址。");
                if (ShouldRoute(target.Host, target.AbsolutePath)) await Local(stream, request, token);
                else await Forward(stream, target, request, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or HttpRequestException or ArgumentException or ObjectDisposedException or System.Security.Cryptography.CryptographicException)
        { if (!token.IsCancellationRequested) log.Write("连接", ex.Message); }
    }
    internal static bool InterceptHost(string host) => (host.Equals("bh3.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".bh3.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".mihoyo.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".hoyoverse.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".honkaiimpact3.com", StringComparison.OrdinalIgnoreCase)) && host is not ("client-report.bh3.com" or "log-upload.bh3.com" or "log-upload-os.hoyoverse.com");
    internal static bool ShouldRoute(string host, string target)
    {
        if (!InterceptHost(host)) return false;
        string path = target.Split('?')[0];
        return new[] { "/query_dispatch", "/query_gateway", "/query_gameserver", "/query_security_file" }.Contains(path)
            || new[] { "/bh3_cn/", "/bh3_usa/", "/account/ma-cn-passport/", "/combo/", "/device-fp/", "/data_abtest_api/", "/sdk/", "/common/h5log/", "/announcement/" }.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal))
            || path is "/sw.html" or "/_ts" or "/report";
    }
    private async Task Local(Stream stream, WireRequest request, CancellationToken token)
    {
        ApiResponse result;
        try { result = api.Handle(request); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { result = LocalApi.Json(new { retcode = -1, msg = "invalid request" }, 400); }
        // Request bodies and query strings may contain account data: retain only method and endpoint.
        log.Write("接口", $"{request.Method} {request.Path.Split('?')[0]} → {result.Status}");
        await HttpWire.Reply(stream, result.Status, result.Body, result.Type, token, request.Method == "HEAD");
    }
    private async Task Forward(Stream stream, Uri uri, WireRequest request, CancellationToken token)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        if (request.Body.Length > 0) message.Content = new ByteArrayContent(request.Body);
        foreach (var h in request.Headers)
        {
            if (new[] { "host", "connection", "proxy-connection", "proxy-authorization", "content-length", "transfer-encoding", "expect" }.Contains(h.Key.ToLowerInvariant())) continue;
            if (!message.Headers.TryAddWithoutValidation(h.Key, h.Value)) message.Content?.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        using var response = await upstream.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        var header = new StringBuilder($"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n");
        foreach (var h in response.Headers.Concat(response.Content.Headers))
            if (!new[] { "connection", "transfer-encoding", "keep-alive" }.Contains(h.Key.ToLowerInvariant())) foreach (var value in h.Value) header.Append(h.Key).Append(": ").Append(value).Append("\r\n");
        header.Append("Connection: close\r\n\r\n"); await stream.WriteAsync(Encoding.Latin1.GetBytes(header.ToString()), token);
        if (request.Method != "HEAD") await response.Content.CopyToAsync(stream, token);
        await stream.FlushAsync(token);
    }
    private async Task Handshake(UdpClient socket, CancellationToken token)
    {
        int conv = 0;
        try
        {
            while (true)
            {
                var p = await socket.ReceiveAsync(token); if (p.Buffer.Length < 20) continue;
                if (BinaryPrimitives.ReadInt32BigEndian(p.Buffer) != 255) continue;
                var response = new byte[20]; BinaryPrimitives.WriteInt32BigEndian(response, 0x145);
                BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(8), ++conv); p.Buffer.AsSpan(12, 4).CopyTo(response.AsSpan(12));
                BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(16), 0x14514545);
                await socket.SendAsync(response, p.RemoteEndPoint, token); log.Write("握手", "已应答客户端握手；后续 KCP 游戏逻辑尚未实现。");
            }
        }
        catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
    }
    internal async Task Stop()
    {
        if (stop is null) return;
        await stop.CancelAsync(); http?.Stop(); proxy?.Stop(); udp?.Dispose();
        foreach (var c in connections.Values) c.Dispose();
        try { await Task.WhenAll(loops.Concat(clients.Values)); } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        loops.Clear(); connections.Clear(); clients.Clear(); stop.Dispose(); stop = null; http = null; proxy = null; udp = null;
    }
    public async ValueTask DisposeAsync() { await Stop(); upstream.Dispose(); }
}
