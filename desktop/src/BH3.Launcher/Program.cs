namespace BH3.Launcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTests.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--bundle-test")) return BundleTests.Run(args).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\BH3.NativeLauncher.v1", out bool created);
        if (!created) { MessageBox.Show("登录器已经在运行，请从任务栏打开。", "崩坏 3"); return 0; }
        AppPaths.Initialize(args);
        using var log = new AppLog(AppPaths.Data);
        try
        {
            Application.ThreadException += (_, e) => { log.Write("错误", e.Exception.Message); MessageBox.Show(e.Exception.Message, "操作未完成"); };
            var config = LauncherConfig.Load();
            using var engine = new LauncherEngine(config, log);
            Application.Run(new MainForm(engine));
            return 0;
        }
        catch (Exception ex) { log.Write("错误", ex.ToString()); MessageBox.Show(ex.Message, "登录器启动失败"); return 1; }
    }
}
