using System.Reflection;
using System.Text.Json;

namespace BH3.Launcher;

internal static class AppPaths
{
    internal static string Data { get; private set; } = "";
    internal static string? Workspace { get; private set; }
    internal static void Initialize(string[] args)
    {
        Workspace = Ancestors(AppContext.BaseDirectory).FirstOrDefault(p => Directory.Exists(Path.Combine(p, "Honkai Impact 3rd Game")));
        int i = Array.IndexOf(args, "--data-dir");
        Data = i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : Path.Combine(AppContext.BaseDirectory, "Data");
        Directory.CreateDirectory(Data);
    }
    internal static IEnumerable<string> Ancestors(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent) yield return d.FullName;
    }
    internal static byte[] Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BH3.Launcher.Resources." + name)
            ?? throw new FileNotFoundException("缺少内置文件：" + name);
        using var ms = new MemoryStream(); stream.CopyTo(ms); return ms.ToArray();
    }
    internal static void AtomicWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}

internal sealed class LauncherConfig
{
    public string GameExe { get; set; } = "";
    public int HttpPort { get; set; } = 20100;
    public int ProxyPort { get; set; } = 8080;
    public int GamePort { get; set; } = 21000;
    public string AccountName { get; set; } = "captain";
    public string AccountUid { get; set; } = "10001";
    public bool UseSystemProxy { get; set; } = true;
    public bool UseWinHttp { get; set; } = false;
    public string Wallpaper { get; set; } = "";
    public string ServerExe { get; set; } = "";
    public bool EnableHandshake { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public byte[] LocalAuthKey { get; set; } = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal string ResolveServerExe(string? launcherDirectory = null)
    {
        string directory = launcherDirectory ?? AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(ServerExe)) return Path.GetFullPath(ServerExe, directory);
        string bundle = Path.GetFullPath(Path.Combine(directory, ".."));
        string candidate = Path.Combine(bundle, "Server", "BH3.Server.exe");
        return File.Exists(candidate) || File.Exists(Path.Combine(bundle, "BH3.package.json")) ? candidate : "";
    }
    internal static LauncherConfig Load()
    {
        var path = Path.Combine(AppPaths.Data, "settings.json");
        LauncherConfig config;
        if (File.Exists(path))
        {
            try { config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path)) ?? throw new JsonException("配置为空"); }
            catch (JsonException ex) { throw new InvalidDataException("设置文件损坏，请检查 " + path, ex); }
        }
        else
        {
            config = new();
            if (AppPaths.Workspace is { } workspace) config.GameExe = Path.Combine(workspace, "Honkai Impact 3rd Game", "BH3.exe");
            config.Save();
        }
        config.Validate(); return config;
    }
    internal void Validate()
    {
        if (new[] { HttpPort, ProxyPort, GamePort }.Any(p => p < 1024 || p > 65535)) throw new ArgumentException("端口应在 1024–65535 之间。");
        if (HttpPort == ProxyPort) throw new ArgumentException("本地接口与代理不能使用同一个端口。");
        if (!uint.TryParse(AccountUid, out uint uid) || uid == 0) throw new ArgumentException("本地账号 UID 应为非零正整数。");
        if (string.IsNullOrWhiteSpace(AccountName) || AccountName.Length > 32) throw new ArgumentException("舰长名称需为 1–32 个字符。");
        if (GameExe.Length > 0 && !Path.GetFileName(GameExe).Equals("BH3.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择客户端的 BH3.exe。");
    }
    internal void Save() { Validate(); AppPaths.AtomicWrite(Path.Combine(AppPaths.Data, "settings.json"), JsonSerializer.Serialize(this, Json)); }
    internal LauncherConfig Copy() => (LauncherConfig)MemberwiseClone();
}

internal sealed class AppLog : IDisposable
{
    private readonly object gate = new();
    private readonly List<string> lines = [];
    internal string DirectoryPath { get; }
    internal event Action<string>? Added;
    internal AppLog(string data) { DirectoryPath = Path.Combine(data, "logs"); Directory.CreateDirectory(DirectoryPath); }
    internal void Write(string tag, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  [{tag}] {message.Replace('\r', ' ').Replace('\n', ' ')}";
        lock (gate)
        {
            lines.Add(line); if (lines.Count > 1200) lines.RemoveRange(0, lines.Count - 1200);
            try { File.AppendAllText(Path.Combine(DirectoryPath, $"launcher-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine); } catch (IOException) { }
        }
        Added?.Invoke(line);
    }
    internal string[] Snapshot() { lock (gate) return lines.ToArray(); }
    public void Dispose() { }
}
