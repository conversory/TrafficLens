using System.Diagnostics;
using Microsoft.Win32;

namespace TrafficLens;

internal sealed class MainForm : TrayForm
{
    private readonly TrafficMonitor monitor = new();
    private readonly AdapterSampler adapters = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly ComboBox adapterChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245 };
    private readonly ComboBox scopeChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 195 };
    private readonly ComboBox sortChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 135 };
    private readonly TextBox search = new() { Width = 170, PlaceholderText = "搜索程序 / PID / 路径" };
    private readonly DataGridView processes = Grid();
    private readonly DataGridView targets = Grid();
    private readonly DataGridView adapterGrid = Grid();
    private readonly TextBox pathBox = new() { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label explanation = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label diagnosis = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly RateGraph graph = new() { Dock = DockStyle.Fill };
    private readonly Button elevate = new() { Text = "开启程序监控", AutoSize = true };
    private readonly Button pause = new() { Text = "暂停显示", AutoSize = true };
    private readonly Label[] values = new Label[4];
    private readonly Label[] captions = new Label[4];
    private TrafficSnapshot? snapshot;
    private List<AdapterReading> adapterRows = [];
    private string? selectedKey;
    private string? selectedAdapter;
    private bool paused, filling, closing;
    private Task? metadata;
    private Color upColor = Color.FromArgb(216, 97, 53), downColor = Color.FromArgb(27, 132, 153);
    private TrafficScope Scope => (TrafficScope)Math.Max(0, scopeChoice.SelectedIndex);
    internal object Diagnostics => new { DeviceDpi, ClientSize, Size, Text, MonitorRunning = monitor.IsRunning,
        ProcessRows = processes.Rows.Count, TargetRows = targets.Rows.Count, Status = status.Text, Passed = true };

    public MainForm()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "系统流量 · TrafficLens 1.5";
        Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(1360, 900);
        MinimumSize = new Size(1060, 750);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 12, 22, 12), ColumnCount = 1, RowCount = 8 };
        foreach (var height in new[] { 66f, 58f, 112f, 112f, 44f, 40f }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 32));
        Controls.Add(layout);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        header.RowStyles.Add(new(SizeType.Absolute, 36)); header.RowStyles.Add(new(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = "流量侦探", Font = new(Font.FontFamily, 22, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
        header.Controls.Add(new Label { Text = "看看谁在上传，以及数据去了哪里", Dock = DockStyle.Fill }, 0, 1);
        layout.Controls.Add(header, 0, 0);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        var export = new Button { Text = "导出 CSV", AutoSize = true };
        var clear = new Button { Text = "重新计数", AutoSize = true };
        var folder = new Button { Text = "查看程序位置", AutoSize = true };
        var help = new Button { Text = "使用说明", AutoSize = true };
        var clash = new Button { Text = "Clash 明细", AutoSize = true };
        foreach (var c in new Control[] { clash, elevate, pause, export, clear, folder, help }) { c.Margin = new Padding(0, 0, 10, 0); toolbar.Controls.Add(c); }
        clash.Click += (_, _) => TrayApplication.OpenWindow<ClashForm>(this);
        layout.Controls.Add(toolbar, 0, 1);
        elevate.Click += (_, _) => Elevate(); pause.Click += (_, _) => { paused = !paused; pause.Text = paused ? "继续显示" : "暂停显示"; if (!paused) RefreshData(); };
        export.Click += (_, _) => Export();
        clear.Click += (_, _) => { monitor.Store.Clear(); adapters.Clear(); graph.Clear(); RefreshData(); };
        folder.Click += (_, _) => OpenLocation();
        help.Click += (_, _) => MessageBox.Show(this,
            "1. 点击“开启程序监控”，在 Windows 权限提示中选择“是”。\n2. 选择实际联网的 Wi-Fi 或以太网卡，观察顶部上传速度。\n3. 下方按“累计上传”排序，观察 1–2 分钟，选中靠前的程序查看目标地址和路径。\n4. 有本地代理时，切换到“本机通信”可追查是谁把数据交给代理。\n\n网卡统计与程序统计口径不同；VPN、代理和虚拟网卡可能产生多层流量，请勿把程序流量直接相加当作外网总量。\n\n累计量从启动或“重新计数”开始；关闭窗口后在托盘继续采集；右键托盘“退出”才会停止并清除本次内存记录。加密流量只能看到程序、IP、端口和字节量，不能识别具体文件内容。软件只保存手动导出的报告。", "如何定位上传来源", MessageBoxButtons.OK, MessageBoxIcon.Information);

        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        for (int i = 0; i < 4; i++)
        {
            cards.ColumnStyles.Add(new(SizeType.Percent, 25));
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 8, 4, 8), Margin = new Padding(0, 0, 10, 8) };
            card.RowStyles.Add(new(SizeType.Absolute, 28)); card.RowStyles.Add(new(SizeType.Percent, 100));
            captions[i] = new Label { Text = new[] { "当前上传 · 网卡", "当前下载 · 网卡", "本次累计上传 · 网卡", "本次累计下载 · 网卡" }[i], Dock = DockStyle.Fill };
            values[i] = new Label { Text = "—", Font = new(Font.FontFamily, 24, FontStyle.Bold), Dock = DockStyle.Fill, ForeColor = i % 2 == 0 ? upColor : downColor };
            card.Controls.Add(captions[i], 0, 0); card.Controls.Add(values[i], 0, 1); cards.Controls.Add(card, i, 0);
        }
        layout.Controls.Add(cards, 0, 2); layout.Controls.Add(graph, 0, 3);
        var networkBar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 7, 0, 0) };
        networkBar.Controls.Add(new Label { Text = "网卡", AutoSize = true, Margin = new Padding(0, 4, 8, 0) });
        networkBar.Controls.Add(adapterChoice);
        explanation.Text = "网卡与程序统计分别显示；代理转发可能重复出现。";
        explanation.Dock = DockStyle.None; explanation.Size = new(800, 28); explanation.Margin = new Padding(12, 0, 0, 0);
        networkBar.Controls.Add(explanation); layout.Controls.Add(networkBar, 0, 4);
        adapterChoice.SelectedIndexChanged += (_, _) => { if (adapterChoice.SelectedItem is AdapterItem a && a.Id != selectedAdapter) { selectedAdapter = a.Id; graph.Clear(); UpdateCards(); } };

        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
        scopeChoice.Items.AddRange(["外部连接（不含本机）", "仅公网目标", "本机通信（含本地代理）", "全部（可能重复计数）"]); scopeChoice.SelectedIndex = 0;
        sortChoice.Items.AddRange(["累计上传 ↓", "当前上传 ↓", "累计下载 ↓", "当前下载 ↓"]); sortChoice.SelectedIndex = 0;
        foreach (var c in new Control[] { scopeChoice, sortChoice, search }) { c.Margin = new Padding(0, 0, 10, 0); filters.Controls.Add(c); }
        diagnosis.Dock = DockStyle.None; diagnosis.Size = new(610, 27); filters.Controls.Add(diagnosis);
        layout.Controls.Add(filters, 0, 5);
        scopeChoice.SelectedIndexChanged += (_, _) => Refilter(); sortChoice.SelectedIndexChanged += (_, _) => FillProcesses(); search.TextChanged += (_, _) => FillProcesses();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var procTab = new TabPage("程序流量排行"); var netTab = new TabPage("全部网卡"); tabs.TabPages.AddRange([procTab, netTab]);
        var split = new SplitContainer { Size = new Size(1000, 400), Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Panel1MinSize = 100, Panel2MinSize = 100 };
        procTab.Controls.Add(split); split.Panel1.Controls.Add(processes);
        var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 8, 0, 0) };
        detail.RowStyles.Add(new(SizeType.Absolute, 34)); detail.RowStyles.Add(new(SizeType.Percent, 100));
        detail.Controls.Add(pathBox, 0, 0); detail.Controls.Add(targets, 0, 1); split.Panel2.Controls.Add(detail);
        netTab.Controls.Add(adapterGrid); layout.Controls.Add(tabs, 0, 6); layout.Controls.Add(status, 0, 7);
        AddColumns(processes, ("程序", 190), ("PID", 65), ("上传 / 秒", 110), ("下载 / 秒", 110), ("累计上传", 110), ("累计下载", 110), ("目标数", 70), ("状态", 75), ("程序路径", 260));
        AddColumns(targets, ("协议", 75), ("目标地址", 265), ("端口", 70), ("类型", 140), ("累计上传", 125), ("累计下载", 125), ("最近活动", 110));
        AddColumns(adapterGrid, ("网卡", 160), ("设备", 350), ("上传 / 秒", 135), ("下载 / 秒", 135), ("本次上传", 135), ("本次下载", 135));
        processes.SelectionChanged += (_, _) => { if (!filling) { selectedKey = processes.CurrentRow?.Tag as string; FillTargets(); } };
        processes.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenLocation(); };
        processes.ColumnHeaderMouseClick += (_, e) => { if (e.ColumnIndex is >= 2 and <= 5) sortChoice.SelectedIndex = e.ColumnIndex switch { 2 => 1, 3 => 3, 4 => 0, _ => 2 }; };
        var menu = new ContextMenuStrip(); menu.Items.Add("查看程序位置", null, (_, _) => OpenLocation()); menu.Items.Add("复制程序路径", null, (_, _) => CopyPath()); processes.ContextMenuStrip = menu;
        timer.Tick += (_, _) => RefreshData();
        Shown += async (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width - 32), Math.Min(MinimumSize.Height, area.Height - 32));
            Size = new Size(Math.Min(Width, area.Width - 32), Math.Min(Height, area.Height - 32));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            split.SplitterDistance = Math.Max(100, split.Height * 58 / 100);
            adapterRows = adapters.Sample(); RefreshAdapterList(); UpdateCards();
            status.Text = "正在启动监控…";
            await Task.Run(monitor.Start);
            if (closing) return;
            elevate.Text = monitor.IsRunning ? "程序监控已开启" : "开启程序监控";
            elevate.Enabled = !monitor.IsRunning;
            timer.Start(); RefreshData();
        };
        FormClosing += (_, _) => { closing = true; timer.Stop(); monitor.Dispose(); };
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
        FormClosed += (_, _) => { SystemEvents.UserPreferenceChanged -= PreferenceChanged; timer.Dispose(); };
        ApplyTheme();
        ResumeLayout(true);
    }
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (IsHandleCreated && !closing) BeginInvoke((Action)ApplyTheme);
    }
    private void ApplyTheme()
    {
        bool dark = false;
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); dark = (int?)key?.GetValue("AppsUseLightTheme") == 0; } catch { }
        var background = dark ? Color.FromArgb(25, 29, 36) : Color.FromArgb(245, 247, 250);
        var surface = dark ? Color.FromArgb(33, 38, 47) : Color.White;
        var text = dark ? Color.FromArgb(230, 233, 240) : Color.FromArgb(31, 43, 58);
        void Theme(Control c)
        {
            c.BackColor = c is DataGridView or TextBox or ComboBox ? surface : background; c.ForeColor = text;
            if (c is DataGridView g)
            {
                g.BackgroundColor = surface; g.DefaultCellStyle.BackColor = surface; g.DefaultCellStyle.ForeColor = text;
                g.AlternatingRowsDefaultCellStyle.BackColor = dark ? Color.FromArgb(38, 43, 51) : Color.FromArgb(248, 250, 252);
                g.ColumnHeadersDefaultCellStyle.BackColor = background; g.ColumnHeadersDefaultCellStyle.ForeColor = text;
                g.DefaultCellStyle.SelectionBackColor = dark ? Color.FromArgb(47, 71, 88) : Color.FromArgb(225, 239, 246);
                g.DefaultCellStyle.SelectionForeColor = text; g.GridColor = background;
            }
            foreach (Control child in c.Controls) Theme(child);
        }
        Theme(this); for (int i = 0; i < 4; i++) values[i].ForeColor = i % 2 == 0 ? upColor : downColor;
        graph.UpColor = upColor; graph.DownColor = downColor; graph.Invalidate();
    }
    internal static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, BorderStyle = BorderStyle.None,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, EnableHeadersVisualStyles = false,
        RowTemplate = { Height = 31 }, ColumnHeadersHeight = 36, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
    };
    internal static void AddColumns(DataGridView grid, params (string Title, int Width)[] columns)
    {
        foreach (var (title, width) in columns)
        {
            int i = grid.Columns.Add("c" + grid.Columns.Count, title);
            grid.Columns[i].FillWeight = width; grid.Columns[i].MinimumWidth = Math.Min(width, 85);
            grid.Columns[i].SortMode = DataGridViewColumnSortMode.Programmatic;
        }
    }
    private void RefreshData()
    {
        if (paused) { status.Text = "显示已暂停 · 后台仍在累计流量；点击“继续显示”恢复。"; return; }
        adapterRows = adapters.Sample(); RefreshAdapterList(); UpdateCards();
        if (metadata == null || metadata.IsCompleted) metadata = Task.Run(monitor.Store.ResolveNames);
        snapshot = monitor.Store.Snapshot(Scope, monitor.LostEvents, monitor.Error);
        FillProcesses();
        adapterGrid.Rows.Clear(); foreach (var a in adapterRows) adapterGrid.Rows.Add(a.Name, a.Description, Units.Rate(a.UpRate), Units.Rate(a.DownRate), Units.Bytes(a.Uploaded), Units.Bytes(a.Downloaded));
        var leader = snapshot.Processes.OrderByDescending(p => p.Uploaded).FirstOrDefault();
        diagnosis.Text = leader == null ? (monitor.IsRunning ? "等待程序产生流量…" : "开启程序监控后显示上传排行") : $"累计上传最多：{leader.Name} · {Units.Bytes(leader.Uploaded)}";
        status.Text = monitor.Error ?? $"监控中 · 从 {snapshot.Started:HH:mm:ss} 开始 · {snapshot.Processes.Count} 个程序 · {snapshot.Events:N0} 条网络事件 · 丢失 {snapshot.LostEvents:N0} 条";
        if (snapshot.LostEvents > 0) status.Text += "（统计可能偏少）";
    }
    private sealed record AdapterItem(string Id, string Text) { public override string ToString() => Text; }
    private void RefreshAdapterList()
    {
        var ids = adapterRows.Select(a => a.Id).ToArray();
        var old = adapterChoice.Items.Cast<AdapterItem>().Select(a => a.Id).ToArray();
        if (ids.SequenceEqual(old)) return;
        adapterChoice.Items.Clear();
        foreach (var a in adapterRows) adapterChoice.Items.Add(new AdapterItem(a.Id, a.Name + (a.PhysicalCandidate ? " · 物理网卡候选" : "")));
        int index = adapterRows.FindIndex(a => a.Id == selectedAdapter);
        if (index < 0) index = adapterRows.Count == 0 ? -1 : 0;
        adapterChoice.SelectedIndex = index;
        if (index < 0) selectedAdapter = null;
    }
    private void UpdateCards()
    {
        var a = adapterRows.FirstOrDefault(a => a.Id == selectedAdapter);
        if (a == null) { foreach (var v in values) v.Text = "—"; return; }
        values[0].Text = Units.Rate(a.UpRate); values[1].Text = Units.Rate(a.DownRate);
        values[2].Text = Units.Bytes(a.Uploaded); values[3].Text = Units.Bytes(a.Downloaded);
        explanation.Text = a.PhysicalCandidate ? "单块网卡计数 · 本次上传/下载比 " + (a.Downloaded > 0 ? ((double)a.Uploaded / a.Downloaded).ToString("0.00") : "—") : "当前为虚拟或未识别网卡；请确认实际联网网卡，避免重复计数。";
        graph.Push(a.UpRate, a.DownRate);
    }
    private void Refilter()
    {
        snapshot = monitor.Store.Snapshot(Scope, monitor.LostEvents, monitor.Error, false); FillProcesses();
    }
    private void FillProcesses()
    {
        if (snapshot == null) return;
        var filter = search.Text.Trim();
        IEnumerable<TrafficRow> rows = snapshot.Processes.Where(p => filter == "" || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || p.Pid.ToString().Contains(filter) || p.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));
        rows = sortChoice.SelectedIndex switch { 1 => rows.OrderByDescending(p => p.UploadRate), 2 => rows.OrderByDescending(p => p.Downloaded), 3 => rows.OrderByDescending(p => p.DownloadRate), _ => rows.OrderByDescending(p => p.Uploaded) };
        int scroll = processes.FirstDisplayedScrollingRowIndex;
        filling = true;
        processes.Rows.Clear();
        int selectedIndex = -1;
        foreach (var p in rows)
        {
            int index = processes.Rows.Add(p.Name, p.Pid, Units.Rate(p.UploadRate), Units.Rate(p.DownloadRate), Units.Bytes(p.Uploaded), Units.Bytes(p.Downloaded), p.Targets, p.Running ? "运行中" : "已结束", p.Path);
            processes.Rows[index].Tag = p.Key;
            if (p.Key == selectedKey) selectedIndex = index;
        }
        if (processes.Rows.Count > 0)
        {
            selectedIndex = Math.Max(0, selectedIndex); processes.CurrentCell = processes.Rows[selectedIndex].Cells[0];
            processes.Rows[selectedIndex].Selected = true; selectedKey = processes.Rows[selectedIndex].Tag as string;
            if (scroll >= 0 && scroll < processes.Rows.Count) processes.FirstDisplayedScrollingRowIndex = scroll;
        }
        else selectedKey = null;
        filling = false; FillTargets();
    }
    private void FillTargets()
    {
        targets.Rows.Clear();
        var p = snapshot?.Processes.FirstOrDefault(p => p.Key == selectedKey);
        pathBox.Text = p == null ? "选择上方程序，查看它的目标 IP、端口和路径。" : $"{p.Name} · PID {p.Pid}  |  " + (p.Path == "" ? "程序路径暂不可读取" : p.Path);
        if (p == null || snapshot == null) return;
        foreach (var t in snapshot.Targets.Where(t => t.ProcessKey == p.Key).OrderByDescending(t => t.Uploaded))
            targets.Rows.Add(t.Protocol, t.Address, t.Port == 0 ? "—" : t.Port.ToString(), Units.Kind(t.Kind), Units.Bytes(t.Uploaded), Units.Bytes(t.Downloaded), t.LastSeen.ToString("HH:mm:ss"));
    }
    private void Elevate()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--system") { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            CloseForReplacement();
        }
        catch (Exception ex) { MessageBox.Show(this, "未开启管理员监控。网卡速度仍可查看。\n" + ex.Message, "开启程序监控", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
    private TrafficRow? Selected => snapshot?.Processes.FirstOrDefault(p => p.Key == selectedKey);
    private void OpenLocation()
    {
        var p = Selected;
        if (p == null || p.Path == "" || !File.Exists(p.Path)) { MessageBox.Show(this, "请先选择有可读取程序路径的进程。", "程序位置"); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + p.Path + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
    private void CopyPath() { if (Selected is { Path.Length: > 0 } p) Clipboard.SetText(p.Path); }
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV 报告 (*.csv)|*.csv", FileName = "流量报告-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv", InitialDirectory = AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var current = monitor.Store.Snapshot(Scope, monitor.LostEvents, monitor.Error, false);
            Reports.WriteCsv(dialog.FileName, current, adapterRows, Scope);
            status.Text = "已导出：" + dialog.FileName;
        }
        catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message); }
    }
}

