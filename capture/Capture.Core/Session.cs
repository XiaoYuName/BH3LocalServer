using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;
using System.Security.Cryptography;
namespace Bh3Capture;
public sealed class CaptureCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    public double StartOffsetSeconds { get; set; }
    public double? EndOffsetSeconds { get; set; }
    public bool ClosedBySessionEnd { get; set; }
}
public sealed class CaptureSession
{
    public string Format { get; set; } = "BH3Capture.Session";
    public int FormatVersion { get; set; } = 1;
    public string ToolVersion { get; set; } = "1.0.0";
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? EndedUtc { get; set; }
    public string State { get; set; } = "Preparing";
    public string StopReason { get; set; } = "";
    public string ClientVersion { get; set; } = "崩坏3（未填写）";
    public int[] Ports { get; set; } = [];
    public int SegmentMB { get; set; } = 128;
    public int MaxTotalMB { get; set; } = 4096;
    public List<CaptureCase> Cases { get; set; } = [];
    public List<string> PcapFiles { get; set; } = [];
    public CaptureReport? Report { get; set; }
    [JsonIgnore] public string DirectoryPath { get; set; } = "";
    [JsonIgnore] public CaptureCase? OpenCase => Cases.LastOrDefault(c => c.EndedUtc is null);
    public void Save() => SessionFiles.AtomicJson(Path.Combine(DirectoryPath, "session.json"), this);
    public void UpdateCaseDetails(Guid id, string name, string notes, string? type = null)
    {
        var item = Cases.SingleOrDefault(c => c.Id == id) ?? throw new InvalidOperationException("案例不存在。");
        name = CaptureCaseName.Normalize(name);
        var previousName = item.Name; var previousNotes = item.Notes; var previousType = item.Type;
        item.Name = name; item.Notes = notes; if (type is not null) item.Type = type;
        try { Save(); }
        catch { item.Name = previousName; item.Notes = previousNotes; item.Type = previousType; throw; }
    }
    public static CaptureSession Load(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Format", out var format) || format.GetString() != "BH3Capture.Session")
            throw new InvalidDataException("这不是抓包助手的会话文件，请选择本工具生成的session.json。");
        var result = JsonSerializer.Deserialize<CaptureSession>(json, SessionFiles.Json)
            ?? throw new InvalidDataException("会话文件为空。");
        if (result.FormatVersion != 1) throw new InvalidDataException("不支持此会话格式版本。");
        if (result.PcapFiles is null || result.Cases is null) throw new InvalidDataException("会话缺少文件或案例列表。");
        result.DirectoryPath = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (var name in result.PcapFiles) SessionFiles.Resolve(result.DirectoryPath, name);
        return result;
    }
}
public static class CaptureCaseName
{
    public static string Segment(int number, string type) => Normalize($"第 {number} 段" +
        (string.IsNullOrWhiteSpace(type) || type == "其他操作" ? "" : " · " + type));
    public static string NextSegment(CaptureSession? session, string type, string? reservedName = null)
    {
        var number = (session?.Cases.Count ?? 0) + 1;
        while (Segment(number, type) == reservedName || session?.Cases.Any(c => c.Name == Segment(number, type)) == true) number++;
        return Segment(number, type);
    }
    public static string Normalize(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("案例名称须为1～100个字符。");
        return name;
    }
    public static string Next(CaptureSession? session, string type, string? reservedName = null)
    {
        var prefix = type + " · 第 ";
        var names = session?.Cases.Select(c => c.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        if (reservedName is not null) names.Add(reservedName);
        var number = 1 + (session?.Cases.Count(c => c.Type == type || c.Name.StartsWith(prefix, StringComparison.Ordinal)) ?? 0);
        while (names.Contains($"{prefix}{number} 次")) number++;
        return Normalize($"{prefix}{number} 次");
    }
}
public static class SessionFiles
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string NewSharePath(CaptureSession session, string directory)
    {
        Directory.CreateDirectory(directory);
        var stem = "崩坏3-capture-" + session.StartedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(directory, stem + ".zip");
        for (var suffix = 2; File.Exists(path) || Directory.Exists(path); suffix++)
            path = Path.Combine(directory, $"{stem}-{suffix}.zip");
        return path;
    }
    public static void AtomicJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    public static string Resolve(string root, string name)
    {
        if (Path.IsPathRooted(name) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("无效的文件引用。");
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件引用越出会话目录。");
        return full;
    }
    public static void Export(CaptureSession session, string destination, Action<string>? progress = null)
    {
        if (session.State is not ("Ready" or "NeedsAttention")) throw new InvalidOperationException("先结束采集并转换，再导出分享包。");
        if (File.Exists(destination)) throw new IOException("目标文件已存在，请选择新名称。");
        var files = new List<string> { "session.json" };
        files.AddRange(session.PcapFiles);
        foreach (var name in new[] { "account-copy.json", "account-summary.txt" })
            if (File.Exists(Path.Combine(session.DirectoryPath, name))) files.Add(name);
        if (File.Exists(Path.Combine(session.DirectoryPath, "check.json"))) files.Add("check.json");
        if (File.Exists(Path.Combine(session.DirectoryPath, "check.txt"))) files.Add("check.txt");
        if (session.PcapFiles.Count == 0) throw new InvalidOperationException("没有已转换的抓包文件。");
        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var hashes = new Dictionary<string, string>();
                foreach (var name in files.Distinct())
                {
                    progress?.Invoke("打包 " + name);
                    var source = Resolve(session.DirectoryPath, name);
                    using var input = File.OpenRead(source);
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using var output = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
                    var buffer = new byte[1024 * 1024];
                    int count;
                    while ((count = input.Read(buffer)) > 0) { digest.AppendData(buffer.AsSpan(0, count)); output.Write(buffer, 0, count); }
                    hashes[name] = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
                }
                using (var writer = new StreamWriter(zip.CreateEntry("SHA256.json").Open()))
                    writer.Write(JsonSerializer.Serialize(hashes, Json));
                using (var writer = new StreamWriter(zip.CreateEntry("打开说明.txt").Open()))
                    writer.Write("解压到新目录，用抓包助手的“历史记录”（旧版为“打开会话”）选择 session.json。\n所有案例共享完整登录上下文，不要只发送最后一个 pcapng 分卷。\n案例起止时间和相对秒数见 session.json；本版本提供案例索引，不含协议浏览器。\n原始网络数据可能包含账号或会话信息，请仅分享给需要分析的开发者。\n检查范围为崩坏3游戏首包、双向UDP连续性，不等同于全部协议解密成功。\n");
                using (var writer = new StreamWriter(zip.CreateEntry("案例索引.txt").Open()))
                {
                    writer.WriteLine("以下过滤表达式用于定位案例。分析与解密仍需读取完整上下文，不能按此过滤后删除前置流量。\n");
                    foreach (var item in session.Cases)
                    {
                        var from = (item.StartedUtc - DateTimeOffset.UnixEpoch).TotalSeconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                        writer.WriteLine($"{item.Name}\n开始UTC：{item.StartedUtc:O}\n结束UTC：{item.EndedUtc:O}\n备注：{item.Notes}");
                        if (item.EndedUtc is { } ended)
                        {
                            var to = (ended - DateTimeOffset.UnixEpoch).TotalSeconds.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
                            writer.WriteLine($"Wireshark显示过滤：frame.time_epoch >= {from} && frame.time_epoch <= {to}\n");
                        }
                        else writer.WriteLine("结束时间未知，案例未完成。\n");
                    }
                }
            }
            File.Move(partial, destination);
        }
        catch { if (File.Exists(partial)) File.Delete(partial); throw; }
    }
}
