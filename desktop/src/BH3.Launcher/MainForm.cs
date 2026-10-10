using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace BH3.Launcher;

internal sealed class MainForm : Form
{
    private readonly LauncherEngine engine;
    private readonly Panel content = new() { Dock = DockStyle.Fill, Padding = new(20, 18, 20, 16) };
    private readonly Dictionary<string, Control> pages = [];
    private readonly Dictionary<string, ActionButton> navigation = [];
    private readonly List<Control> operations = [];
    private readonly HeroPanel hero = new() { Dock = DockStyle.Fill, Margin = new(0, 0, 18, 0) };
    private readonly TextBox gamePath, captain, uid, wallpaper, serverPath;
    private readonly NumericUpDown httpPort, proxyPort, gamePort;
    private readonly CheckBox useProxy, useWinHttp, handshake;
    private readonly Label clientState = Theme.Label("正在检查", 9), certState = Theme.Label("正在检查", 9), serviceState = Theme.Label("未启动", 9), routeState = Theme.Label("未接管", 9);
    private readonly Label notice = Theme.Label("点击启动服务，自动运行随包服务端。", 9, Theme.Muted);
    private readonly Label footer = Theme.Label("就绪", 9, Theme.Muted);
    private readonly RichTextBox logs = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Theme.Background, ForeColor = Theme.Muted, BorderStyle = BorderStyle.None, Font = Theme.Font(9), DetectUrls = false, WordWrap = true };
    private readonly ActionButton start, stop, launch, install, save;
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 1500 };
    private bool busy, closing, canClose, certificateInstalled;
    private GmWindow? gmWindow;
    internal MainForm(LauncherEngine engine)
    {
        this.engine = engine;
        Text = "崩坏 3 · 本地登录器"; Name = "BH3DesktopLauncher"; BackColor = Theme.Background; ForeColor = Theme.Text; Font = Theme.Font();
        AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; ClientSize = new(1180, 746);
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("BH3.Launcher.Resources.launcher.ico")) if (stream is not null) Icon = new Icon(stream);
        var cfg = engine.Config;
        gamePath = Theme.Input(cfg.GameExe, true); gamePath.AccessibleName = "客户端路径";
        captain = Theme.Input(cfg.AccountName); captain.AccessibleName = "舰长名称"; captain.MaxLength = 32;
        uid = Theme.Input(cfg.AccountUid); uid.AccessibleName = "本地 UID"; uid.MaxLength = 10;
        wallpaper = Theme.Input(cfg.Wallpaper, true); wallpaper.AccessibleName = "首页背景";
        serverPath = Theme.Input(cfg.ServerExe, true); serverPath.AccessibleName = "自定义服务端路径";
        httpPort = Number(cfg.HttpPort, "接口端口"); proxyPort = Number(cfg.ProxyPort, "代理端口"); gamePort = Number(cfg.GamePort, "游戏端口");
        useProxy = Check("启动游戏时接管系统代理，退出后还原", cfg.UseSystemProxy);
        useWinHttp = Check("同时接管 WinHTTP（需要管理员权限）", cfg.UseWinHttp);
        handshake = Check("启用本地 UDP 握手入口", cfg.EnableHandshake);
        start = Button("启动服务", () => Run(async () => { Commit(); await engine.Start(); }, "正在启动本地服务…"));
        stop = Button("停止服务", () => Run(engine.Stop, "正在停止服务并还原代理…"));
        launch = Button("启动游戏   →", () => Run(async () => { if (!engine.Running) Commit(); await engine.Launch(); }, "正在准备客户端…"), true);
        install = Button("安装本地证书", () => Run(async () => { await Task.Run(engine.Certificates.Install); certificateInstalled = true; engine.Log.Write("证书", "当前用户证书已安装并核对指纹。"); }, "等待 Windows 证书确认…"));
        save = Button("保存设置", () => Run(() => { Commit(); engine.Log.Write("设置", "设置已保存。"); return Task.CompletedTask; }, "正在保存设置…"), true);
        BuildShell(); BuildHome(); BuildLogs(); BuildSettings(); BuildAbout(); ShowPage("开始游戏");
        // These controls are created in code after WinForms' initial DPI pass.
        // Scale all page layouts once, then let per-monitor DPI handle later moves.
        float initialScale = DeviceDpi / 96f;
        if (initialScale != 1)
        {
            var factor = new SizeF(initialScale, initialScale);
            Scale(factor);
            foreach (var page in pages.Values.Where(p => p.Parent is null)) page.Scale(factor);
            ClientSize = new((int)(1180 * initialScale), (int)(746 * initialScale));
        }
        AutoScaleDimensions = new(DeviceDpi, DeviceDpi); AutoScaleMode = AutoScaleMode.Dpi;
        LoadArt(); foreach (var line in engine.Log.Snapshot()) AppendLog(line);
        engine.Log.Added += OnLog;
        Shown += async (_, _) =>
        {
            Theme.DarkTitle(this);
            var screen = Screen.FromControl(this).WorkingArea;
            if (Height > screen.Height - 20) Height = Math.Max(560, screen.Height - 20);
            if (Width > screen.Width - 20) Width = Math.Max(900, screen.Width - 20);
            try { certificateInstalled = await Task.Run(() => engine.Certificates.Installed); }
            catch (Exception ex) { engine.Log.Write("证书", ex.Message); }
            UpdateState();
            engine.Log.Write("界面", $"DPI {DeviceDpi} · 客户区 {ClientSize.Width}×{ClientSize.Height} · 输入高度 {captain.Height} · 字体 {captain.Font.SizeInPoints}");
        };
        poll.Tick += (_, _) => UpdateState(); poll.Start();
        FormClosing += OnClosing;
    }
    private void BuildShell()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        shell.RowStyles.Add(new(SizeType.Absolute, 76)); shell.RowStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.Absolute, 38));
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new(22, 12, 22, 12), BackColor = Theme.Surface, Margin = Padding.Empty };
        top.RowCount = 1; top.RowStyles.Add(new(SizeType.Percent, 100));
        top.ColumnStyles.Add(new(SizeType.Absolute, 242)); top.ColumnStyles.Add(new(SizeType.Percent, 100));
        top.Controls.Add(Theme.Label("BH3  /  本地登录器", 15, Theme.Accent, true).withSizeDock(), 0, 0);
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new(0, 5, 0, 0) };
        foreach (var name in new[] { "开始游戏", "运行记录", "启动设置", "版本信息" })
        {
            var b = new ActionButton(name) { Width = 125, Height = 38, Margin = new(0, 0, 8, 0) }; b.Click += (_, _) => ShowPage(name); nav.Controls.Add(b); navigation[name] = b;
        }
        nav.Controls.Add(Button("GM 工具",()=>Run(async()=>
        {
            if(!engine.Running){Commit();await engine.Start();}
            if(engine.GmAddress is not {} address)throw new InvalidOperationException("GM 工具需要随包服务端。");
            if(gmWindow is null||gmWindow.IsDisposed){gmWindow=new GmWindow(address);gmWindow.Show(this);}else{gmWindow.Activate();}
        },"正在打开 GM 工具…"),width:110));
        top.Controls.Add(nav, 1, 0); shell.Controls.Add(top, 0, 0); shell.Controls.Add(content, 0, 1);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new(22, 0, 22, 0), Margin = Padding.Empty };
        bottom.RowCount = 1; bottom.RowStyles.Add(new(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new(SizeType.Absolute, 300)); footer.Dock = DockStyle.Fill;
        var version = Theme.Label($"客户端 9.1.0    ·    桌面版 {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}", 9, Theme.Muted); version.Dock = DockStyle.Fill; version.TextAlign = ContentAlignment.MiddleRight;
        bottom.Controls.Add(footer, 0, 0); bottom.Controls.Add(version, 1, 0); shell.Controls.Add(bottom, 0, 2); Controls.Add(shell);
    }
    private void BuildHome()
    {
        var home = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        home.RowCount = 1; home.RowStyles.Add(new(SizeType.Percent, 100));
        home.ColumnStyles.Add(new(SizeType.Percent, 100)); home.ColumnStyles.Add(new(SizeType.Absolute, 408)); home.Controls.Add(hero, 0, 0);
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, AutoScroll = true, Padding = new(24, 18, 24, 18), Margin = Padding.Empty };
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = Padding.Empty, Margin = Padding.Empty };
        Add(form, Theme.Label("准备启航", 22, null, true), 40);
        Add(form, Theme.Label("本地舰长 · PC 国服 · 9.1.0", 9, Theme.Muted), 24);
        Add(form, Theme.Label("客户端目录", 9, Theme.Muted), 25);
        Add(form, PathRow(gamePath, "选择", ChooseClient), 36);
        var account = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        account.RowCount = 1; account.RowStyles.Add(new(SizeType.Percent, 100));
        account.ColumnStyles.Add(new(SizeType.Percent, 60)); account.ColumnStyles.Add(new(SizeType.Percent, 40));
        var a = new Panel { Dock = DockStyle.Fill, Margin = new(0, 0, 12, 0) }; var b = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var al = Theme.Label("舰长名称", 9, Theme.Muted); al.Dock = DockStyle.Top; al.Height = 28; captain.Dock = DockStyle.Bottom; a.Controls.Add(captain); a.Controls.Add(al);
        var bl = Theme.Label("本地 UID", 9, Theme.Muted); bl.Dock = DockStyle.Top; bl.Height = 28; uid.Dock = DockStyle.Bottom; b.Controls.Add(uid); b.Controls.Add(bl);
        account.Controls.Add(a, 0, 0); account.Controls.Add(b, 1, 0); Add(form, account, 58);
        Add(form, Theme.Label("环境状态", 9, Theme.Muted), 28);
        Add(form, StatusRow("客户端", clientState), 23); Add(form, StatusRow("HTTPS 证书", certState), 23); Add(form, StatusRow("服务端 / 本地接口", serviceState), 23); Add(form, StatusRow("系统代理", routeState), 23);
        Add(form, Row(start, stop), 44);
        var check = Button("检查客户端", () => Run(() => { if (!engine.Running) Commit(); foreach (var result in engine.CheckClient()) engine.Log.Write("检查", result); return Task.CompletedTask; }, "正在检查客户端…"));
        Add(form, Row(check, install), 44);
        Add(form, launch, 58);
        notice.AutoEllipsis = true; Add(form, notice, 32);
        Add(form, Theme.Label("登录与大厅基础数据已接入，正在验证客户端兼容。", 9, Theme.Muted), 30);
        card.Controls.Add(form); home.Controls.Add(card, 1, 0); pages["开始游戏"] = home;
    }
    private void BuildLogs()
    {
        var page = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Surface, Padding = new(22) };
        page.RowStyles.Add(new(SizeType.Absolute, 58)); page.RowStyles.Add(new(SizeType.Percent, 100)); page.RowStyles.Add(new(SizeType.Absolute, 58));
        page.Controls.Add(Theme.Label("运行记录", 21, null, true).withSizeDock(), 0, 0);
        logs.AccessibleName = "运行日志"; page.Controls.Add(logs, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(0, 12, 0, 0) };
        buttons.Controls.Add(Button("导出诊断", Export, width: 120));
        buttons.Controls.Add(Button("打开日志目录", () => OpenFolder(engine.Log.DirectoryPath), width: 148));
        buttons.Controls.Add(Button("清空显示", () => logs.Clear(), width: 120));
        buttons.Controls.Add(Button("恢复系统代理", () => Run(() => { engine.Proxy.Restore(); return Task.CompletedTask; }, "正在恢复原代理…"), width: 150));
        page.Controls.Add(buttons, 0, 2); pages["运行记录"] = page;
    }
    private void BuildSettings()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface, Padding = new(28, 20, 28, 20) };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        Add(table, Theme.Label("启动设置", 21, null, true), 48);
        Add(table, Theme.Label("设置在本机保存；修改端口和账号前请先停止服务。", 10, Theme.Muted), 34);
        Add(table, Field("本地接口端口", httpPort), 42); Add(table, Field("代理端口", proxyPort), 42); Add(table, Field("游戏服 UDP 端口", gamePort), 42);
        Add(table, useProxy, 36); Add(table, useWinHttp, 36); Add(table, handshake, 36);
        Add(table, Theme.Label("自定义服务端（留空使用随包服务端）", 10, Theme.Muted), 33);
        Add(table, PathRow(serverPath, "选择", () => Pick(serverPath, "游戏服程序|*.exe", "选择兼容 9.1 的本地游戏服"), true), 40);
        Add(table, Theme.Label("默认自动运行 Server 文件夹中的服务端，无需选择。", 9, Theme.Muted), 29);
        Add(table, Theme.Label("首页背景（可选）", 10, Theme.Muted), 33);
        Add(table, PathRow(wallpaper, "选择", () => Pick(wallpaper, "背景图片|*.png;*.jpg;*.jpeg;*.bmp", "选择首页背景"), true), 40);
        Add(table, save, 50);
        Add(table, Theme.Label("配置位置：" + AppPaths.Data, 9, Theme.Muted), 34);
        scroll.Controls.Add(table); pages["启动设置"] = scroll;
    }
    private void BuildAbout()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new(32), AutoScroll = true };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(12), ScrollBars = ScrollBars.Vertical, Text = "崩坏 3 · 本地登录器\r\n桌面版 1.4.1  /  2026.10.09\r\n\r\n原生 Windows 窗口\r\n参考 115CN 的顶部导航、主视觉和固定启动区布局。\r\n\r\n本次更新\r\n• 新增 GM 工具：货币、装备、女武神、补给管理及邮件。\r\n• 接入经过两份真实样本验证的 9.1 dispatch 密钥。\r\n• 本地接口、HTTPS 代理、证书和进程管理迁入 .NET。\r\n• 新增客户端选择、本地账号、设置、实时日志及诊断导出。\r\n• 退出恢复原系统代理；端口冲突自动清理本次启动。\r\n• 发布包同时包含登录器和服务端，自动定位并托管。\r\n• 启动时核对服务端就绪状态，退出时正常停服。\r\n\r\n当前范围\r\n提供本地 SDK、KCP、账号认证、大厅与 GM 工具。\r\n第一章出击、通关结算和任务领奖已接入；实机待验收。\r\n服务端随登录器一起发布，无需手动启动服务端。\r\n\r\n本地账号仅用于此测试环境，请勿填写正式账号密码。" };
        page.Controls.Add(text); pages["版本信息"] = page;
    }
    private void ShowPage(string name)
    {
        content.SuspendLayout(); content.Controls.Clear(); content.Controls.Add(pages[name]);
        foreach (var pair in navigation) { pair.Value.Selected = pair.Key == name; pair.Value.Invalidate(); }
        content.ResumeLayout();
    }
    private static void Add(TableLayoutPanel table, Control control, int height)
    {
        int index = table.RowCount++; table.RowStyles.Add(new(SizeType.Absolute, height)); control.Dock = DockStyle.Fill; control.Margin = new(0, 0, 0, 4); table.Controls.Add(control, 0, index);
    }
    private static Control Field(string name, Control input)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = Padding.Empty }; row.RowStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 220)); row.ColumnStyles.Add(new(SizeType.Absolute, 170));
        row.Controls.Add(Theme.Label(name).withSizeDock(), 0, 0); input.Dock = DockStyle.Fill; row.Controls.Add(input, 1, 0); return row;
    }
    private static Control StatusRow(string name, Label value)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = Padding.Empty }; row.RowStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Percent, 44)); row.ColumnStyles.Add(new(SizeType.Percent, 56));
        row.Controls.Add(Theme.Label(name, 9, Theme.Muted).withSizeDock(), 0, 0); value.Dock = DockStyle.Fill; value.TextAlign = ContentAlignment.MiddleRight; row.Controls.Add(value, 1, 0); return row;
    }
    private static Control Row(Control first, Control second)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = Padding.Empty }; row.RowStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Percent, 50)); row.ColumnStyles.Add(new(SizeType.Percent, 50));
        first.Dock = second.Dock = DockStyle.Fill; first.Margin = new(0, 2, 5, 5); second.Margin = new(5, 2, 0, 5); row.Controls.Add(first, 0, 0); row.Controls.Add(second, 1, 0); return row;
    }
    private Control PathRow(TextBox input, string text, Action action, bool clear = false)
    {
        var row = new TableLayoutPanel { ColumnCount = clear ? 3 : 2, RowCount = 1, Margin = Padding.Empty }; row.RowStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 70)); if (clear) row.ColumnStyles.Add(new(SizeType.Absolute, 70));
        input.Dock = DockStyle.Fill; input.Margin = new(0, 5, 8, 0); row.Controls.Add(input, 0, 0);
        var b = Button(text, action); b.Dock = DockStyle.Fill; b.Margin = new(0, 0, 0, 2); row.Controls.Add(b, 1, 0);
        if (clear) { var c = Button("清除", () => input.Text = ""); c.Dock = DockStyle.Fill; row.Controls.Add(c, 2, 0); } return row;
    }
    private static NumericUpDown Number(int value, string name) => new() { Minimum = 1024, Maximum = 65535, Value = value, BackColor = Theme.Background, ForeColor = Theme.Text, Font = Theme.Font(), AccessibleName = name };
    private static CheckBox Check(string text, bool value) => new() { Text = text, Checked = value, ForeColor = Theme.Text, Font = Theme.Font(), AutoSize = false };
    private ActionButton Button(string text, Action action, bool primary = false, int width = 130)
    { var b = new ActionButton(text, primary) { Width = width, Margin = new(0, 0, 10, 0) }; b.Click += (_, _) => action(); operations.Add(b); return b; }
    private void Pick(TextBox input, string filter, string title)
    {
        using var dialog = new OpenFileDialog { Filter = filter, Title = title, CheckFileExists = true, FileName = input.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.FileName;
    }
    private void ChooseClient() { Pick(gamePath, "崩坏 3 客户端|BH3.exe", "选择客户端 BH3.exe"); UpdateState(); }
    private void Commit()
    {
        var c = engine.Config.Copy(); c.GameExe = gamePath.Text; c.AccountName = captain.Text.Trim(); c.AccountUid = uid.Text.Trim();
        c.HttpPort = (int)httpPort.Value; c.ProxyPort = (int)proxyPort.Value; c.GamePort = (int)gamePort.Value;
        c.UseSystemProxy = useProxy.Checked; c.UseWinHttp = useWinHttp.Checked; c.EnableHandshake = handshake.Checked; c.Wallpaper = wallpaper.Text; c.ServerExe = serverPath.Text;
        if (c.UseWinHttp && !engine.IsAdmin) throw new InvalidOperationException("WinHTTP 接管需要管理员权限。请取消该选项，或以管理员身份重新打开登录器。");
        if (c.Wallpaper.Length > 0) { using var test = Image.FromFile(c.Wallpaper); }
        engine.Save(c); LoadArt();
    }
    private void LoadArt() { try { hero.SetArt(engine.Config.Wallpaper); } catch (Exception ex) { engine.Log.Write("界面", "背景图片读取失败：" + ex.Message); } }
    private async void Run(Func<Task> work, string label)
    {
        if (busy) return; busy = true; notice.Text = label; notice.ForeColor = Theme.Accent; footer.Text = label; UpdateState();
        try { await work(); notice.Text = "操作完成，详细信息见运行记录。"; notice.ForeColor = Theme.Cyan; }
        catch (Exception ex) { engine.Log.Write("错误", ex.Message); notice.Text = ex.Message; notice.ForeColor = Theme.Error; }
        finally { busy = false; UpdateState(); }
    }
    private void UpdateState()
    {
        if (IsDisposed || closing) return;
        foreach (var c in operations) c.Enabled = !busy;
        start.Enabled = !busy && !engine.Running; stop.Enabled = !busy && engine.Running; save.Enabled = !busy && !engine.Running;
        launch.Enabled = !busy && !engine.GameRunning; install.Enabled = !busy && !certificateInstalled;
        gamePath.Enabled = captain.Enabled = uid.Enabled = !busy && !engine.Running;
        clientState.Text = File.Exists(gamePath.Text) ? "●  已发现" : "○  未选择"; clientState.ForeColor = File.Exists(gamePath.Text) ? Theme.Cyan : Theme.Muted;
        certState.Text = certificateInstalled ? "●  已安装" : "○  未安装"; certState.ForeColor = certificateInstalled ? Theme.Cyan : Theme.Muted;
        serviceState.Text = engine.Running ? "●  运行中" : "○  未启动"; serviceState.ForeColor = engine.Running ? Theme.Cyan : Theme.Muted;
        routeState.Text = engine.Proxy.Active ? "●  已接管" : "○  原系统配置"; routeState.ForeColor = engine.Proxy.Active ? Theme.Cyan : Theme.Muted;
        if (!busy) footer.Text = engine.GameRunning ? "游戏进程运行中" : engine.Running ? "服务端、本地接口与代理已就绪" : "就绪  ·  本地服务未启动";
    }
    private void OnLog(string line)
    { if (IsDisposed || Disposing) return; if (!IsHandleCreated) return; try { BeginInvoke(() => AppendLog(line)); } catch (InvalidOperationException) { } }
    private void AppendLog(string line)
    {
        if (logs.TextLength > 220000) logs.Clear(); logs.SelectionStart = logs.TextLength;
        logs.SelectionColor = line.Contains("[错误]") ? Theme.Error : line.Contains("[服务]") || line.Contains("[游戏]") ? Theme.Cyan : Theme.Muted;
        logs.AppendText(line + Environment.NewLine); logs.ScrollToCaret();
    }
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "诊断压缩包|*.zip", FileName = $"BH3诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Run(() => { if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName); engine.ExportDiagnostics(dialog.FileName); engine.Log.Write("诊断", "已导出：" + dialog.FileName); return Task.CompletedTask; }, "正在导出诊断…");
    }
    private void OpenFolder(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { engine.Log.Write("错误", ex.Message); } }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (canClose) return;
        e.Cancel = true; if (closing || busy) { if (busy) notice.Text = "当前操作完成后即可关闭。"; return; }
        closing = true; poll.Stop(); Enabled = false;
        try { await engine.Stop(); canClose = true; Close(); }
        catch (Exception ex) { closing = false; Enabled = true; poll.Start(); MessageBox.Show(this, ex.Message, "恢复代理失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { poll.Dispose(); engine.Log.Added -= OnLog; foreach (var p in pages.Values) p.Dispose(); } base.Dispose(disposing); }
}

internal static class ControlExtensions
{
    internal static T withSizeDock<T>(this T c) where T : Control { c.Dock = DockStyle.Fill; return c; }
}
