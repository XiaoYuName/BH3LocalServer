using System.Diagnostics;
using System.Text;
namespace Bh3Capture;
public record CommandResult(int ExitCode, string Output);
public interface ICommandRunner
{
    Task<CommandResult> Run(string executable, IReadOnlyList<string> arguments);
}
public sealed class CommandRunner : ICommandRunner
{
    public async Task<CommandResult> Run(string executable, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法启动 " + executable);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var readOut = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var readError = process.StandardError.BaseStream.CopyToAsync(stderr);
        await process.WaitForExitAsync();
        await Task.WhenAll(readOut, readError);
        return new(process.ExitCode, WindowsCommandText.Decode(stdout.ToArray()) + WindowsCommandText.Decode(stderr.ToArray()));
    }
}
public sealed class CaptureController(ICommandRunner runner)
{
    public CaptureSession? Session { get; private set; }
    public bool OwnsCapture { get; private set; }
    public event Action<string>? Progress;
    private readonly Stopwatch elapsed = new();
    private async Task<CommandResult> Invoke(string executable, params string[] args)
    {
        var result = await runner.Run(executable, args);
        if (Session is not null)
        {
            try
            {
                await File.AppendAllTextAsync(Path.Combine(Session.DirectoryPath, "operations.log"),
                    $"[{DateTimeOffset.UtcNow:O}] {executable} {string.Join(' ', args)}\n{result.Output}\n", Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Progress?.Invoke("运行日志写入失败：" + ex.Message); }
        }
        return result;
    }
    private async Task RequireIdle()
    {
        var state = await Invoke("logman.exe", "query", "PktMon", "-ets");
        CollectorStatus.RequireIdle(state);
    }
    public async Task Start(string parent, int[] ports, int maxMB, string clientVersion, bool recordFromStart = true)
    {
        if (OwnsCapture) throw new InvalidOperationException("采集已经开始。");
        if (maxMB < 128 || maxMB > 65536) throw new ArgumentOutOfRangeException(nameof(maxMB));
        if (ports.Length > 32 || ports.Any(p => p < 1 || p > 65535)) throw new ArgumentException("端口须为1～65535，最多32个。");
        await RequireIdle();
        var directory = Path.Combine(Path.GetFullPath(parent), "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        Session = new() { DirectoryPath = directory, StartedUtc = DateTimeOffset.UtcNow, Ports = ports.Distinct().ToArray(), MaxTotalMB = maxMB, ClientVersion = clientVersion };
        Session.Save();
        try
        {
            // Matches the verified project capture plan. Never stop a pre-existing collector.
            await Must("filter", "remove");
            if (ports.Length == 0) await Must("filter", "add", "BH3Capture-UDP", "-t", "UDP");
            else foreach (var port in Session.Ports) await Must("filter", "add", "BH3Capture-" + port, "-t", "UDP", "-p", port.ToString());
            await Must("start", "--capture", "--comp", "all", "--pkt-size", "0", "--file-name", Path.Combine(directory, "capture.etl"), "--file-size", "128", "--log-mode", "multi-file");
            OwnsCapture = true;
            Session.StartedUtc = DateTimeOffset.UtcNow;
            elapsed.Restart();
            Session.State = "Capturing";
            if (recordFromStart)
                Session.Cases.Add(new() { Name = CaptureCaseName.NextSegment(Session, "其他操作"), Type = "其他操作",
                    StartedUtc = Session.StartedUtc, StartOffsetSeconds = 0 });
            Session.Save();
            Progress?.Invoke("采集已启动。现在可以启动游戏并登录。");
        }
        catch { Session.State = OwnsCapture ? "Capturing" : "StartFailed"; Session.Save(); throw; }
    }
    private async Task Must(params string[] args)
    {
        var result = await Invoke("pktmon.exe", args);
        if (result.ExitCode != 0) throw new IOException($"pktmon {args[0]} 失败（{result.ExitCode}）。\n{result.Output}");
    }
    public void BeginCase(string name, string notes, string type = "")
    {
        var session = Session ?? throw new InvalidOperationException("先开始采集。");
        if (!OwnsCapture || session.State != "Capturing") throw new InvalidOperationException("当前没有持续采集。");
        if (session.OpenCase is not null) throw new InvalidOperationException("先保存当前案例。");
        name = CaptureCaseName.Normalize(name);
        var item = new CaptureCase { Name = name, Type = type, Notes = notes, StartedUtc = DateTimeOffset.UtcNow, StartOffsetSeconds = elapsed.Elapsed.TotalSeconds };
        session.Cases.Add(item);
        try { session.Save(); } catch { session.Cases.Remove(item); throw; }
    }
    public void SaveCase(string notes) => CompleteCase(null, notes, null, null);
    public void SaveCase(string name, string notes, string? type = null) => CompleteCase(name, notes, null, type);
    public void SaveCaseAndBeginNext(string name, string notes, string nextName, string? type = null) =>
        CompleteCase(name, notes, new CaptureCase { Name = CaptureCaseName.Normalize(nextName) }, type);
    private void CompleteCase(string? name, string notes, CaptureCase? next, string? type)
    {
        var session = Session ?? throw new InvalidOperationException("先开始采集。");
        if (!OwnsCapture || session.State != "Capturing") throw new InvalidOperationException("当前没有持续采集。");
        var item = session.OpenCase ?? throw new InvalidOperationException("没有进行中的案例。");
        name = name is null ? item.Name : CaptureCaseName.Normalize(name);
        var previousName = item.Name; var previousNotes = item.Notes; var previousType = item.Type;
        var boundary = DateTimeOffset.UtcNow; var offset = elapsed.Elapsed.TotalSeconds;
        item.Name = name;
        item.Notes = notes;
        if (type is not null) item.Type = type;
        item.EndedUtc = boundary;
        item.EndOffsetSeconds = offset;
        if (next is not null)
        {
            next.Type = item.Type;
            next.StartedUtc = boundary; next.StartOffsetSeconds = offset;
            session.Cases.Add(next);
        }
        // Closing and opening the next marker is one disk commit; a failed write leaves the current case open.
        try { session.Save(); }
        catch
        {
            if (next is not null) session.Cases.Remove(next);
            item.Name = previousName; item.Notes = previousNotes; item.Type = previousType; item.EndedUtc = null; item.EndOffsetSeconds = null;
            throw;
        }
        Progress?.Invoke(next is null ? "本段已保存，后台继续抓包。准备好下一次操作时可手动开始新段。" : "本段已保存，下一段已开始记录，后台抓包持续进行。");
    }
    public long CapturedBytes => Session is null ? 0 : Directory.EnumerateFiles(Session.DirectoryPath, "*.etl").Sum(p => new FileInfo(p).Length);
    public TimeSpan Elapsed => elapsed.Elapsed;
    public async Task Stop(string reason)
    {
        if (!OwnsCapture || Session is null) return;
        // An external restart must not give us authority to stop another collector.
        var status = await Invoke("pktmon.exe", "status");
        if (status.ExitCode != 0 || !CaptureRecovery.StatusBelongsTo(status.Output, Session))
            throw new IOException("无法确认采集仍属于本会话，未停止其他采集。原始数据已保留；请检查 pktmon 状态。\n" + status.Output);
        Progress?.Invoke("正在停止采集并写入分卷…");
        await Must("stop");
        OwnsCapture = false;
        elapsed.Stop();
        Session.EndedUtc = DateTimeOffset.UtcNow;
        Session.StopReason = reason;
        if (Session.OpenCase is { } open)
        {
            open.EndedUtc = Session.EndedUtc;
            open.EndOffsetSeconds = elapsed.Elapsed.TotalSeconds;
            open.ClosedBySessionEnd = true;
        }
        Session.State = "Stopped";
        Session.Save();
        await ConvertAndCheck(Session);
    }
    public async Task ConvertAndCheck(CaptureSession session)
    {
        if (OwnsCapture) throw new InvalidOperationException("仍在采集，不能转换。");
        await RequireIdle();
        Session = session;
        if (session.State == "Capturing" || session.State == "Preparing")
        {
            session.StopReason = "Interrupted";
            session.EndedUtc = null; // Never manufacture the unknown end of an interrupted capture.
        }
        session.State = "Converting";
        session.Save();
        try
        {
            var segments = Directory.GetFiles(session.DirectoryPath, "*.etl")
                .OrderBy(p => File.GetLastWriteTimeUtc(p)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            if (segments.Length == 0) throw new IOException("没有原始ETL分卷；请保留目录并检查启动日志。");
            session.PcapFiles.Clear();
            for (var i = 0; i < segments.Length; i++)
            {
                Progress?.Invoke($"转换分卷 {i + 1}/{segments.Length}…");
                var name = $"capture-{i:D4}.pcapng";
                var destination = Path.Combine(session.DirectoryPath, name);
                var temporary = Path.Combine(session.DirectoryPath, $"convert-{Guid.NewGuid():N}.pcapng");
                try
                {
                    await Must("etl2pcap", segments[i], "--out", temporary);
                    if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("转换未生成有效文件。");
                    File.Move(temporary, destination, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                session.PcapFiles.Add(name);
                session.Save();
            }
            Progress?.Invoke("重组KCP并导出账号快照…");
            session.Report = await Task.Run(() => CaptureInspector.Inspect(session.PcapFiles.Select(p => SessionFiles.Resolve(session.DirectoryPath, p))));
            await Task.Run(() => AccountExport.Write(session.DirectoryPath, session.Report));
            SessionFiles.AtomicJson(Path.Combine(session.DirectoryPath, "check.json"), session.Report);
            await File.WriteAllTextAsync(Path.Combine(session.DirectoryPath, "check.txt"), session.Report.ToText(), Encoding.UTF8);
            session.State = session.Report.Passed && session.StopReason is not ("Interrupted" or "CapacityLimit") ? "Ready" : "NeedsAttention";
            session.Save();
            Progress?.Invoke(session.State == "Ready" ? "转换与基础检查通过，可以导出。" : "转换完成，有完整性提示。可导出并保留问题说明。");
        }
        catch { session.State = "ConversionFailed"; session.Save(); throw; }
    }
}
