using System.Diagnostics;
using System.Globalization;
using Bh3Capture;
namespace CaptureApp;
internal sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(27, 43, 64), Accent = Color.FromArgb(27, 102, 221), Muted = Color.FromArgb(95, 111, 130);
    private readonly CaptureController controller = new(new CommandRunner());
    private readonly Settings settings = Settings.Load();
    private CaptureSession? displayed;
    private bool busy, allowClose, settingsExpanded, refreshingCases;
    private string activity = "正在处理…", lastExport = "", noticeText = "", previewScene = "Ready";
    private readonly bool preview;
    private readonly string[] startupArgs;
    private TableLayoutPanel page = null!;
    private Panel settingsCard = null!;
    private readonly Label destination = Label("", 9), notice = Label("", 10), steps = Label("① 开始抓包     →     ② 操作完成后分段     →     ③ 结束并分享", 10, true);
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Visible = false };
    private readonly Button configure = Button("采集设置 ▾"), more = Button("更多操作 ▾");
    private readonly ContextMenuStrip moreMenu = new();
    private readonly ToolTip tips = new() { AutoPopDelay = 15000 };
    private ToolStripMenuItem recheckItem = null!, recoverItem = null!, pauseCaseItem = null!;
    private readonly TextBox root = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox version = new() { Dock = DockStyle.Fill };
    private readonly TextBox ports = new() { Dock = DockStyle.Fill };
    private readonly ComboBox mode = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button caseType = Button("类型（选填） ▾");
    private readonly ContextMenuStrip caseTypeMenu = new();
    private string caseTypeValue = "其他操作";
    private readonly Label caseStatus = Label("还未开始记录", 10, true);
    private readonly NumericUpDown limit = new() { Minimum = 128, Maximum = 65536, Increment = 1024, ThousandsSeparator = true, Dock = DockStyle.Fill };
    private readonly CaseNameInput caseName = new() { Dock = DockStyle.Fill, PlaceholderText = "名称选填，也可以之后补写", MaxLength = 100 };
    private readonly TextBox notes = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, PlaceholderText = "记录难度、女武神及装备、关键操作或异常现象…", MaxLength = 10000 };
    private readonly Button start = Button("开始抓包", true), stop = Button("结束并生成分享包"), begin = Button("开始记录新的一段", true);
    private readonly Button nextCase = Button("完成本段，继续记录", true), editCase = Button("补写名称和备注…");
    private readonly Button open = Button("历史记录"), export = Button("另存分享包…"), recover = Button("修复未完成记录"), recheck = Button("重新检查数据"), folder = Button("查看文件"), browse = Button("选择目录");
    private readonly Button showExport = Button("查看分享包", true);
    private readonly Button help = Button("使用说明");
    private readonly Label state = Label("准备抓包", 15, true), stats = Label("先开始抓包，再启动游戏并登录。", 10), hint = Label("一次操作结束后点完成本段，下一段自动开始。", 10);
    private readonly DataGridView cases = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly TextBox detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
    private readonly TextBox check = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    public MainForm(bool preview = false, string[]? startupArgs = null)
    {
        this.preview = preview;
        this.startupArgs = startupArgs ?? [];
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        if (preview) Opacity = 0;
        Text = "崩坏3 抓包助手 1.0.0";
        Font = new Font("Microsoft YaHei UI", 9F);
        ForeColor = Ink; BackColor = Color.FromArgb(241, 245, 250);
        MinimumSize = new Size(880, 650); Size = new Size(1080, 780); StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        foreach (var type in new[] { "登录 / 大厅", "女武神", "装备 / 圣痕", "编队 / 技能", "异常复现", "其他操作" })
        {
            var item = new ToolStripMenuItem(type); item.Click += (_, _) => Sync(() => SetCaseType(type, updateCurrent: true)); caseTypeMenu.Items.Add(item);
        }
        caseType.Click += (_, _) => caseTypeMenu.Show(caseType, new Point(0, caseType.Height));
        SetCaseType(caseTypeMenu.Items.Cast<ToolStripMenuItem>().Any(i => i.Text == settings.DraftType) ? settings.DraftType : "其他操作");
        mode.Items.AddRange(["全部 UDP（首次抓包推荐）", "指定端口"]);
        root.Text = settings.OutputParent; version.Text = settings.Version; ports.Text = settings.Ports;
        caseName.Text = settings.DraftName; notes.Text = settings.DraftNotes;
        mode.SelectedIndex = settings.AllUdp ? 0 : 1; limit.Value = Math.Clamp(settings.MaxMB, 128, 65536);
        mode.SelectedIndexChanged += (_, _) => RefreshButtons();
        controller.Progress += ReportProgress;
        start.Click += async (_, _) => await Run(StartCapture, "正在准备采集…");
        begin.Click += (_, _) => Sync(BeginCurrentCase);
        nextCase.Click += (_, _) => Sync(() => SaveCurrentCase(true));
        editCase.Click += (_, _) => EditSelectedCase();
        cases.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && editCase.Enabled) EditSelectedCase(); };
        stop.Click += async (_, _) => await Run(() => StopCapture("User"), "正在结束采集…");
        open.Click += (_, _) => OpenSession();
        help.Click += (_, _) => ShowHelp();
        export.Click += async (_, _) => await ExportSession();
        recover.Click += async (_, _) => await Run(RecoverSession, "正在修复未完成记录…");
        recheck.Click += async (_, _) => await Run(RecheckSession);
        folder.Click += (_, _) => Sync(() => { var path = displayed?.DirectoryPath ?? root.Text; Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true }); });
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "选择抓包保存目录", UseDescriptionForTitle = true, SelectedPath = root.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) root.Text = dialog.SelectedPath; };
        configure.Click += (_, _) => SetSettingsExpanded(!settingsExpanded);
        more.Click += (_, _) => { RefreshButtons(); moreMenu.Show(more, new Point(0, more.Height)); };
        showExport.Click += (_, _) => Sync(() => RevealFile(lastExport));
        tips.SetToolTip(open, "查看以前的抓包：先解压分享 ZIP，再选择目录中的 session.json。");
        tips.SetToolTip(stop, "保存最后一段，结束抓包，自动转换、检查并生成 ZIP。结束后下一轮须重新登录。");
        tips.SetToolTip(nextCase, "做完一次副本、购买或异常复现后点这里，本段立即保存，下一段自动开始。抓包始终继续。");
        tips.SetToolTip(editCase, "双击列表也能补写已保存分段的名称、备注；不会改变抓包范围。");
        tips.SetToolTip(caseType, "当前段做了什么？可选女武神、购买等；自动名称随类型更新，自定义名称保留。");
        tips.SetToolTip(export, "把已经完成的记录保存为另一个 ZIP，方便复制到其他目录。");
        tips.SetToolTip(recover, "重新转换上次未完成的原始数据，不会恢复或重新启动采集。");
        tips.SetToolTip(configure, "默认全部 UDP 和 4 GB 容量即可使用；在这里更改保存目录、端口和容量。");
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F1) { help.PerformClick(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.Enter)
            { if (nextCase.Enabled) nextCase.PerformClick(); else if (begin.Enabled) begin.PerformClick(); e.SuppressKeyPress = true; }
        };
        root.TextChanged += (_, _) => RefreshDestination();
        mode.SelectedIndexChanged += (_, _) => RefreshDestination();
        cases.SelectionChanged += (_, _) => ShowCase();
        timer.Tick += async (_, _) => await Tick();
        FormClosing += OnFormClosing;
        if (preview) FillDemo();
        RefreshButtons();
        ResumeLayout(true);
        if (!preview) timer.Start();
        Shown += async (_, _) =>
        {
            if (preview) return;
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            if (this.startupArgs.Contains("--start")) await Run(StartCapture, "正在准备采集…");
            else if (Array.IndexOf(this.startupArgs, "--recover") is var index && index >= 0 && index + 1 < this.startupArgs.Length)
            {
                Sync(() => LoadSession(this.startupArgs[index + 1]));
                if (displayed is not null) await Run(RecoverSession, "正在修复未完成记录…");
            }
            else if (File.Exists(settings.LastSession))
            {
                try
                {
                    var previous = CaptureSession.Load(settings.LastSession);
                    if (previous.State is "Preparing" or "Capturing" or "Converting" or "Stopped" or "ConversionFailed")
                    {
                        displayed = previous; RefreshCases();
                        noticeText = "发现上次未完成的记录。点“修复未完成记录”重新转换，原始文件会保留。";
                        RefreshButtons();
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                { AppendLog("上次记录无法读取：" + ex.Message); }
            }
        };
    }
    private static Label Label(string text, float size = 10, bool bold = false) => new() { Text = text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = bold ? Ink : Muted };
    private static Button Button(string text, bool primary = false) => new() { Text = text, AutoSize = false, Size = new Size(110, 30), MinimumSize = new Size(110, 30),
        FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Margin = new Padding(0, 3, 10, 3), Cursor = Cursors.Hand };
    private static TableLayoutPanel Table(int columns, int rows) => new() { ColumnCount = columns, RowCount = rows, Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
    private static Panel Card(Control content) => new() { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16, 10, 16, 10), Margin = new Padding(0, 0, 0, 10), Controls = { content } };
    private void BuildLayout()
    {
        page = Table(1, 7); page.Padding = new Padding(22, 12, 22, 14);
        foreach (var height in new[] { 90F, 122F, 48F, 0F, 126F }) page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); page.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        page.Dock = DockStyle.Top; page.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        scroll.Controls.Add(page); Controls.Add(scroll);
        scroll.Resize += (_, _) => ResizePage();
        var header = Table(2, 2); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        header.Controls.Add(Label("崩坏3 抓包助手", 22, true), 0, 0);
        header.Controls.Add(steps, 0, 1);
        var headerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        help.MinimumSize = new Size(100, 30); help.Width = 100;
        headerActions.Controls.Add(open); headerActions.Controls.Add(help); header.Controls.Add(headerActions, 1, 0); header.SetRowSpan(headerActions, 2); page.Controls.Add(header, 0, 0);
        var statusCard = Table(2, 4); statusCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); statusCard.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        foreach (var height in new[] { 32F, 24F, 34F, 6F }) statusCard.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        statusCard.Controls.Add(state, 0, 0); statusCard.Controls.Add(stats, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        stop.Width = 184; recover.Width = 174; showExport.Width = 132; start.Width = 148;
        foreach (var button in new[] { stop, showExport, recover, start }) { button.Height = 38; button.Margin = new Padding(8, 5, 0, 4); actions.Controls.Add(button); }
        statusCard.Controls.Add(actions, 1, 0); statusCard.SetRowSpan(actions, 2);
        statusCard.Controls.Add(notice, 0, 2); statusCard.SetColumnSpan(notice, 2);
        statusCard.Controls.Add(progress, 0, 3); statusCard.SetColumnSpan(progress, 2);
        page.Controls.Add(Card(statusCard), 0, 1);
        var summary = Table(2, 1); summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
        summary.Controls.Add(destination, 0, 0); configure.Width = 124; summary.Controls.Add(configure, 1, 0);
        page.Controls.Add(summary, 0, 2);
        var config = Table(6, 3);
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80)); config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88)); config.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0)); config.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        for (var i = 0; i < 3; i++) config.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
        config.Controls.Add(Label("保存到"), 0, 0); config.Controls.Add(root, 1, 0); config.SetColumnSpan(root, 4); config.Controls.Add(browse, 5, 0);
        config.Controls.Add(Label("采集范围"), 0, 1); config.Controls.Add(mode, 1, 1); config.Controls.Add(Label("端口"), 2, 1); config.Controls.Add(ports, 3, 1); config.SetColumnSpan(ports, 3);
        config.Controls.Add(Label("客户端"), 0, 2); config.Controls.Add(version, 1, 2); config.Controls.Add(Label("容量 MB"), 2, 2); config.Controls.Add(limit, 3, 2);
        var capacityHint = Label("128 MB 分卷\n达到阈值自动停止", 9); config.Controls.Add(capacityHint, 4, 2); config.SetColumnSpan(capacityHint, 2);
        settingsCard = Card(config); settingsCard.Visible = false; page.Controls.Add(settingsCard, 0, 3);
        var caseEntry = Table(3, 3); caseEntry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); caseEntry.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); caseEntry.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 318));
        caseEntry.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); caseEntry.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); caseEntry.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caseTitle = Label("② 分段记录 · 名称可以之后补写", 10, true); caseTitle.Margin = Padding.Empty;
        caseEntry.Controls.Add(caseTitle, 0, 0); caseEntry.SetColumnSpan(caseTitle, 2);
        caseStatus.Margin = Padding.Empty; caseStatus.TextAlign = ContentAlignment.MiddleRight; caseEntry.Controls.Add(caseStatus, 2, 0);
        caseType.Dock = DockStyle.Fill; caseType.Margin = new Padding(0, 2, 12, 2); caseName.Margin = new Padding(0, 2, 0, 2);
        caseEntry.Controls.Add(caseType, 0, 1); caseEntry.Controls.Add(caseName, 1, 1);
        var recordActions = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        begin.Dock = nextCase.Dock = DockStyle.Fill; begin.Margin = nextCase.Margin = Padding.Empty; recordActions.Padding = new Padding(12, 2, 0, 2);
        recordActions.Controls.Add(begin); recordActions.Controls.Add(nextCase); caseEntry.Controls.Add(recordActions, 2, 1);
        hint.Margin = Padding.Empty; caseEntry.Controls.Add(hint, 0, 2); caseEntry.SetColumnSpan(hint, 3);
        page.Controls.Add(Card(caseEntry), 0, 4);
        var body = Table(2, 2); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(Label("操作记录 · 双击可补写名称和备注", 11, true), 0, 0); body.Controls.Add(Label("当前段备注（选填）", 11, true), 1, 0);
        cases.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(232, 239, 249), ForeColor = Ink, Font = new Font(Font, FontStyle.Bold) };
        cases.EnableHeadersVisualStyles = false; cases.ColumnHeadersHeight = 34; cases.RowTemplate.Height = 34;
        cases.DefaultCellStyle.SelectionBackColor = Color.FromArgb(218, 233, 255); cases.DefaultCellStyle.SelectionForeColor = Ink;
        foreach (var (name, title, weight) in new[] { ("name", "分段名称", 38F), ("start", "开始时间", 22F), ("duration", "时长", 16F), ("status", "状态", 24F) })
            cases.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        body.Controls.Add(cases, 0, 1);
        var notePanel = Table(1, 3); notePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); notePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); notePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        notes.Margin = new Padding(10, 0, 0, 5); detail.Margin = new Padding(10, 0, 0, 0); editCase.Margin = new Padding(10, 3, 0, 0); editCase.Dock = DockStyle.Fill;
        notePanel.Controls.Add(notes, 0, 0); notePanel.Controls.Add(detail, 0, 1); notePanel.Controls.Add(editCase, 0, 2); body.Controls.Add(notePanel, 1, 1);
        page.Controls.Add(Card(body), 0, 5);
        var bottom = Table(2, 1); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 192));
        var tabs = new TabControl { Dock = DockStyle.Fill }; tabs.TabPages.Add(new TabPage("检查结果") { Controls = { check } }); tabs.TabPages.Add(new TabPage("详细日志") { Controls = { log } }); bottom.Controls.Add(tabs, 0, 0);
        var finalActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12, 0, 0, 0), WrapContents = false };
        foreach (var button in new[] { export, folder, more }) { button.Width = 164; finalActions.Controls.Add(button); }
        recheckItem = new ToolStripMenuItem("重新检查数据", null, async (_, _) => await Run(RecheckSession, "正在重新检查数据…"));
        recoverItem = new ToolStripMenuItem("重新转换原始文件", null, async (_, _) => await Run(RecoverSession, "正在转换原始文件…"));
        moreMenu.Items.Add(recheckItem); moreMenu.Items.Add(recoverItem);
        moreMenu.Items.Add(new ToolStripSeparator());
        moreMenu.Items.Add("复制分享包路径", null, (_, _) => Sync(() => { if (File.Exists(lastExport)) { Clipboard.SetText(lastExport); noticeText = "分享包路径已复制，可以粘贴给需要接收文件的人。"; } }));
        moreMenu.Items.Add(new ToolStripSeparator());
        pauseCaseItem = new ToolStripMenuItem("结束当前段，稍后手动开始", null, (_, _) => Sync(() => SaveCurrentCase(false)))
            { ToolTipText = "只结束当前段的标记，网络抓包仍继续。想精确标记下次操作的起点时再手动开始新的一段。" };
        moreMenu.Items.Add(pauseCaseItem);
        moreMenu.Items.Add(new ToolStripSeparator());
        moreMenu.Items.Add("查看账号副本", null, (_, _) => Sync(() => {
            var file = Path.Combine(displayed?.DirectoryPath ?? root.Text, "account-copy.json");
            if (!File.Exists(file)) throw new InvalidOperationException("请先结束采集，或打开历史记录后重新检查数据。");
            RevealFile(file);
        }));
        moreMenu.Items.Add("打开账号摘要", null, (_, _) => Sync(() => {
            var file = Path.Combine(displayed?.DirectoryPath ?? root.Text, "account-summary.txt");
            if (!File.Exists(file)) throw new InvalidOperationException("请先结束采集并生成账号副本。");
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }));
        moreMenu.ShowItemToolTips = true;
        recheckItem.ToolTipText = "重新读取已转换的数据，检查KCP 连续性及账号快照。";
        recoverItem.ToolTipText = "从原始 ETL 重新生成数据和分享包；不会重新启动采集。";
        bottom.Controls.Add(finalActions, 1, 0); page.Controls.Add(bottom, 0, 6);
        ResizePage();
    }
    private void ResizePage()
    {
        var scale = DeviceDpi / 96F;
        page.Width = page.Parent!.ClientSize.Width - (((Panel)page.Parent).VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0);
        page.Height = Math.Max(page.Parent.ClientSize.Height, (int)((settingsExpanded ? 880 : 750) * scale));
    }
    private void SetSettingsExpanded(bool expanded)
    {
        settingsExpanded = expanded; settingsCard.Visible = expanded;
        page.RowStyles[3].Height = expanded ? 128 * DeviceDpi / 96F : 0;
        configure.Text = expanded ? "收起设置 ▴" : "采集设置 ▾";
        ResizePage();
    }
    private void RefreshDestination() => destination.Text = $"{(mode.SelectedIndex == 1 ? "指定端口" : "全部 UDP · 推荐")}   |   保存到：{root.Text}";
    private void ReportProgress(string message)
    {
        AppendLog(message);
        if (InvokeRequired) { BeginInvoke(() => ReportProgressState(message)); return; }
        ReportProgressState(message);
    }
    private void ReportProgressState(string message)
    {
        if (!busy) return;
        activity = message; state.Text = message;
    }
    private void AppendLog(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(message)); return; }
        log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        if (log.TextLength > 100000) log.Text = log.Text[^60000..];
    }
    private bool IsCapturing => controller.OwnsCapture || (preview && previewScene is "Capturing" or "Recording" or "Paused");
    private void RefreshButtons()
    {
        var active = IsCapturing;
        var current = controller.OwnsCapture ? controller.Session : displayed;
        var runningCase = active && current?.OpenCase is not null;
        var canExport = !active && displayed?.State is "Ready" or "NeedsAttention";
        var unfinished = !active && displayed?.State is "Preparing" or "Capturing" or "Stopped" or "Converting" or "ConversionFailed";
        var hasRaw = (preview && previewScene == "Recovery") || (displayed is not null && Directory.Exists(displayed.DirectoryPath) && Directory.EnumerateFiles(displayed.DirectoryPath, "*.etl").Any());
        start.Enabled = !busy && !active; stop.Enabled = !busy && active;
        start.Visible = !active; stop.Visible = active;
        start.Text = displayed is null ? "开始抓包" : "开始新的抓包";
        start.BackColor = canExport || unfinished ? Color.White : Accent; start.ForeColor = canExport || unfinished ? Ink : Color.White;
        begin.Enabled = !busy && active && !runningCase; nextCase.Enabled = !busy && runningCase;
        begin.Visible = !runningCase; nextCase.Visible = runningCase;
        caseName.Enabled = caseType.Enabled = !busy && (active || displayed is null);
        pauseCaseItem.Enabled = !busy && runningCase; pauseCaseItem.Visible = active;
        notes.ReadOnly = busy || (!active && displayed is not null);
        editCase.Enabled = !busy && cases.CurrentRow?.Tag is CaptureCase selectedCase && selectedCase.EndedUtc is not null;
        caseName.PlaceholderText = runningCase ? "名称选填，留空保留自动编号" : "名称选填，自动命名为“" + CaptureCaseName.NextSegment(current, caseTypeValue) + "”";
        root.Enabled = version.Enabled = mode.Enabled = limit.Enabled = browse.Enabled = !busy && !active;
        ports.Enabled = !busy && !active && mode.SelectedIndex == 1;
        configure.Enabled = !busy;
        open.Enabled = !busy && !active; export.Enabled = !busy && canExport;
        showExport.Enabled = !busy && (preview || File.Exists(lastExport)); showExport.Visible = !active && lastExport.Length > 0;
        recover.Enabled = !busy && !active && hasRaw; recover.Visible = unfinished;
        recoverItem.Enabled = !busy && !active && hasRaw;
        recheck.Enabled = !busy && !active && displayed?.PcapFiles.Count > 0;
        recheckItem.Enabled = recheck.Enabled;
        moreMenu.Items[3].Enabled = !busy && File.Exists(lastExport);
        folder.Enabled = !busy;
        progress.Visible = busy;
        if (busy) { state.Text = activity; state.ForeColor = Accent; }
        else if (active) { state.Text = runningCase ? $"抓包持续进行 · 正在记录第 {current!.Cases.IndexOf(current.OpenCase!) + 1} 段" : "抓包持续进行 · 分段标记已暂停"; state.ForeColor = Color.FromArgb(0, 132, 108); }
        else
        {
            state.Text = displayed?.State switch { "Ready" => lastExport.Length > 0 ? "采集完成 · 查看账号副本与缺项" : "记录已打开 · 基础检查通过", "NeedsAttention" => "记录已保留 · 检查结果有提示", "ConversionFailed" => "转换未完成 · 可修复记录", "Capturing" or "Preparing" or "Converting" or "Stopped" => "上次记录未完成 · 可以修复", "StartFailed" => "抓包未启动 · 查看详细日志", _ => "先退出游戏，再开始抓包" };
            state.ForeColor = displayed?.State == "NeedsAttention" ? Color.FromArgb(172, 108, 25) : Ink;
            if (displayed is null) stats.Text = "开始后自动记录第一段，登录数据和后续操作都会保留。";
            else stats.Text = $"{displayed.Cases.Count} 段操作记录 · 开始于 {displayed.StartedUtc.ToLocalTime():MM-dd HH:mm:ss}";
        }
        hint.Text = runningCase ? "一次操作做完点右边，本段保存、下一段自动开始。第一段含登录；名称和备注都可后补。" : active ? "抓包仍在继续。准备好下一次操作时点“开始记录新的一段”，只恢复分段标记。" : displayed is null ? "先点“开始抓包”，第一段自动记录。登录、操作后点“完成本段”；最后结束并分享。" : "每段对应一次副本、购买或复现；双击列表可补写名称和备注，时间范围保持原样。";
        RefreshCaseStatus(active, runningCase, current);
        notice.Text = busy ? "请稍候，窗口可以保持打开。当前步骤和详细进度会显示在这里及日志中。" : noticeText.Length > 0 ? noticeText : "自动记录每段操作，做完一次就分段；全部做完点“结束并生成分享包”。";
        steps.ForeColor = active ? Accent : Ink;
        check.Text = CheckSummary();
        foreach (var button in new[] { start, stop, begin, nextCase, editCase, caseType, open, export, recover, recheck, folder, browse, configure, more, showExport })
        {
            var primary = button == begin || button == nextCase || button == showExport || (button == start && !canExport && !unfinished);
            button.BackColor = !button.Enabled ? Color.FromArgb(235, 239, 245) : primary ? Accent : Color.White;
            button.ForeColor = !button.Enabled ? Muted : primary ? Color.White : Ink;
        }
        RefreshDestination();
    }
    private string CheckSummary()
    {
        if (displayed?.State is "Preparing" or "Capturing" or "Converting" or "Stopped" or "ConversionFailed")
            return displayed.State == "Capturing" ? "采集正在进行。结束后自动检查数据并生成分享 ZIP。" : "记录尚未完成转换，请等待处理或点击“修复未完成记录”。\r\n已有原始文件会保留，完成后才能得到本次的检查结果。";
        if (displayed?.Report is not { } report)
            return "结束时自动检查KCP 连续性及账号快照，并生成包含所有分段记录的 ZIP。\r\n检查结果不代表协议解密或业务流程完整性。";
        var text = new System.Text.StringBuilder(report.Passed ? "基础检查通过：已识别游戏登录数据，KCP 未发现缺口。" : "检查完成：存在以下提示，请保留原始数据并交给开发者核查。");
        text.Append($"\r\n账号快照 {report.AccountCount} 份；角色、技能、武器和圣痕见 account-copy.json。\r\n");
        text.Append($"\r\n识别游戏连接 {report.Connections.Count} 个 · 读取数据包 {report.PacketCount:N0} 个\r\n");
        if (displayed.StopReason == "CapacityLimit") text.AppendLine("已达到容量上限并自动停止，当前段操作可能尚未完成。");
        if (displayed.StopReason == "Interrupted") text.AppendLine("采集曾意外中断，结束时间和操作完整性需要核查。");
        foreach (var issue in report.Issues.Concat(report.Connections.SelectMany(c => c.Issues))) text.AppendLine("• " + issue);
        foreach (var connection in report.Connections)
            text.Append($"连接 {connection.Number}：请求首包{(connection.ClientStartFound ? "已捕获" : "缺失")}，回应首包{(connection.ServerStartFound ? "已捕获" : "缺失")}。\r\n");
        text.Append("账号副本仅包含实际捕获的响应；未解析的数据会保留原始分卷。");
        return text.ToString().ReplaceLineEndings("\r\n");
    }
    private void RefreshCases()
    {
        var selected = cases.CurrentRow?.Tag as CaptureCase;
        refreshingCases = true;
        cases.Rows.Clear();
        if (displayed is not null)
            foreach (var item in displayed.Cases)
            {
                var duration = item.EndOffsetSeconds is { } end ? TimeSpan.FromSeconds(Math.Max(0, end - item.StartOffsetSeconds)).ToString(@"hh\:mm\:ss") : "—";
                var status = item.EndedUtc is null ? (IsCapturing ? "记录中" : "未完成") : item.ClosedBySessionEnd ? "结束时已保存" : "已保存";
                var index = cases.Rows.Add(item.Name, item.StartedUtc.ToLocalTime().ToString("HH:mm:ss"), duration, status);
                cases.Rows[index].Tag = item;
                if (selected?.Id == item.Id || item.EndedUtc is null) cases.CurrentCell = cases.Rows[index].Cells[0];
            }
        refreshingCases = false;
        ShowCase(); RefreshButtons();
    }
    private void ShowCase()
    {
        if (refreshingCases) return;
        editCase.Enabled = !busy && cases.CurrentRow?.Tag is CaptureCase selected && selected.EndedUtc is not null;
        if (displayed is null || cases.CurrentRow?.Tag is not CaptureCase item || !displayed.Cases.Any(c => c.Id == item.Id)) { detail.Clear(); return; }
        var end = item.EndedUtc is { } ended ? ended.ToLocalTime().ToString("HH:mm:ss") : "未结束";
        detail.Text = $"{item.Name}\r\n时间：{item.StartedUtc.ToLocalTime():HH:mm:ss} → {end}\r\n{item.Notes}";
        if (!IsCapturing) notes.Text = item.Notes;
    }
    private void SetCaseType(string value, bool updateCurrent = false)
    {
        if (updateCurrent && IsCapturing && displayed?.OpenCase is { } current)
        {
            var number = displayed.Cases.IndexOf(current) + 1;
            var autoName = CaptureCaseName.Segment(number, current.Type);
            var name = string.IsNullOrWhiteSpace(caseName.Text) || caseName.Text == autoName
                ? CaptureCaseName.Segment(number, value) : CaptureCaseName.Normalize(caseName.Text);
            if (preview) { current.Name = name; current.Type = value; current.Notes = notes.Text; }
            else displayed.UpdateCaseDetails(current.Id, name, notes.Text, value);
            caseName.Text = name;
        }
        caseTypeValue = value; caseType.Text = value == "其他操作" ? "类型（选填） ▾" : value + " ▾";
        foreach (ToolStripMenuItem item in caseTypeMenu.Items) item.Checked = item.Text == value;
        if (updateCurrent) RefreshCases(); else RefreshButtons();
    }
    private string CurrentCaseName() => string.IsNullOrWhiteSpace(caseName.Text)
        ? displayed?.OpenCase is { } current ? CaptureCaseName.Segment(displayed.Cases.IndexOf(current) + 1, caseTypeValue) : CaptureCaseName.NextSegment(displayed, caseTypeValue)
        : CaptureCaseName.Normalize(caseName.Text);
    private void BeginCurrentCase()
    {
        var name = CurrentCaseName();
        if (preview)
        {
            displayed!.Cases.Add(new() { Name = name, Type = caseTypeValue, Notes = notes.Text, StartedUtc = DateTimeOffset.UtcNow });
            previewScene = "Recording";
        }
        else { controller.BeginCase(name, notes.Text, caseTypeValue); displayed = controller.Session; }
        caseName.Text = name;
        noticeText = "新的一段已开始记录。做完本次操作后点“完成本段，继续记录”。";
        RefreshCases(); AppendLog("开始记录：" + name); notes.Focus();
    }
    private void SaveCurrentCase(bool continueRecording)
    {
        var name = CurrentCaseName(); var nextName = CaptureCaseName.NextSegment(displayed, caseTypeValue, name);
        if (preview)
        {
            var item = displayed!.OpenCase!; item.Name = name; item.Notes = notes.Text; item.Type = caseTypeValue;
            item.EndedUtc = DateTimeOffset.UtcNow; item.EndOffsetSeconds = item.StartOffsetSeconds + 30;
            if (continueRecording) displayed.Cases.Add(new() { Name = nextName, Type = item.Type, StartedUtc = item.EndedUtc.Value, StartOffsetSeconds = item.EndOffsetSeconds.Value });
            previewScene = continueRecording ? "Recording" : "Paused";
        }
        else if (continueRecording) controller.SaveCaseAndBeginNext(name, notes.Text, nextName, caseTypeValue);
        else controller.SaveCase(name, notes.Text, caseTypeValue);
        caseName.Text = continueRecording ? nextName : ""; notes.Clear();
        noticeText = continueRecording ? $"“{name}”已保存，正在记录第 {displayed!.Cases.Count} 段。无需重新登录，最后一段可直接结束并分享。" : "本段已保存，抓包仍在继续；分段标记暂不进行，准备好后点“开始记录新的一段”。";
        RefreshCases(); caseName.Focus();
    }
    private void RefreshCaseStatus(bool active, bool recording, CaptureSession? current)
    {
        caseStatus.ForeColor = recording ? Color.FromArgb(0, 132, 108) : Muted;
        if (recording)
        {
            var seconds = preview ? 83 : Math.Max(0, controller.Elapsed.TotalSeconds - current!.OpenCase!.StartOffsetSeconds);
            caseStatus.Text = $"● 第 {current!.Cases.IndexOf(current.OpenCase!) + 1} 段记录中   " + TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
        }
        else caseStatus.Text = active ? $"已保存 {current!.Cases.Count} 段 · 标记已暂停" : current is null ? "开始抓包后自动记录" : $"共 {current.Cases.Count} 段记录";
    }
    private void EditSelectedCase()
    {
        if (displayed is null || cases.CurrentRow?.Tag is not CaptureCase item || item.EndedUtc is null || busy) return;
        using var dialog = new Form { Text = "补写这段操作的名称和备注", Font = Font, AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(600, 390), MinimumSize = new Size(480, 350), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, Padding = new Padding(16) };
        var content = Table(1, 6);
        foreach (var height in new[] { 26F, 34F, 30F }) content.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var nameInput = new TextBox { Dock = DockStyle.Fill, Text = item.Name, MaxLength = 100 };
        var notesInput = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = item.Notes, MaxLength = 10000 };
        content.Controls.Add(Label("名称", 10, true), 0, 0); content.Controls.Add(nameInput, 0, 1);
        content.Controls.Add(Label("备注（选填）", 10, true), 0, 2); content.Controls.Add(notesInput, 0, 3);
        content.Controls.Add(Label("只修改名称和备注，记录的开始、结束时间保持原样。", 9), 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var apply = Button("保存修改", true); var cancel = Button("取消"); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(apply); actions.Controls.Add(cancel); content.Controls.Add(actions, 0, 5); dialog.Controls.Add(content);
        dialog.AcceptButton = apply; dialog.CancelButton = cancel;
        apply.Click += (_, _) =>
        {
            try
            {
                displayed.UpdateCaseDetails(item.Id, nameInput.Text, notesInput.Text);
                var hadExport = lastExport.Length > 0; lastExport = "";
                noticeText = hadExport ? "修改已保存。旧 ZIP 保留；点“另存分享包”生成包含新名称和备注的 ZIP。" : "这段操作的名称和备注已保存，抓包范围保持原样。";
                RefreshCases(); dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "修改未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        dialog.ShowDialog(this);
    }
    private void SaveSettings(bool includeDraft = false)
    {
        settings.OutputParent = root.Text; settings.Version = version.Text; settings.Ports = ports.Text;
        settings.DraftName = includeDraft ? caseName.Text : "";
        settings.DraftNotes = includeDraft && (displayed is null || controller.OwnsCapture) ? notes.Text : "";
        settings.DraftType = includeDraft ? caseTypeValue : "其他操作";
        settings.AllUdp = mode.SelectedIndex == 0; settings.MaxMB = (int)limit.Value; settings.Save();
    }
    private bool Elevate(params string[] arguments)
    {
        if (Program.IsAdministrator()) return false;
        SaveSettings(includeDraft: true);
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add("--elevated"); foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            Process.Start(info); allowClose = true; Close(); return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { throw new InvalidOperationException("管理员授权已取消，采集未启动。准备好后再点一次即可。"); }
    }
    private async Task StartCapture()
    {
        var clearHistoricalNotes = displayed is not null;
        var draftName = clearHistoricalNotes ? "" : caseName.Text;
        var draftNotes = clearHistoricalNotes ? "" : notes.Text;
        SaveSettings();
        if (Process.GetProcessesByName("BH3.Launcher").Any()) throw new InvalidOperationException("请先退出本地登录器，恢复代理后再使用官方启动器登录。");
        if (Elevate("--start")) return;
        if (Process.GetProcessesByName("BH3").Any()) throw new InvalidOperationException("请先完全退出游戏，再开始采集。看到“采集已启动”后再登录。");
        var chosenPorts = mode.SelectedIndex == 0 ? Array.Empty<int>() : ports.Text.Split([',', '，', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        if (mode.SelectedIndex == 1 && chosenPorts.Length == 0) throw new ArgumentException("指定端口模式至少填写一个端口。");
        var full = Path.GetFullPath(root.Text);
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.AvailableFreeSpace < ((long)limit.Value * 3 + 1024) * 1024 * 1024)
            throw new IOException("保存盘空间不足。请降低容量阈值或换盘；需为ETL、转换文件和分享包预留空间。");
        try { await controller.Start(full, chosenPorts, (int)limit.Value, version.Text); }
        catch { displayed = controller.Session ?? displayed; throw; }
        displayed = controller.Session;
        lastExport = ""; settings.LastSession = Path.Combine(displayed!.DirectoryPath, "session.json");
        if (clearHistoricalNotes) notes.Clear();
        try
        {
            using var current = Process.GetCurrentProcess();
            using var watcher = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "--watchdog", Path.Combine(displayed!.DirectoryPath, "session.json"), current.Id.ToString(), current.StartTime.ToUniversalTime().Ticks.ToString() } })
                ?? throw new IOException("无法启动采集监护进程。");
        }
        catch
        {
            await controller.Stop("WatchdogFailed");
            throw new IOException("采集监护进程启动失败，已停止本次采集。请保留运行记录。");
        }
        var first = displayed!.OpenCase ?? throw new InvalidOperationException("第一段记录未创建，请保留本次采集目录。");
        var firstName = string.IsNullOrWhiteSpace(draftName) ? CaptureCaseName.Segment(1, caseTypeValue) : CaptureCaseName.Normalize(draftName);
        displayed.UpdateCaseDetails(first.Id, firstName, draftNotes, caseTypeValue);
        caseName.Text = firstName; notes.Text = draftNotes;
        log.Clear(); AppendLog("采集已启动。现在去启动游戏并登录；全部UDP模式也会包含其他应用的UDP流量。");
        try { settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppendLog("个人设置未保存：" + ex.Message); }
        noticeText = "抓包和第 1 段记录都已开始。现在登录游戏；操作做完点“完成本段”，下一段自动开始。";
        SetSettingsExpanded(false);
        RefreshCases();
    }
    private async Task StopCapture(string reason)
    {
        if (controller.Session?.OpenCase is { } item) controller.Session.UpdateCaseDetails(item.Id, CurrentCaseName(), notes.Text, caseTypeValue);
        await controller.Stop(reason);
        displayed = controller.Session; RefreshCases();
        if (displayed?.State is "Ready" or "NeedsAttention") await GenerateShare();
    }
    private async Task RecoverSession()
    {
        if (displayed is null) return;
        if (Elevate("--recover", Path.Combine(displayed.DirectoryPath, "session.json"))) return;
        await controller.ConvertAndCheck(displayed); RefreshCases(); await GenerateShare();
    }
    private async Task GenerateShare()
    {
        if (displayed is null) return;
        var parent = Directory.GetParent(displayed.DirectoryPath)?.FullName ?? displayed.DirectoryPath;
        var path = SessionFiles.NewSharePath(displayed, Path.Combine(parent, "分享包"));
        await ExportTo(path);
    }
    private async Task ExportTo(string path)
    {
        if (displayed is null) return;
        activity = "正在生成分享包…"; state.Text = activity;
        await Task.Run(() => SessionFiles.Export(displayed, path, ReportProgress));
        lastExport = path;
        noticeText = displayed.State == "NeedsAttention" ? "分享包已生成，但采集完整性有提示。请把 ZIP 和检查结果一起交给开发者。" : "分享包已生成，包含所有分段记录和登录上下文。点“查看分享包”即可找到 ZIP。";
        AppendLog("分享包已生成：" + path);
    }
    private static void RevealFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("分享包已移动或删除，请用“另存分享包”重新生成。", path);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select,", path } });
    }
    private async Task Tick()
    {
        if (busy || !controller.OwnsCapture) return;
        try
        {
            var bytes = controller.CapturedBytes;
            stats.Text = $"已采集 {controller.Elapsed.ToString(@"hh\:mm\:ss")} · 已用 {bytes / 1048576.0:N1} / {controller.Session!.MaxTotalMB:N0} MB · {controller.Session.Cases.Count} 段记录";
            foreach (DataGridViewRow row in cases.Rows)
                if (row.Tag is CaptureCase item && item.EndedUtc is null)
                    row.Cells["duration"].Value = TimeSpan.FromSeconds(Math.Max(0, controller.Elapsed.TotalSeconds - item.StartOffsetSeconds)).ToString(@"hh\:mm\:ss");
            RefreshCaseStatus(true, controller.Session.OpenCase is not null, controller.Session);
            if (bytes >= (long)controller.Session.MaxTotalMB * 1048576)
            {
                AppendLog("达到容量阈值，自动停止以保留登录数据。当前段操作可能未完整结束。");
                await Run(() => StopCapture("CapacityLimit"));
            }
        }
        catch (Exception ex) { AppendLog("容量检查失败：" + ex.Message); }
    }
    private async Task Run(Func<Task> action, string description = "正在处理…")
    {
        if (busy) return;
        busy = true; activity = description; noticeText = ""; RefreshButtons();
        try { await action(); }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            noticeText = ex.Message.Split('\n')[0].Trim();
            state.Text = "操作未完成 · 请查看提示"; state.ForeColor = Color.FromArgb(172, 108, 25); notice.Text = noticeText;
            MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { busy = false; if (!IsDisposed) RefreshButtons(); }
    }
    private void Sync(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppendLog(ex.Message); MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshButtons();
    }
    private void OpenSession()
    {
        using var dialog = new OpenFileDialog { Filter = "抓包记录 (session.json)|session.json", Title = "打开历史记录：选择抓包目录中的 session.json", InitialDirectory = root.Text };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Sync(() => LoadSession(dialog.FileName));
    }
    private void LoadSession(string path)
    {
        displayed = CaptureSession.Load(path); lastExport = ""; settings.LastSession = Path.GetFullPath(path); settings.Save();
        caseName.Clear(); notes.Clear(); noticeText = "历史记录已打开。可以查看案例、重新检查数据或另存分享包。";
        RefreshCases(); AppendLog("已打开历史记录：" + displayed.DirectoryPath);
    }
    internal static string HelpText()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("BH3Capture.Help")
            ?? throw new InvalidDataException("内置使用说明不存在。");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
    private void ShowHelp()
    {
        Sync(() =>
        {
            using var window = new Form { Text = "崩坏3 抓包助手 · 使用说明", Font = Font, AutoScaleDimensions = new SizeF(96, 96),
                AutoScaleMode = AutoScaleMode.Dpi, ClientSize = new Size(740, 580), StartPosition = FormStartPosition.CenterParent, Padding = new Padding(16),
                MinimizeBox = false, MaximizeBox = false };
            window.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White, Text = HelpText().ReplaceLineEndings("\r\n"), Font = Font });
            window.ShowDialog(this);
        });
    }
    private async Task ExportSession()
    {
        if (displayed is null) return;
        using var dialog = new SaveFileDialog { Filter = "抓包分享包 (*.zip)|*.zip", FileName = "崩坏3-capture-" + displayed.StartedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss") + ".zip", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(async () =>
        {
            await ExportTo(dialog.FileName);
        }, "正在生成分享包…");
    }
    private async Task RecheckSession()
    {
        if (displayed is null) return;
        AppendLog("重新读取所有分卷检查首包和KCP连续性和账号快照…");
        displayed.Report = await Task.Run(() => CaptureInspector.Inspect(displayed.PcapFiles.Select(p => SessionFiles.Resolve(displayed.DirectoryPath, p))));
        await Task.Run(() => AccountExport.Write(displayed.DirectoryPath, displayed.Report));
        displayed.State = displayed.Report.Passed && displayed.StopReason is not ("Interrupted" or "CapacityLimit") ? "Ready" : "NeedsAttention";
        SessionFiles.AtomicJson(Path.Combine(displayed.DirectoryPath, "check.json"), displayed.Report);
        await File.WriteAllTextAsync(Path.Combine(displayed.DirectoryPath, "check.txt"), displayed.Report.ToText());
        displayed.Save();
        noticeText = displayed.State == "Ready" ? "基础检查通过，KCP 连续性及账号快照正常。" : "检查已完成，有完整性提示；请查看左下方的检查结果。";
        AppendLog(displayed.Report.Passed ? "基础检查通过。" : "检查完成，存在完整性提示，见完整性检查页。");
    }
    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (preview || allowClose) return;
        if (busy) { e.Cancel = true; return; }
        if (!controller.OwnsCapture) { Sync(() => SaveSettings()); return; }
        e.Cancel = true;
        // Normal window close follows the same safe stop/convert path, with no extra approval dialog.
        await Run(() => StopCapture("WindowClosed"));
        if (!controller.OwnsCapture) { allowClose = true; Close(); }
    }
    private void FillDemo()
    {
        displayed = new() { StartedUtc = DateTimeOffset.UtcNow.AddHours(-1), State = "Ready", DirectoryPath = Path.GetTempPath(),
            PcapFiles = ["capture-0000.pcapng", "capture-0001.pcapng"],
            Report = new() { Passed = true, PacketCount = 18532, AccountCount = 1, Connections = [new() { Number = 1, ServerPort = 16100, ClientStartFound = true, ServerStartFound = true, ClientBytes = 245600, ServerBytes = 4862300 }] } };
        displayed.Cases.Add(new() { Name = "第一章 · 完整通关", StartedUtc = displayed.StartedUtc.AddMinutes(5), EndedUtc = displayed.StartedUtc.AddMinutes(25), StartOffsetSeconds = 300, EndOffsetSeconds = 1500, Notes = "组队 → 入场 → 首领 → 结算 → 回城" });
        displayed.Cases.Add(new() { Name = "女武神装备 · 第二次采集", StartedUtc = displayed.StartedUtc.AddMinutes(30), EndedUtc = displayed.StartedUtc.AddMinutes(55), StartOffsetSeconds = 1800, EndOffsetSeconds = 3300, Notes = "同一次登录，完整保留换频道和前置上下文。" });
        RefreshCases(); AppendLog("演示数据：两个案例共享完整会话。保存案例不会停止采集。"); AppendLog("转换与基础检查通过，可以导出分享包。");
    }
    internal void PreviewScene(string scene)
    {
        if (!preview) throw new InvalidOperationException("Only preview windows support demonstration scenes.");
        previewScene = scene; FillDemo(); noticeText = ""; caseName.Clear(); notes.Clear(); lastExport = ""; busy = false;
        SetCaseType(scene == "Recording" ? "装备 / 圣痕" : "其他操作");
        if (scene == "Idle") { displayed = null; notes.Clear(); }
        else if (scene is "Capturing" or "Recording" or "Paused")
        {
            displayed!.State = "Capturing"; displayed.Report = null; displayed.PcapFiles.Clear();
            if (scene == "Capturing")
            {
                displayed.Cases.Clear();
                var item = new CaptureCase { Name = CaptureCaseName.Segment(1, "其他操作"), Type = "其他操作", StartedUtc = displayed.StartedUtc, StartOffsetSeconds = 0 };
                displayed.Cases.Add(item); caseName.Text = item.Name;
                noticeText = "第一段已自动开始，现在登录并操作游戏。做完后点“完成本段”，不用再创建案例。";
            }
            else if (scene == "Recording")
            {
                var item = new CaptureCase { Name = "第 3 段 · 装备 / 圣痕", Type = "装备 / 圣痕", StartedUtc = DateTimeOffset.UtcNow, StartOffsetSeconds = 3400, Notes = "购买两次，观察成功或失败提示。" };
                displayed.Cases.Add(item); caseName.Text = item.Name; notes.Text = item.Notes;
            }
            stats.Text = "已采集 00:56:42 · 已用 21.1 / 4,096 MB";
        }
        else if (scene == "Recovery")
        { displayed!.State = "ConversionFailed"; noticeText = "上次转换未完成，原始数据仍在。点击修复后会重新转换并生成分享包。"; }
        else if (scene == "Converting")
        { displayed!.State = "Converting"; busy = true; activity = "正在转换分卷 1/2…"; }
        else
        { lastExport = Path.Combine(root.Text, "分享包", "崩坏3-capture-20261005-153143.zip"); noticeText = "分享包已生成，包含所有分段记录和登录上下文。点“查看分享包”即可找到 ZIP。"; }
        SetSettingsExpanded(scene == "Settings"); RefreshCases();
        if (mode.SelectedIndex < 0 || mode.Text.Length == 0 || caseTypeValue.Length == 0 || (caseTypeValue != "其他操作" && !caseType.Text.StartsWith(caseTypeValue))) throw new InvalidOperationException("Capture and case types must retain their selected values.");
        // These checks inspect the actual WinForms controls without invoking capture or UAC.
        if (scene == "Idle" && (!start.Enabled || begin.Enabled || stop.Visible)) throw new InvalidOperationException("Idle actions are incorrect.");
        if (scene == "Capturing" && (begin.Visible || start.Visible || !nextCase.Enabled || !stop.Enabled || displayed!.OpenCase is null || caseName.Text.Length == 0)) throw new InvalidOperationException("Capture must start with an open first segment.");
        if (scene == "Recording" && (!nextCase.Enabled || begin.Visible || !caseName.Enabled || !caseType.Enabled || !notes.Enabled || !stop.Enabled || !caseStatus.Text.Contains("记录中"))) throw new InvalidOperationException("Segment actions are incorrect.");
        if (scene == "Paused" && (!begin.Enabled || !begin.Visible || nextCase.Visible || !stop.Enabled)) throw new InvalidOperationException("Only segment markers should pause, not capture.");
        if (scene == "Ready" && (!export.Enabled || !showExport.Enabled || stop.Visible)) throw new InvalidOperationException("Export actions are incorrect.");
        if (scene == "Recovery" && (!recover.Enabled || !recover.Visible || !start.Enabled)) throw new InvalidOperationException("Recovery actions are incorrect.");
        if (scene == "Converting" && (start.Enabled || export.Enabled || !progress.Visible)) throw new InvalidOperationException("Busy actions are incorrect.");
    }
    internal void PreviewCaseInteractions()
    {
        PreviewScene("Capturing"); SetCaseType("女武神", updateCurrent: true);
        notes.Text = "第一次操作备注";
        if (displayed!.OpenCase?.Name != "第 1 段 · 女武神" || !caseStatus.Text.Contains("记录中")) throw new InvalidOperationException("Automatic first segment/type naming failed.");
        caseName.Text = "女武神一阶通关"; SetCaseType("装备 / 圣痕", updateCurrent: true);
        if (caseName.Text != "女武神一阶通关" || displayed.OpenCase.Type != "装备 / 圣痕") throw new InvalidOperationException("Type edits must preserve custom names.");
        SetCaseType("女武神", updateCurrent: true);
        caseName.Text = "女武神一阶通关"; notes.Text = "已通关"; nextCase.PerformClick();
        if (displayed.Cases[0].Name != "女武神一阶通关" || displayed.Cases[0].Notes != "已通关" || displayed.OpenCase?.Name != "第 2 段 · 女武神" || notes.Text.Length != 0 || !nextCase.Enabled)
            throw new InvalidOperationException("Save/next did not preserve edited metadata or clear the next draft.");
        if (displayed.Cases[0].EndedUtc != displayed.OpenCase.StartedUtc) throw new InvalidOperationException("Consecutive case boundaries differ.");
        caseName.Clear(); notes.Text = "第二次操作"; pauseCaseItem.PerformClick();
        if (displayed.OpenCase is not null || !begin.Enabled || !caseName.PlaceholderText.Contains("女武神") || notes.Text.Length != 0)
            throw new InvalidOperationException("Saving did not return to the next-case entry state.");
        if (!editCase.Enabled || !stop.Enabled) throw new InvalidOperationException("Saved metadata or continued capture actions are unavailable.");
        begin.PerformClick();
        if (displayed.OpenCase?.Name != "第 3 段 · 女武神" || !nextCase.Enabled || begin.Visible || notes.Text.Length != 0)
            throw new InvalidOperationException("Resuming manual markers must open the next segment without old draft text.");
    }
    internal void VerifyCaseAlignment()
    {
        if (!preview) throw new InvalidOperationException("Layout checks are only available in preview mode.");
        var controls = new List<Control> { caseType, caseName };
        if (begin.Visible) controls.Add(begin);
        else controls.Add(nextCase);
        var bounds = controls.Select(c => new Rectangle(c.Parent!.PointToScreen(c.Location), c.Size)).ToArray();
        if (bounds.Any(b => Math.Abs(b.Top - bounds[0].Top) > 1 || Math.Abs(b.Height - bounds[0].Height) > 1))
            throw new InvalidOperationException("Case type, name and action controls must have equal heights and aligned top edges.");
        if (bounds.Zip(bounds.Skip(1)).Any(pair => pair.First.Right > pair.Second.Left))
            throw new InvalidOperationException("Case controls overlap.");
    }
    protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); moreMenu.Dispose(); caseTypeMenu.Dispose(); tips.Dispose(); } base.Dispose(disposing); }
}
