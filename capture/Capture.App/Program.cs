using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using Bh3Capture;
namespace CaptureApp;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--inspect" && args[2] == "--output")
        {
            try
            {
                string output = Path.GetFullPath(args[3]);
                if (Directory.Exists(output) || File.Exists(output)) throw new IOException("分析输出目录已存在，请指定新目录。");
                var files = File.Exists(args[1]) ? new[] { args[1] } : Directory.GetFiles(args[1], "capture-*.pcapng").Order().ToArray();
                var report = CaptureInspector.Inspect(files);
                AccountExport.Write(output, report);
                SessionFiles.AtomicJson(Path.Combine(output, "check.json"), report);
                Environment.ExitCode = report.Passed ? 0 : 2;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--probe-state")
        {
            var result = new CommandRunner().Run("logman.exe", ["query", "PktMon", "-ets"]).GetAwaiter().GetResult();
            string? failure = null;
            try { CollectorStatus.RequireIdle(result); } catch (Exception ex) { failure = ex.Message; }
            SessionFiles.AtomicJson(args[1], new { result.ExitCode, Hex = $"0x{unchecked((uint)result.ExitCode):X8}", result.Output, Idle = failure is null, Failure = failure });
            return;
        }
        if (args.Length == 4 && args[0] == "--watchdog")
        { Watchdog(args[1], int.Parse(args[2]), long.Parse(args[3])).GetAwaiter().GetResult(); return; }
        // Apply the preview-only DPI context before WinForms initializes its cached scaling values.
        if (args.Length == 2 && args[0] == "--preview-96") SetThreadDpiAwarenessContext(new IntPtr(-1));
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] is "--preview" or "--preview-96")
        {
            // This thread-only override belongs to offline preview; it does not change display settings.
            if (args[0] == "--preview-96") SetThreadDpiAwarenessContext(new IntPtr(-1));
            using var preview = new MainForm(true);
            preview.Show(); Application.DoEvents();
            if (args[0] == "--preview-96" && preview.DeviceDpi != 96) throw new InvalidOperationException("96-DPI preview context was not applied.");
            Directory.CreateDirectory(args[1]);
            preview.PreviewCaseInteractions();
            File.WriteAllText(Path.Combine(args[1], "使用说明-内置验证.txt"), MainForm.HelpText());
            foreach (var scene in new[] { "Idle", "Capturing", "Recording", "Paused", "Ready", "Recovery", "Converting", "Settings" })
            {
                preview.PreviewScene(scene);
                foreach (var size in new[] { new Size(1100, 870), new Size(1450, 960), new Size(880, 650) })
                {
                    var scale = preview.DeviceDpi / 96F;
                    preview.ClientSize = new Size((int)(size.Width * scale), (int)(size.Height * scale));
                    preview.PerformLayout(); Application.DoEvents();
                    preview.VerifyCaseAlignment();
                    using var bitmap = new Bitmap(preview.Width, preview.Height);
                    preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(args[1], $"capture-{scene}-{size.Width}x{size.Height}.png"));
                }
            }
            File.WriteAllText(Path.Combine(args[1], "界面状态验证.txt"), $"PASS: automatic first segment, live type/name edits, one-click split, continuous boundaries, draft clearing, optional pause/resume, saved-case edit availability; equal control heights/top edges, no overlaps; DPI={preview.DeviceDpi}; idle, capturing, recording, paused, ready, recovery, busy and settings; 24 renders. No real packet capture started.");
            return;
        }
        using var mutex = new Mutex(false, "Local\\BH3Capture-App-v1");
        bool acquired;
        try { acquired = mutex.WaitOne(args.Contains("--elevated") ? 10000 : 0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { MessageBox.Show("抓包助手已打开，请回到原窗口。", "崩坏3 抓包助手"); return; }
        try { Application.Run(new MainForm(startupArgs: args)); }
        finally { mutex.ReleaseMutex(); }
    }
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static async Task Watchdog(string path, int pid, long startedTicks)
    {
        bool ParentAlive()
        {
            try { using var parent = System.Diagnostics.Process.GetProcessById(pid); return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == startedTicks; }
            catch (ArgumentException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
        try
        {
            while (ParentAlive())
            {
                var session = CaptureSession.Load(path);
                if (session.State != "Capturing") return;
                await Task.Delay(1500);
            }
            await CaptureRecovery.StopOrphan(CaptureSession.Load(path), new CommandRunner());
        }
        catch (Exception ex)
        {
            try { await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(path)!, "operations.log"), "[watchdog] " + ex.Message + "\n"); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
    }
}
internal sealed class Settings
{
    public string OutputParent { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BH3抓包");
    public string Version { get; set; } = "崩坏3 9.1.0";
    public string Ports { get; set; } = "16100,21000";
    public bool AllUdp { get; set; } = true;
    public int MaxMB { get; set; } = 4096;
    public string LastSession { get; set; } = "";
    public string DraftName { get; set; } = "";
    public string DraftNotes { get; set; } = "";
    public string DraftType { get; set; } = "其他操作";
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BH3Capture", "settings.json");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save() { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); SessionFiles.AtomicJson(FilePath, this); }
}
