using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace BH3.Launcher;

internal static class BundleTests
{
    private static int FreeTcp() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static int FreeUdp() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
    private static void Expect(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    internal static async Task<int> Run(string[] args)
    {
        string temp = Path.Combine(Path.GetTempPath(), "bh3-bundle-check-" + Guid.NewGuid().ToString("N"));
        AppPaths.Initialize(["--data-dir", temp]); using var log = new AppLog(temp);
        var checks = new List<object>(); bool passed = true;
        async Task Check(string name, Func<Task> work)
        {
            try { await work(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { passed = false; checks.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + " " + ex.Message); }
        }
        var config = new LauncherConfig { HttpPort = FreeTcp(), ProxyPort = FreeTcp(), GamePort = FreeUdp(), UseSystemProxy = false };
        int healthPort = FreeTcp(); string serverConfig = Path.Combine(temp, "server.json");
        File.WriteAllText(serverConfig, JsonSerializer.Serialize(new { clientVersion = "9.1.0", bindAddress = "127.0.0.1", gamePort = FreeUdp(), healthPort,
            databasePath = "players/test.db", logDirectory = "server-logs", transportMode = "handshake-only" }));
        await Check("完整发布目录自动定位服务端，保持相对位置可移动", () =>
        {
            Expect(config.ServerExe == "" && File.Exists(config.ResolveServerExe()), "bundled server missing");
            string moved = Path.Combine(temp, "moved"); Directory.CreateDirectory(Path.Combine(moved, "Launcher")); Directory.CreateDirectory(Path.Combine(moved, "Server"));
            string expected = Path.Combine(moved, "Server", "BH3.Server.exe"); File.WriteAllBytes(expected, []);
            Expect(config.ResolveServerExe(Path.Combine(moved, "Launcher")) == expected, "resolver pins old path");
            File.Delete(expected); File.WriteAllText(Path.Combine(moved, "BH3.package.json"), "{}");
            Expect(config.ResolveServerExe(Path.Combine(moved, "Launcher")) == expected, "incomplete bundle silently falls back");
            return Task.CompletedTask;
        });
        using var engine = new LauncherEngine(config, log, serverConfig);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        await Check("登录器启动真实服务端并核对 PID、端口和健康状态", async () =>
        {
            await engine.Start();
            using var state = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{healthPort}/health/ready"));
            Expect(engine.Running && state.RootElement.GetProperty("processId").GetInt32() == engine.ServerProcessId, "process readiness mismatch");
            Expect(state.RootElement.GetProperty("gamePort").GetInt32() == config.GamePort, "game port override not applied");
            Expect(!state.RootElement.GetProperty("gameplayReady").GetBoolean(), "gameplay must remain unimplemented");
            Expect((await http.GetStringAsync($"http://127.0.0.1:{config.HttpPort}/health")).Contains("external"), "local API does not use managed server");
            using var udp = new UdpClient(); var packet = new byte[20]; BinaryPrimitives.WriteInt32BigEndian(packet, 255);
            await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, config.GamePort));
            using var timeout = new CancellationTokenSource(3000); var reply = await udp.ReceiveAsync(timeout.Token);
            Expect(BinaryPrimitives.ReadInt32BigEndian(reply.Buffer) == 0x145, "managed server handshake");
        });
        await Check("停止服务正常关闭子进程并释放全部监听", async () =>
        {
            await engine.Stop(); Expect(!engine.Running && engine.ServerProcessId is null, "owned process remains");
            foreach (int port in new[] { config.HttpPort, config.ProxyPort, healthPort }) { using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); }
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, config.GamePort));
        });
        await Check("重启后服务端异常退出会自动停止本地链路", async () =>
        {
            await engine.Start(); using var child = Process.GetProcessById(engine.ServerProcessId!.Value); child.Kill(); await child.WaitForExitAsync();
            var watch = Stopwatch.StartNew(); while (engine.Running && watch.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(100);
            Expect(!engine.Running, "unexpected exit left API running"); await engine.Stop();
        });
        await Check("本地代理端口冲突时回收已经启动的服务端", async () =>
        {
            using var held = new TcpListener(IPAddress.Loopback, config.ProxyPort); held.Start();
            bool rejected = false; try { await engine.Start(); } catch (SocketException) { rejected = true; }
            Expect(rejected && !engine.Running && engine.ServerProcessId is null, "partial startup not rolled back");
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, config.GamePort));
            using var health = new TcpListener(IPAddress.Loopback, healthPort); health.Start();
        });
        await engine.Stop();
        int reportIndex = Array.IndexOf(args, "--report"); string report = reportIndex >= 0 ? Path.GetFullPath(args[reportIndex + 1]) : Path.Combine(temp, "bundle-test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed, count = checks.Count, data_directory = temp,
            package_directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")), real_system_proxy_modified = false,
            root_certificate_installed = false, game_launched = false, checks, logs = log.Snapshot() }, LauncherConfig.Json));
        Console.WriteLine("Report: " + report); return passed ? 0 : 1;
    }
}
