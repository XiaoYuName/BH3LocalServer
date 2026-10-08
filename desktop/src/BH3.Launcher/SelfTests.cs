using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace BH3.Launcher;

internal static class SelfTests
{
    private sealed class MemoryProxy : IProxyStore
    {
        internal ProxySnapshot Value = new(1, "127.0.0.1:7897", "<local>", "http://example.invalid/proxy.pac");
        public ProxySnapshot Read() => Value;
        public void Write(ProxySnapshot state) => Value = state;
    }
    private static void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int p = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return p; }
    internal static async Task<int> Run(string[] args)
    {
        string temp = Path.Combine(Path.GetTempPath(), "bh3-native-check-" + Guid.NewGuid().ToString("N"));
        AppPaths.Initialize(["--data-dir", temp]); using var log = new AppLog(temp);
        var checks = new List<object>(); bool passed = true;
        async Task Check(string name, Func<Task> body)
        {
            try { await body(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { passed = false; checks.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + " " + ex.Message); }
        }
        await Check("两份真实 9.1 密文：解密、JSON 与逐字节重新加密", () =>
        {
            foreach (string name in new[] { "dispatch", "gameserver" })
            {
                byte[] cipher = Convert.FromBase64String(Encoding.ASCII.GetString(AppPaths.Resource($"sample_{name}_91.bin")).Trim());
                byte[] plain = DispatchCrypto.Decrypt("9.1.0", cipher); using var json = JsonDocument.Parse(plain);
                Expect(json.RootElement.GetProperty("retcode").GetInt32() == (name == "dispatch" ? 0 : 5), "retcode");
                using var aes = Aes.Create(); aes.Key = DispatchCrypto.Key("9.1.0"); Expect(aes.EncryptEcb(plain, PaddingMode.PKCS7).SequenceEqual(cipher), "cipher mismatch");
            }
            Expect(Convert.ToHexString(MD5.HashData("9.0bgyzmmhy"u8.ToArray())).ToLowerInvariant() == "58260189ad8b4ab54d3dde91cb3b24d3", "9.0 derivation");
            return Task.CompletedTask;
        });
        var config = new LauncherConfig { HttpPort = 0, ProxyPort = 0, GamePort = 0 };
        var api = new LocalApi(config);
        await Check("本地 dispatch/gateway 使用 9.1 密钥及本机地址", () =>
        {
            foreach (string path in new[] { "/query_dispatch", "/query_gameserver" })
            {
                var response = api.Handle(new("GET", path + "?version=9.1.0_gf_pc", [], []));
                using var json = JsonDocument.Parse(DispatchCrypto.Decrypt("9.1.0", Convert.FromBase64String(Encoding.ASCII.GetString(response.Body))));
                Expect(json.RootElement.GetProperty("retcode").GetInt32() == 0, "local retcode");
                if (path == "/query_dispatch") Expect(json.RootElement.GetProperty("region_list").GetArrayLength() == 1, "cached official response must not be reused");
                else Expect(json.RootElement.GetProperty("gameserver").GetProperty("ip").GetString() == "127.0.0.1", "game address");
            }
            Expect(api.Handle(new("GET", "/query_dispatch?version=99.0", [], [])).Status == 400, "unsupported version");
            Expect(api.Handle(new("GET", "/query_security_file?file_key=..%2fconfig", [], [])).Status == 404, "cache traversal");
            return Task.CompletedTask;
        });
        await Check("代理路由限于游戏域名，资源请求放行", () =>
        {
            Expect(LocalStack.ShouldRoute("outer-dp-pc01.bh3.com", "/query_gameserver"), "gateway must route");
            Expect(!LocalStack.ShouldRoute("example.org", "/query_dispatch"), "unrelated host routed");
            Expect(!LocalStack.ShouldRoute("bh3.com.evil.invalid", "/query_dispatch"), "suffix boundary");
            Expect(!LocalStack.ShouldRoute("autopatchcn.bh3.com", "/asset_bundle/pc01/file"), "asset routed");
            Expect(!LocalStack.ShouldRoute("outer-dp-pc01.bh3.com", "/query_gameserver_invalid"), "path prefix boundary");
            return Task.CompletedTask;
        });
        await Check("HTTP chunked 请求解析与 CL/TE 歧义拒绝", async () =>
        {
            using var input = new MemoryStream("POST /test HTTP/1.1\r\nHost: localhost\r\nTransfer-Encoding: chunked\r\n\r\n4\r\ntest\r\n0\r\n\r\n"u8.ToArray());
            var request = await HttpWire.Read(input, default); Expect(Encoding.UTF8.GetString(request!.Body) == "test", "chunk body");
            using var bad = new MemoryStream("POST / HTTP/1.1\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n"u8.ToArray());
            bool rejected = false; try { await HttpWire.Read(bad, default); } catch (InvalidDataException) { rejected = true; } Expect(rejected, "ambiguous framing accepted");
        });
        await Check("代理恢复保留原值、缺失值及外部变更；无备份不写系统", () =>
        {
            var memory = new MemoryProxy(); var original = memory.Value; string backup = Path.Combine(temp, "proxy-test.json"); var lease = new ProxyLease(memory, backup, _ => { });
            lease.Restore(); Expect(memory.Value == original, "no backup changes proxy");
            lease.Enable(8080); lease.Enable(8080); lease.Restore(); Expect(memory.Value == original, "original proxy not restored");
            memory.Value = new(null, null, null, null); lease.Enable(8080); lease.Restore(); Expect(memory.Value == new ProxySnapshot(null, null, null, null), "missing values not restored");
            lease.Enable(8080); var external = new ProxySnapshot(1, "127.0.0.1:7999", null, null); memory.Value = external; lease.Restore(); Expect(memory.Value == external, "external change overwritten");
            return Task.CompletedTask;
        });
        await Check("证书私钥使用当前用户 DPAPI 保护", () =>
        {
            byte[] data = RandomNumberGenerator.GetBytes(64); byte[] encrypted = DataProtection.Transform(data, true);
            Expect(!encrypted.SequenceEqual(data) && DataProtection.Transform(encrypted, false).SequenceEqual(data), "DPAPI roundtrip"); return Task.CompletedTask;
        });
        using var certificates = new CertificateService();
        await Check("HTTP、HTTPS CONNECT、UDP 握手及停止后端口释放", async () =>
        {
            await using var stack = new LocalStack(config, certificates, log); await stack.Start();
            int hp = stack.HttpPort, pp = stack.ProxyPort, up = stack.UdpPort;
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            var health = await client.GetStringAsync($"http://127.0.0.1:{hp}/health"); Expect(health.Contains("ready"), "HTTP health");
            using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{pp}"), UseProxy = true };
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if ((errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0 || cert is null) return false;
                using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(certificates.Root); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(cert);
            };
            using var proxied = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            string encoded = await proxied.GetStringAsync("https://outer-dp-pc01.bh3.com/query_dispatch?version=9.1.0_gf_pc");
            using var payload = JsonDocument.Parse(DispatchCrypto.Decrypt("9.1.0", Convert.FromBase64String(encoded)));
            Expect(payload.RootElement.GetProperty("region_list")[0].GetProperty("title").GetString() == "本地服", "TLS proxy response");
            using var udp = new UdpClient(); var packet = new byte[20]; BinaryPrimitives.WriteInt32BigEndian(packet, 255); BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), 12345);
            await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, up)); using var timeout = new CancellationTokenSource(5000); var response = await udp.ReceiveAsync(timeout.Token);
            Expect(BinaryPrimitives.ReadInt32BigEndian(response.Buffer) == 0x145 && BinaryPrimitives.ReadInt32BigEndian(response.Buffer.AsSpan(12)) == 12345, "handshake");
            await stack.Stop(); var h = new TcpListener(IPAddress.Loopback, hp); var p = new TcpListener(IPAddress.Loopback, pp); h.Start(); p.Start(); h.Stop(); p.Stop();
            using var released = new UdpClient(new IPEndPoint(IPAddress.Loopback, up));
            await stack.Start(); await stack.Stop(); Expect(!stack.Running, "restart state");
        });
        await Check("代理端口冲突时回收先启动的 HTTP 监听", async () =>
        {
            var held = new TcpListener(IPAddress.Loopback, 0); held.Start();
            int local = FreePort(); var conflicting = new LauncherConfig { HttpPort = local, ProxyPort = ((IPEndPoint)held.LocalEndpoint).Port, EnableHandshake = false };
            try
            {
                await using var stack = new LocalStack(conflicting, certificates, log); bool rejected = false;
                try { await stack.Start(); } catch (SocketException) { rejected = true; }
                Expect(rejected && !stack.Running, "conflict was hidden"); var check = new TcpListener(IPAddress.Loopback, local); check.Start(); check.Stop();
            }
            finally { held.Stop(); }
        });
        await Check("配置校验与可移植设置往返", () =>
        {
            var value = new LauncherConfig { AccountName = "测试舰长", AccountUid = "10002" }; value.Save(); var restored = LauncherConfig.Load(); Expect(restored.AccountName == value.AccountName, "settings roundtrip");
            value.ProxyPort = value.HttpPort; bool rejected = false; try { value.Validate(); } catch (ArgumentException) { rejected = true; } Expect(rejected, "duplicate TCP ports");
            return Task.CompletedTask;
        });
        int reportIndex = Array.IndexOf(args, "--report"); string report = reportIndex >= 0 ? Path.GetFullPath(args[reportIndex + 1]) : Path.Combine(temp, "self-test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed, count = checks.Count, data_directory = temp, real_system_proxy_modified = false, root_certificate_installed = false, game_launched = false, checks }, LauncherConfig.Json));
        Console.WriteLine("Report: " + report); return passed ? 0 : 1;
    }
}
