using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Bh3Capture;

if (args.Length == 2 && args[0] == "--inspect") {
    var report = CaptureInspector.Inspect(File.Exists(args[1]) ? [args[1]] : Directory.GetFiles(args[1], "capture-*.pcapng").Order());
    Console.WriteLine(report.ToText());
    if (Directory.Exists(args[1])) AccountExport.Write(args[1], report);
    return report.Passed ? 0 : 2;
}
var sandbox = Path.Combine(Path.GetTempPath(), "BH3Capture-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
var passed = 0;
void Assert(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
try {
    var good = ProtocolChecks.Run(sandbox, Assert);
    var runner = new FakeRunner { Fixture = good };
    var engine = new CaptureController(runner);
    await engine.Start(sandbox, [], 128, "测试版本", recordFromStart: false);
    runner.Directory = engine.Session!.DirectoryPath;
    File.WriteAllBytes(Path.Combine(runner.Directory, "capture.etl"), [1, 2, 3]);
    engine.BeginCase("团本一", "入场"); engine.SaveCase("通关"); engine.BeginCase("团本二", "换频道"); engine.SaveCase("第二个通关");
    Assert(engine.OwnsCapture && runner.Commands.Count(x => x.Contains("pktmon.exe stop")) == 0, "保存两个案例不停止采集");
    Assert(CaptureSession.Load(Path.Combine(runner.Directory, "session.json")).Cases.Count == 2, "案例立即落盘，重开可读取");
    engine.BeginCase("第三个未手动保存", "仍在操作");
    await engine.Stop("User");
    Assert(!engine.OwnsCapture && engine.Session.State == "Ready" && runner.Commands.Count(x => x == "pktmon.exe stop") == 1, "结束采集只停止一次并转换检查");
    Assert(engine.Session.Cases[2].ClosedBySessionEnd && engine.Session.Cases[2].EndedUtc is not null, "结束时标记未手动保存的案例");
    Assert(File.Exists(Path.Combine(engine.Session.DirectoryPath, "account-copy.json")), "结束采集自动写出账号副本");
    Assert(runner.Commands.Any(x => x.Contains("-t UDP")) && !runner.Commands.Any(x => x.Contains("-t TCP")), "采集过滤器使用BH3 UDP");
    Assert(runner.Commands.Any(x => x.Contains("--comp all --pkt-size 0")) && runner.Commands.Any(x => x.EndsWith("multi-file")), "完整包长和全部组件非循环分卷");
    var shares = Path.Combine(sandbox, "分享包");
    var zipPath = SessionFiles.NewSharePath(engine.Session, shares); SessionFiles.Export(engine.Session, zipPath);
    Assert(File.Exists(zipPath) && Path.GetDirectoryName(zipPath) == shares && Path.GetFileName(zipPath).StartsWith("崩坏3-capture-"), "结束后的默认分享目录与文件名可直接导出");
    var nextShare = SessionFiles.NewSharePath(engine.Session, shares);
    Assert(nextShare != zipPath && Path.GetFileNameWithoutExtension(nextShare).EndsWith("-2"), "再次生成分享包自动改名，不覆盖旧ZIP");
    Directory.CreateDirectory(nextShare);
    Assert(Path.GetFileNameWithoutExtension(SessionFiles.NewSharePath(engine.Session, shares)).EndsWith("-3"), "分享文件名也避开同名目录");
    using (var zip = ZipFile.OpenRead(zipPath))
    {
        Assert(zip.Entries.Count(e => e.Name.EndsWith(".pcapng")) == 1 && zip.GetEntry("session.json") is not null, "多个案例仅打包一份原始上下文");
        Assert(zip.GetEntry("account-copy.json") is not null && zip.GetEntry("account-summary.txt") is not null, "分享包包含账号副本和缺项摘要");
        using var hashesInput = zip.GetEntry("SHA256.json")!.Open(); var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(hashesInput)!;
        Assert(hashes.All(pair => { using var source = zip.GetEntry(pair.Key)!.Open(); return pair.Value == Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(); }), "分享包每个数据文件SHA256可复核");
    }
    var extracted = Path.Combine(sandbox, "extracted"); ZipFile.ExtractToDirectory(zipPath, extracted);
    var portable = CaptureSession.Load(Path.Combine(extracted, "session.json"));
    Assert(portable.Cases.Count == 3 && CaptureInspector.Inspect(portable.PcapFiles.Select(p => SessionFiles.Resolve(extracted, p))).Passed, "分享包解压后独立加载与检查");
    bool rejected = false; try { SessionFiles.Export(engine.Session, zipPath); } catch (IOException) { rejected = true; }
    Assert(rejected, "导出不覆盖已有文件");
    rejected = false; try { SessionFiles.Resolve(sandbox, "../escape.pcapng"); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "拒绝会话文件路径越界");
    Assert(!CaptureRecovery.StatusBelongsTo(engine.Session.DirectoryPath + "-other\\capture.etl", engine.Session), "采集归属路径不能只匹配目录前缀");
    Assert(CaptureRecovery.StatusBelongsTo(Path.Combine(engine.Session.DirectoryPath, "capture_00002.etl"), engine.Session), "采集归属支持多文件模式的活动分卷名");
    var invalidSession = Path.Combine(sandbox, "old-session.json"); File.WriteAllText(invalidSession, "{\"pid\":1}");
    rejected = false; try { CaptureSession.Load(invalidSession); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "拒绝其他工具的同名session.json");
    var active = new FakeRunner { BusyCollector = true };
    rejected = false; try { await new CaptureController(active).Start(sandbox, [], 128, "test"); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected && active.Commands.Count == 1, "已有pktmon采集时不重置过滤器、不停止它");
    var failing = new FakeRunner { Fixture = good, FailConversion = true }; var interrupted = new CaptureController(failing);
    await interrupted.Start(sandbox, [], 128, "test"); failing.Directory = interrupted.Session!.DirectoryPath;
    var rawPath = Path.Combine(failing.Directory, "capture.etl"); File.WriteAllBytes(rawPath, [1, 2]);
    rejected = false; try { await interrupted.Stop("User"); } catch (IOException) { rejected = true; }
    Assert(rejected && File.Exists(rawPath) && interrupted.Session.State == "ConversionFailed" && !interrupted.OwnsCapture, "转换失败保留ETL与可恢复状态");
    failing.FailConversion = false; await interrupted.ConvertAndCheck(interrupted.Session);
    Assert(interrupted.Session.State == "Ready", "失败后重试转换成功");
    var capacityRunner = new FakeRunner { Fixture = good }; var capacityEngine = new CaptureController(capacityRunner);
    await capacityEngine.Start(sandbox, [11661], 128, "test"); capacityRunner.Directory = capacityEngine.Session!.DirectoryPath;
    File.WriteAllBytes(Path.Combine(capacityRunner.Directory, "capture.etl"), [1]); await capacityEngine.Stop("CapacityLimit");
    Assert(capacityEngine.Session.State == "NeedsAttention", "容量自动停止不把业务样本认定完整");
    var orphanRunner = new FakeRunner { BusyCollector = true, Directory = capacityEngine.Session.DirectoryPath };
    capacityEngine.Session.State = "Capturing";
    Assert(await CaptureRecovery.StopOrphan(capacityEngine.Session, orphanRunner) && capacityEngine.Session.StopReason == "Interrupted", "异常退出只停止自己拥有的采集并保留恢复状态");
    capacityEngine.Session.State = "Capturing";
    orphanRunner.Directory = "other-capture";
    var previousStopCount = orphanRunner.Commands.Count(x => x == "pktmon.exe stop");
    Assert(!await CaptureRecovery.StopOrphan(capacityEngine.Session, orphanRunner) && orphanRunner.Commands.Count(x => x == "pktmon.exe stop") == previousStopCount, "异常退出监护不停止其他会话");
    var caseRunner = new FakeRunner { Fixture = good }; var caseEngine = new CaptureController(caseRunner);
    await caseEngine.Start(sandbox, [], 128, "case workflow", recordFromStart: false);
    var caseSession = caseEngine.Session!; caseRunner.Directory = caseSession.DirectoryPath;
    File.WriteAllBytes(Path.Combine(caseRunner.Directory, "capture.etl"), [1]);
    caseEngine.BeginCase(CaptureCaseName.Next(caseSession, "女武神"), "开始", "女武神");
    var first = caseSession.OpenCase!; var firstId = first.Id; var startTime = first.StartedUtc; var startOffset = first.StartOffsetSeconds;
    // Reserving a name being edited must not give the next record that same name.
    var reserved = CaptureCaseName.Next(caseSession, "女武神", "女武神 · 第 2 次");
    Assert(reserved == "女武神 · 第 3 次", "自动编号避开本次修改中的名称");
    caseEngine.SaveCaseAndBeginNext("女武神一阶", "通关", reserved);
    var second = caseSession.OpenCase!;
    Assert(first.Name == "女武神一阶" && first.Notes == "通关" && first.Id == firstId && first.StartedUtc == startTime && first.StartOffsetSeconds == startOffset,
        "保存时修改名称备注不改变案例身份和开始边界");
    Assert(first.EndedUtc == second.StartedUtc && first.EndOffsetSeconds == second.StartOffsetSeconds && second.Notes == "" && second.Type == "女武神",
        "连续记录一次提交且边界相接，下一次备注为空");
    var casePath = Path.Combine(caseRunner.Directory, "session.json");
    var saved = CaptureSession.Load(casePath);
    Assert(saved.Cases.Count == 2 && saved.OpenCase?.Id == second.Id && caseRunner.Commands.All(c => c != "pktmon.exe stop"),
        "保存并开始下一次立即落盘且不停止采集");
    var blocker = casePath + ".tmp"; Directory.CreateDirectory(blocker);
    rejected = false;
    try { caseEngine.SaveCaseAndBeginNext("不应落盘", "不应更新", "不应新建", "装备 / 圣痕"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
    Assert(rejected && caseSession.Cases.Count == 2 && caseSession.OpenCase == second && second.Name == reserved && second.Notes == "" && second.Type == "女武神" && second.EndOffsetSeconds is null,
        "连续保存写盘失败时保留当前记录，回滚名称与下一次标记");
    rejected = false;
    try { caseSession.UpdateCaseDetails(firstId, "不应改名", "不应修改备注", "装备 / 圣痕"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
    Assert(rejected && first.Name == "女武神一阶" && first.Notes == "通关" && first.Type == "女武神" && CaptureSession.Load(casePath).Cases[0].Name == "女武神一阶",
        "修改已保存案例写盘失败时内存和原文件保持一致");
    Directory.Delete(blocker);
    caseSession.UpdateCaseDetails(firstId, "女武神1阶 · 完整通关", "补充备注");
    Assert(first.StartedUtc == startTime && first.StartOffsetSeconds == startOffset && first.EndedUtc == second.StartedUtc && CaptureSession.Load(casePath).Cases[0].Notes == "补充备注",
        "补改已保存案例只修改名称备注，时间范围与落盘一致");
    caseEngine.SaveCase("第二次", "结束");
    Assert(caseSession.OpenCase is null && caseEngine.OwnsCapture, "单独保存后等待下一次，后台继续采集");
    await caseEngine.Stop("User");
    var revisedZip = Path.Combine(sandbox, "revised-cases.zip"); SessionFiles.Export(caseSession, revisedZip);
    using (var zip = ZipFile.OpenRead(revisedZip))
    using (var input = zip.GetEntry("session.json")!.Open())
    using (var indexReader = new StreamReader(zip.GetEntry("案例索引.txt")!.Open()))
    {
        var sharedSession = JsonSerializer.Deserialize<CaptureSession>(input)!; var index = indexReader.ReadToEnd();
        Assert(sharedSession.Cases[0].Name == "女武神1阶 · 完整通关" && index.Contains("补充备注") && sharedSession.Cases[1].Name == "第二次",
            "分享包和案例索引携带修改后的名称备注");
    }
    var autoRunner = new FakeRunner { Fixture = good }; var autoEngine = new CaptureController(autoRunner);
    await autoEngine.Start(sandbox, [], 128, "automatic segments");
    var autoSession = autoEngine.Session!; autoRunner.Directory = autoSession.DirectoryPath;
    File.WriteAllBytes(Path.Combine(autoRunner.Directory, "capture.etl"), [1]);
    var autoFirst = autoSession.OpenCase!;
    Assert(autoSession.Cases.Count == 1 && autoFirst.Name == "第 1 段" && autoFirst.StartedUtc == autoSession.StartedUtc && autoFirst.StartOffsetSeconds == 0,
        "开始抓包自动创建第一段，包含登录前的完整起点");
    var autoReloaded = CaptureSession.Load(Path.Combine(autoRunner.Directory, "session.json"));
    Assert(autoReloaded.OpenCase?.Id == autoFirst.Id && autoEngine.OwnsCapture, "自动第一段在启动成功时立即落盘");
    autoSession.UpdateCaseDetails(autoFirst.Id, "第 1 段 · 女武神", "准备通关", "女武神");
    autoEngine.SaveCaseAndBeginNext("第一次女武神", "通关", CaptureCaseName.NextSegment(autoSession, "女武神"), "女武神");
    var autoSecond = autoSession.OpenCase!;
    Assert(autoSecond.Name == "第 2 段 · 女武神" && autoFirst.EndedUtc == autoSecond.StartedUtc && autoFirst.EndOffsetSeconds == autoSecond.StartOffsetSeconds,
        "改名后仍按全局段号继续，分段边界无缺口");
    autoEngine.SaveCaseAndBeginNext("商店买材料", "购买成功", CaptureCaseName.NextSegment(autoSession, "装备 / 圣痕"), "装备 / 圣痕");
    Assert(autoSecond.Type == "装备 / 圣痕" && autoSession.OpenCase!.Type == "装备 / 圣痕" && autoSession.OpenCase.Name == "第 3 段 · 装备 / 圣痕" && autoRunner.Commands.All(c => c != "pktmon.exe stop"),
        "记录中可修改类型，连续分段不重启或停止网络采集");
    await autoEngine.Stop("User");
    Assert(autoSession.Cases.All(c => c.EndedUtc is not null) && autoSession.Cases[^1].ClosedBySessionEnd && autoSession.Cases[^1].EndedUtc == autoSession.EndedUtc,
        "最后一段无需再点完成，结束抓包时自动保存");
    var unmarkedRunner = new FakeRunner { Fixture = good }; var unmarkedEngine = new CaptureController(unmarkedRunner);
    await unmarkedEngine.Start(sandbox, [], 128, "no manual markers");
    unmarkedRunner.Directory = unmarkedEngine.Session!.DirectoryPath;
    File.WriteAllBytes(Path.Combine(unmarkedRunner.Directory, "capture.etl"), [1]); await unmarkedEngine.Stop("User");
    Assert(unmarkedEngine.Session.Cases.Count == 1 && unmarkedEngine.Session.Cases[0].StartOffsetSeconds == 0 && unmarkedEngine.Session.Cases[0].EndedUtc == unmarkedEngine.Session.EndedUtc,
        "全程不操作分段按钮也保留一段完整采集");
    var failedStartRunner = new FakeRunner { FailStart = true }; var failedStartEngine = new CaptureController(failedStartRunner);
    rejected = false; try { await failedStartEngine.Start(sandbox, [], 128, "start failure"); } catch (IOException) { rejected = true; }
    Assert(rejected && !failedStartEngine.OwnsCapture && failedStartEngine.Session!.State == "StartFailed" && failedStartEngine.Session.Cases.Count == 0,
        "抓包启动失败不会显示已经在记录的虚假分段");
    Console.WriteLine($"\n{passed} checks passed. No real packet capture started.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
finally { Directory.Delete(sandbox, true); }

sealed class FakeRunner : ICommandRunner
{
    public List<string> Commands { get; } = [];
    public bool BusyCollector, FailConversion, FailStart;
    public string Directory = "", Fixture = "";
    public Task<CommandResult> Run(string exe, IReadOnlyList<string> args)
    {
        Commands.Add(exe + " " + string.Join(' ', args));
        if (exe == "logman.exe") return Task.FromResult(new CommandResult(BusyCollector ? 0 : CollectorStatus.NotFound, "collector state"));
        if (args[0] == "status") return Task.FromResult(new CommandResult(0, "Log file: " + Directory + "\\capture.etl"));
        if (args[0] == "start" && FailStart) return Task.FromResult(new CommandResult(1, "start failed"));
        if (args[0] == "etl2pcap")
        {
            if (FailConversion) return Task.FromResult(new CommandResult(1, "conversion failed"));
            File.Copy(Fixture, args[^1]);
        }
        return Task.FromResult(new CommandResult(0, "ok"));
    }
}