internal static class Units
{
    public static string Bytes(double value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0; while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return value.ToString(i == 0 ? "0" : "0.0") + " " + units[i];
    }
    public static string Rate(double value) => Bytes(value) + "/s";
    public static string Kind(TrafficKind kind) => kind switch { TrafficKind.Local => "本机通信", TrafficKind.Lan => "局域网 / 保留地址", TrafficKind.Proxy => "代理虚拟地址", _ => "公网目标" };
}

internal sealed class RateGraph : Control
{
    private readonly Queue<(double Up, double Down)> samples = new();
    public Color UpColor, DownColor;
    public RateGraph() { DoubleBuffered = true; }
    public void Push(double up, double down) { samples.Enqueue((up, down)); while (samples.Count > 120) samples.Dequeue(); Invalidate(); }
    public void Clear() { samples.Clear(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var data = samples.ToArray(); var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var text = new SolidBrush(ForeColor);
        g.DrawString("最近 120 秒 · 网卡速度", Font, text, 10, 0);
        using var up = new Pen(UpColor, 2);
        using var down = new Pen(DownColor, 2);
        g.DrawLine(up, Width - 190, 12, Width - 168, 12); g.DrawString("上传", Font, text, Width - 161, 2);
        g.DrawLine(down, Width - 96, 12, Width - 74, 12); g.DrawString("下载", Font, text, Width - 68, 2);
        float left = Math.Max(92, TextRenderer.MeasureText("999.9 MB/s", Font).Width + 12);
        float top = Font.Height + 12, width = Math.Max(1, Width - left - 10), height = Math.Max(1, Height - top - Font.Height - 12);
        double max = Math.Max(1024, data.Select(s => Math.Max(s.Up, s.Down)).DefaultIfEmpty(0).Max() * 1.15);
        using var grid = new Pen(Color.FromArgb(40, ForeColor));
        for (int i = 0; i <= 2; i++) { float y = top + i * height / 2; g.DrawLine(grid, left, y, left + width, y); g.DrawString(Units.Rate(max * (1 - i / 2.0)), Font, text, 0, y - 8); }
        if (data.Length < 2) return;
        PointF[] Points(bool upload) => data.Select((s, i) => new PointF(left + (120 - data.Length + i) * width / 119, top + height - (float)((upload ? s.Up : s.Down) / max * height))).ToArray();
        g.DrawLines(down, Points(false)); g.DrawLines(up, Points(true));
    }
}
