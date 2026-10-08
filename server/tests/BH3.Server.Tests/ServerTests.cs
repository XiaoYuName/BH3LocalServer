using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BH3.Server.Configuration;
using BH3.Server.Hosting;
using BH3.Server.Preflight;
using BH3.Tests;

namespace BH3.Server.Tests;

public sealed class ServerTests
{
    private static ServerConfig Config(TestDirectory temp) => new()
    { BaseDirectory = temp.Path, GamePort = 0, HealthPort = 0, DatabasePath = "data/players.db", LogDirectory = "logs", TransportMode = "handshake-only" };
    private static byte[] Connect(uint nonce)
    { var request = new byte[20]; request[3] = 255; BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(12),nonce); return request; }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public void PreflightResolvesPathsFromConfigAndDoesNotCreateDatabase()
    {
        using var temp = new TestDirectory(); string path = Path.Combine(temp.Path,"server.json");
        File.WriteAllText(path,"{\"databasePath\":\"nested/test.db\",\"logDirectory\":\"logs\"}");
        var config = ServerConfig.Load(path); ServerPreflight.Check(config);
        Assert.Equal(Path.Combine(temp.Path,"nested","test.db"),config.ResolveDatabase()); Assert.False(Directory.Exists(Path.Combine(temp.Path,"nested")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path,"logs")));
    }
    [Theory]
    [InlineData("{\"gamePort\":0}")]
    [InlineData("{\"bindAddress\":\"0.0.0.0\"}")]
    [InlineData("{\"transportMode\":\"unknown\"}")]
    [InlineData("{\"maxSessions\":0}")]
    [InlineData("{\"clientVersion\":\"9.0.0\"}")]
    public void UnsupportedConfigurationsAreRejected(string json)
    {
        using var temp = new TestDirectory(); string path = Path.Combine(temp.Path,"server.json"); File.WriteAllText(path,json);
        Assert.Throws<ArgumentException>(() => ServerConfig.Load(path));
    }
    [Fact]
    public void ConfigurationTyposAreRejected()
    {
        using var temp = new TestDirectory(); string path = Path.Combine(temp.Path,"server.json"); File.WriteAllText(path,"{\"gamePrt\":21000}");
        Assert.Throws<JsonException>(() => ServerConfig.Load(path));
    }
    [Fact]
    public async Task LiveHttpAndUdpExposeHonestCapabilitiesAndDuplicateHandshake()
    {
        using var temp = new TestDirectory(); await using var server = new ServerRuntime(Config(temp),TextWriter.Null);
        await server.StartAsync(Ct,allowEphemeralPorts:true);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy=false });
        using var status = JsonDocument.Parse(await http.GetStringAsync(server.HealthAddress+"/health/ready",Ct));
        Assert.True(status.RootElement.GetProperty("hostReady").GetBoolean()); Assert.False(status.RootElement.GetProperty("gameplayReady").GetBoolean());
        Assert.Empty(status.RootElement.GetProperty("registeredCommands").EnumerateArray());
        using var udp = new UdpClient(); var endpoint = new IPEndPoint(IPAddress.Loopback,server.GamePort);
        await udp.SendAsync(Connect(77),endpoint,Ct); var first = await udp.ReceiveAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3),Ct);
        await udp.SendAsync(Connect(77),endpoint,Ct); var second = await udp.ReceiveAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3),Ct);
        Assert.Equal(first.Buffer,second.Buffer); Assert.Equal(1,server.Composition.Sessions.Count);
        Assert.Equal(0x145u,BinaryPrimitives.ReadUInt32BigEndian(first.Buffer));
        await udp.SendAsync(new byte[]{1,2,3},endpoint,Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct); timeout.CancelAfter(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await udp.ReceiveAsync(timeout.Token));
        Assert.Equal(1,server.Composition.Sessions.Count);
    }
    [Fact]
    public async Task SessionSweepAndShutdownReleaseResources()
    {
        using var temp = new TestDirectory(); await using var server = new ServerRuntime(Config(temp) with { SessionIdleSeconds=1 },TextWriter.Null);
        await server.StartAsync(Ct,allowEphemeralPorts:true); int port = server.GamePort; int health = new Uri(server.HealthAddress).Port;
        using var udp = new UdpClient(); await udp.SendAsync(Connect(1),new IPEndPoint(IPAddress.Loopback,port),Ct);
        await udp.ReceiveAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3),Ct);
        await Task.Delay(2200,Ct); Assert.Equal(0,server.Composition.Sessions.Count);
        await server.DisposeAsync(); Assert.False(server.Ready);
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback,port));
        using var tcp = new TcpListener(IPAddress.Loopback,health); tcp.Start(); tcp.Stop();
    }
    [Fact]
    public async Task HttpBindFailureReleasesPreviouslyBoundUdpAndDatabaseLease()
    {
        using var temp = new TestDirectory(); using var held = new TcpListener(IPAddress.Loopback,0); held.Start();
        using var free = new UdpClient(new IPEndPoint(IPAddress.Loopback,0)); int port = ((IPEndPoint)free.Client.LocalEndPoint!).Port; free.Dispose();
        var config = Config(temp) with { GamePort=port,HealthPort=((IPEndPoint)held.LocalEndpoint).Port };
        await using (var server = new ServerRuntime(config,TextWriter.Null))
        { await Assert.ThrowsAnyAsync<IOException>(() => server.StartAsync(Ct)); Assert.False(server.Ready); }
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback,port));
        await using var retry = new ServerRuntime(Config(temp),TextWriter.Null); await retry.StartAsync(Ct,allowEphemeralPorts:true); Assert.True(retry.Ready);
    }
    [Fact]
    public async Task AnotherHostCannotOwnSameDatabase()
    {
        using var temp = new TestDirectory(); var config = Config(temp);
        await using var first = new ServerRuntime(config,TextWriter.Null); await first.StartAsync(Ct,allowEphemeralPorts:true);
        await using var second = new ServerRuntime(config,TextWriter.Null);
        await Assert.ThrowsAnyAsync<IOException>(() => second.StartAsync(Ct,allowEphemeralPorts:true)); Assert.True(first.Ready);
    }
}
