using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using System.Text.Json;

namespace BH3.Launcher;

internal sealed class LauncherEngine : IDisposable
{
    internal LauncherConfig Config { get; private set; }
    internal AppLog Log { get; }
    internal CertificateService Certificates { get; } = new();
    internal ProxyLease Proxy { get; }
    private LocalStack? stack;
    private Process? server;
    private Process? game;
    private readonly string? serverConfigOverride;
    private readonly SemaphoreSlim gate = new(1);
    internal bool Running => stack?.Running == true;
    internal Uri? GmAddress { get; private set; }
    internal int? ServerProcessId => server is { HasExited: false } child ? child.Id : null;
    internal bool GameRunning => game is { HasExited: false };
    internal bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    internal LauncherEngine(LauncherConfig config, AppLog log, string? serverConfigOverride = null)
    {
        Config = config; Log = log; this.serverConfigOverride = serverConfigOverride;
        Proxy = new(new RegistryProxyStore(), Path.Combine(AppPaths.Data, "proxy-backup.json"), text => log.Write("代理", text));
        if (Proxy.Pending) Proxy.Restore();
        log.Write("登录器", "原生桌面版已就绪 · 9.1 dispatch 密钥已接入。");
    }
    internal void Save(LauncherConfig config)
    {
        if (Running) throw new InvalidOperationException("请先停止本地服务，再保存设置。");
        config.Validate(); config.Save(); Config = config;
    }
    internal string[] CheckClient()
    {
        string exe = Config.GameExe;
        if (!File.Exists(exe)) throw new FileNotFoundException("未找到 BH3.exe，请选择客户端目录。", exe);
        string root = Path.GetDirectoryName(exe)!;
        foreach (string file in new[] { @"BH3_Data\Native\UserAssembly.dll", @"BH3_Data\Managed\Metadata\global-metadata.dat" })
            if (!File.Exists(Path.Combine(root, file))) throw new FileNotFoundException("客户端文件缺失：" + file);
        string ini = Path.Combine(root, "config.ini");
        if (File.Exists(ini))
        {
            string? version = File.ReadLines(ini).Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("game_version=", StringComparison.OrdinalIgnoreCase) || x.StartsWith("game_version =", StringComparison.OrdinalIgnoreCase));
            if (version is not null && version.Split('=', 2)[1].Trim() != "9.1.0") throw new InvalidOperationException("当前桌面版适配 9.1.0；检测到 " + version);
        }
        byte[] sample = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(AppPaths.Resource("sample_dispatch_91.bin")).Trim());
        using var dispatch = JsonDocument.Parse(DispatchCrypto.Decrypt("9.1.0", sample));
        if (dispatch.RootElement.GetProperty("retcode").GetInt32() != 0)
            throw new InvalidDataException("内置 9.1 dispatch 样本校验失败。");
        return ["BH3.exe 已找到", "UserAssembly.dll 与元数据文件齐全", "9.1 dispatch 样本解密通过", "游戏服：" + (Config.ResolveServerExe() is { Length: > 0 } service ? service : "仅握手入口，尚不能进入大厅")];
    }
    internal async Task Start()
    {
        await gate.WaitAsync();
        try
        {
            if (Running) return;
            Config.Validate();
            Config.LocalAuthKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            string serverExe = Config.ResolveServerExe();
            if (serverExe.Length > 0)
            {
                if (!File.Exists(serverExe)) throw new FileNotFoundException("未找到服务端，请完整保留发布包中的 Launcher 和 Server 文件夹。", serverExe);
                if (serverExe.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("不能将登录器自身作为游戏服。");
                bool bundledProtocol = Path.GetFileName(serverExe).Equals("BH3.Server.exe", StringComparison.OrdinalIgnoreCase);
                string configPath = serverConfigOverride ?? Path.Combine(Path.GetDirectoryName(serverExe)!, "config", "server.json");
                var info = new ProcessStartInfo(serverExe) { WorkingDirectory = Path.GetDirectoryName(serverExe)!, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                info.Environment["BH3_LOCAL_AUTH_KEY"] = Convert.ToBase64String(Config.LocalAuthKey);
                if (bundledProtocol)
                {
                    info.ArgumentList.Add("--launcher-control"); info.ArgumentList.Add("--config"); info.ArgumentList.Add(configPath);
                    info.ArgumentList.Add("--game-port"); info.ArgumentList.Add(Config.GamePort.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    info.ArgumentList.Add("--transport"); info.ArgumentList.Add("kcp");
                }
                var candidate = new Process { StartInfo = info, EnableRaisingEvents = true };
                candidate.OutputDataReceived += (_, e) => { if (e.Data is not null) Log.Write("游戏服", e.Data); };
                candidate.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log.Write("游戏服", e.Data); };
                try { if (!candidate.Start()) throw new InvalidOperationException("无法创建游戏服进程。"); }
                catch { candidate.Dispose(); throw; }
                server = candidate; server.BeginOutputReadLine(); server.BeginErrorReadLine();
                if (bundledProtocol) await WaitForServerReady(candidate, configPath);
                else { await Task.Delay(350); if (server.HasExited) throw new InvalidOperationException($"外部游戏服退出，代码 {server.ExitCode}。"); }
                candidate.Exited += (_, _) => _ = HandleServerExit(candidate);
                if (candidate.HasExited) throw new InvalidOperationException("服务端在启动期间退出。");
            }
            var effective = Config.Copy(); effective.ServerExe = serverExe;
            stack = new(effective, Certificates, Log); await stack.Start();
        }
        catch { await StopOwned(); throw; }
        finally { gate.Release(); }
    }
    private async Task WaitForServerReady(Process child, string configPath)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = settings.RootElement;
        string bind = root.TryGetProperty("bindAddress", out var address) ? address.GetString()! : "127.0.0.1";
        int port = root.TryGetProperty("healthPort", out var health) ? health.GetInt32() : 21080;
        if (!System.Net.IPAddress.TryParse(bind, out var ip) || !System.Net.IPAddress.IsLoopback(ip) || port is < 1024 or > 65535)
            throw new InvalidDataException("服务端健康检查必须使用有效的本机地址和端口。");
        var uri = new UriBuilder("http", bind, port, "/health/ready").Uri;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMilliseconds(800) };
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(12))
        {
            if (child.HasExited) throw new InvalidOperationException($"服务端启动失败，退出码 {child.ExitCode}，详情见运行记录。");
            try
            {
                using var response = await http.GetAsync(uri);
                if (response.IsSuccessStatusCode)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var state = body.RootElement;
                    if (state.TryGetProperty("processId", out var pid) && pid.GetInt32() == child.Id &&
                        state.GetProperty("service").GetString() == "BH3.Server" && state.GetProperty("hostReady").GetBoolean() &&
                        state.GetProperty("gamePort").GetInt32() == Config.GamePort)
                    { GmAddress = new UriBuilder("http",bind,port,"/gm/index.html").Uri; Log.Write("服务", $"随包服务端已就绪 · PID {child.Id} · UDP {Config.GamePort}。"); return; }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("服务端未在 12 秒内就绪，请查看运行记录或检查健康端口是否被占用。");
    }
    private async Task HandleServerExit(Process child)
    {
        await gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(server, child)) return;
            Log.Write("错误", $"服务端意外退出（代码 {child.ExitCode}），正在停止本地链路。");
            await StopOwned();
        }
        catch (Exception ex) { Log.Write("错误", ex.Message); }
        finally { gate.Release(); }
    }
    internal async Task Stop()
    {
        await gate.WaitAsync();
        try { await StopOwned(); Log.Write("服务", "本地服务已停止。"); }
        finally { gate.Release(); }
    }
    private async Task StopOwned()
    {
        // Restore the route while the listener can still serve requests.
        Exception? restorationError = null;
        try { Proxy.Restore(); } catch (Exception ex) { restorationError = ex; Log.Write("错误", "代理恢复失败，已保留恢复文件：" + ex.Message); }
        if (stack is not null) { await stack.DisposeAsync(); stack = null; }
        if (server is not null)
        {
            if (!server.HasExited)
            {
                try { await server.StandardInput.WriteLineAsync("stop"); server.StandardInput.Close(); } catch (IOException) { }
                try { await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); }
                catch (TimeoutException) { server.Kill(true); await server.WaitForExitAsync(); }
            }
            server.Dispose(); server = null;
        }
        if (restorationError is not null) throw new InvalidOperationException("服务已关闭，但代理恢复失败。请使用“恢复系统代理”重试。", restorationError);
    }
    internal async Task Launch()
    {
        if (GameRunning) throw new InvalidOperationException("本次启动的游戏仍在运行。");
        CheckClient();
        if (!Certificates.Installed) throw new InvalidOperationException("请先点击“安装本地证书”，完成 Windows 确认后再启动游戏。");
        bool startedHere = !Running;
        try
        {
            await Start();
            if (Config.UseSystemProxy) Proxy.Enable(Config.ProxyPort, Config.UseWinHttp);
            var start = new ProcessStartInfo(Config.GameExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Config.GameExe)! };
            foreach (string name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" }) start.Environment[name] = $"http://127.0.0.1:{Config.ProxyPort}";
            try { game = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                if (!Config.UseSystemProxy) throw new InvalidOperationException("客户端要求管理员权限；请启用启动时接管系统代理后重试。");
                game = Process.Start(new ProcessStartInfo(Config.GameExe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = start.WorkingDirectory });
            }
            if (game is null) throw new InvalidOperationException("未能创建游戏进程。");
            Log.Write("游戏", $"已启动 BH3.exe · PID {game.Id}。");
        }
        catch { Proxy.Restore(); if (startedHere) await Stop(); throw; }
    }
    internal void ExportDiagnostics(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("运行记录.txt").Open())) writer.Write(string.Join(Environment.NewLine, Log.Snapshot()));
        using (var writer = new StreamWriter(zip.CreateEntry("状态.json").Open())) writer.Write(JsonSerializer.Serialize(new { desktop = true, version = typeof(LauncherEngine).Assembly.GetName().Version?.ToString(3), client = File.Exists(Config.GameExe), Running, GameRunning, system_proxy_owned = Proxy.Active, Config.HttpPort, Config.ProxyPort, Config.GamePort, managed_game_server = Config.ResolveServerExe().Length > 0, key_version = "9.1.0", gameplay = "not-verified" }, LauncherConfig.Json));
        // Certificate private keys, proxy snapshots and SDK request bodies are deliberately excluded.
    }
    public void Dispose()
    {
        try { Stop().GetAwaiter().GetResult(); } catch (Exception ex) { Log.Write("错误", ex.Message); }
        // A process-exit callback can still be queued; this semaphore has no native wait handle.
        Certificates.Dispose(); game?.Dispose();
    }
}
