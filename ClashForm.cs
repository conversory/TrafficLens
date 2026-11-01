using System.Diagnostics;
using Microsoft.Win32;

namespace TrafficLens;

internal sealed class ClashForm : TrayForm
{
    private readonly ClashStore store = new();
    private readonly DailyHistory history = new(HistoryPaths.DirectoryPath);
    private readonly HistoryPanel historyPanel;
    private readonly Label saveStatus = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly CancellationTokenSource stopping = new();
    private DateTime nextRender;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly DataGridView ranking = MainForm.Grid(), details = MainForm.Grid(), summary = MainForm.Grid(), domains = MainForm.Grid();
    private readonly TabControl views = new() { Dock = DockStyle.Fill }, detailViews = new() { Dock = DockStyle.Fill };
    private readonly ComboBox grouping = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly ComboBox routeFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly Label[] routeValues = new Label[3];
    private readonly TextBox search = new() { PlaceholderText = "搜索域名 / 程序 / IP / 节点", Width = 240 };
    private readonly CheckBox uploadsOnly = new() { Text = "只看有上传", AutoSize = true };
    private readonly CheckBox activeOnly = new() { Text = "仅活动连接", AutoSize = true };
    private readonly CheckBox startup = new() { Text = "开机自启", AutoSize = true };
    private readonly Label connectionStatus = new() { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label status = new() { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox selectionInfo = new() { ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill };
    private readonly Label[] values = new Label[4];
    private readonly Label empty = new() { Text = "正在读取 Clash；同名程序的连接会合并为一行…", Dock = DockStyle.Bottom, Height = 30 };
    private ClashApi? api;
    private List<ClashGroup> groups = [];
    private List<ClashGroup> programGroups = [];
    private string? selected;
    private string? selectedProgram;
    private bool programSelectionPinned;
    private bool IsProgramSummary => views.SelectedIndex == 0;
    private bool IsHistory => views.SelectedIndex == 2;
    private int CurrentGrouping => IsProgramSummary ? 2 : grouping.SelectedIndex;
    private string? CurrentKey => IsProgramSummary ? selectedProgram : selected;
    private DateTime nextRetry;
    private bool busy, closing, filling, paused, reconnect;
    private readonly Button pause = new() { Text = "暂停显示", AutoSize = true };
    internal long Frames => store.Current.Frames;
    internal object Diagnostics => new { DeviceDpi, ClientSize, Size, Text, ClashConnected = api != null,
        Frames = store.Current.Frames, RankingRows = ranking.Rows.Count, DetailRows = details.Rows.Count,
        ProgramSummaryRows = summary.Rows.Count, DomainRows = domains.Rows.Count, DefaultView = views.SelectedTab?.Text,
        ProgramNames = programGroups.Select(g => g.Process).ToArray(),
        ProgramTotalsReconcile = ClashStore.Group(store.Current, 2, "", false, false).Sum(g => g.Uploaded) == store.Current.Connections.Sum(c => c.Uploaded)
            && ClashStore.Group(store.Current, 2, "", false, false).Sum(g => g.Downloaded) == store.Current.Connections.Sum(c => c.Downloaded),
        RouteTotals = store.Current.Routes,
        RouteTotalsReconcile = store.Current.Routes.Uploaded == store.Current.Connections.Sum(c => c.Uploaded) + store.Current.FoldedUploaded
            && store.Current.Routes.Downloaded == store.Current.Connections.Sum(c => c.Downloaded) + store.Current.FoldedDownloaded,
        TrayVisible = TrayApplication.IconVisible, Visible, Startup = StartupRegistration.Read(),
        HistoryDirectory = history.DirectoryPath, HistoryWritable = history.CanWrite, HistorySaved = history.LastSaved,
        HistoryError = history.Error, HistoryRecovery = history.RecoveryNotice, HistoryDay = historyPanel.SelectedDate.ToString("yyyy-MM-dd"), HistoryRows = historyPanel.Rows,
        HistoryUploaded = historyPanel.Uploaded, HistoryDownloaded = historyPanel.Downloaded, HistoryInfo = historyPanel.Info,
        store.Current.Uploaded, store.Current.Downloaded, Status = status.Text, Passed = api != null && store.Current.Frames > 0 };

    public ClashForm()
    {
        historyPanel = new(history); store.Sampled += history.Apply;
        SuspendLayout(); AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Clash 程序流量汇总 · 流量侦探 1.6"; Font = new Font("Microsoft YaHei UI", 10);
        ClientSize = new Size(1360, 900); MinimumSize = new Size(1050, 720); StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 12, 22, 10), ColumnCount = 1, RowCount = 8 };
        foreach (float h in new[] { 66f, 60f, 96f, 54f, 36f, 76f }) layout.RowStyles.Add(new(SizeType.Absolute, h));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 42)); Controls.Add(layout);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        heading.RowStyles.Add(new(SizeType.Absolute, 36)); heading.RowStyles.Add(new(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "Clash 程序流量汇总", Font = new(Font.FontFamily, 22, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
        heading.Controls.Add(new Label { Text = "同名程序合并 · 代理 / VPN 与直连分开统计 · 后台持续监控 · 每日历史自动保存", Dock = DockStyle.Fill }, 0, 1); layout.Controls.Add(heading, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        var retry = new Button { Text = "重新连接", AutoSize = true }; var reset = new Button { Text = "重新计数", AutoSize = true };
        var export = new Button { Text = "导出 CSV", AutoSize = true }; var system = new Button { Text = "系统流量", AutoSize = true };
        var help = new Button { Text = "说明", AutoSize = true }; var background = new Button { Text = "后台监控", AutoSize = true };
        background.Click += (_, _) => TrayApplication.HideWindow(this);
        foreach (var c in new Control[] { retry, pause, reset, export, system, background, help }) { c.Margin = new Padding(0, 0, 10, 0); toolbar.Controls.Add(c); }
        connectionStatus.Dock = DockStyle.None; connectionStatus.Size = new Size(510, 36); toolbar.Controls.Add(connectionStatus); layout.Controls.Add(toolbar, 0, 1);
        retry.Click += async (_, _) => { reconnect = true; nextRetry = DateTime.MinValue; await PollAsync(); };
        pause.Click += (_, _) => { paused = !paused; pause.Text = paused ? "继续显示" : "暂停显示"; if (!paused) Render(); };
        reset.Click += (_, _) => { store.Clear(); Render(); }; export.Click += (_, _) => { if (IsHistory) historyPanel.Export(); else Export(); };
        system.Click += (_, _) => TrayApplication.OpenWindow<MainForm>(this);
        help.Click += (_, _) => MessageBox.Show(this,
            "点右上角关闭或“后台监控”会隐藏到系统托盘，后台继续计数。双击托盘图标恢复；右键“退出流量侦探”才会结束。每日程序和线路汇总每 5 秒自动保存，退出和重启后可在“每日历史”按日期查看。实时连接明细仍为本次运行记录；“重新计数”不删除历史。\n\n线路按实际出口及其类型判断：代理 / VPN、直连、其他 / 未知。上方线路卡为全部已归因量，筛选只影响下方表格。仅统计经过 Clash 的连接；绕过 Clash 的连接请看系统流量。\n\n默认显示“程序汇总”：同名可执行文件的不同进程、域名和路径合并为一行，大小写和 .exe 后缀差异也会合并。点击本次上传、本次下载、上传/秒或下载/秒列标题可排序。\n\n选中程序后，下方“域名明细”列出它访问的域名；“连接明细”保留具体连接和路径。上方“域名 / 连接排行”保留原来的视图。不同路径的同名程序会显示路径数。\n\n想查 ab.chatgpt.com：在搜索框输入域名；汇总随筛选变化，清空搜索可看完整程序总量。\n\n顶部来自 Clash 内核计数，下方来自采样到的连接增量。短连接可能漏采，底部显示未归因差额。连接累计包含监控开启前的部分；本次量只计算观察期间增量。\n\n软件只读本机接口，不修改代理配置。密钥不写入报告。HTTPS 正文和具体文件内容不可见。", "如何阅读程序汇总", MessageBoxButtons.OK, MessageBoxIcon.Information);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        string[] titles = ["当前上传 · Clash 内核", "当前下载 · Clash 内核", "本次观察上传 · 内核", "本次观察下载 · 内核"];
        for (int i = 0; i < 4; i++)
        {
            cards.ColumnStyles.Add(new(SizeType.Percent, 25));
            var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10, 4, 4, 8) };
            card.RowStyles.Add(new(SizeType.Absolute, 28)); card.RowStyles.Add(new(SizeType.Percent, 100));
            card.Controls.Add(new Label { Text = titles[i], Dock = DockStyle.Fill }, 0, 0);
            values[i] = new Label { Text = "—", Font = new(Font.FontFamily, 23, FontStyle.Bold), Dock = DockStyle.Fill };
            card.Controls.Add(values[i], 0, 1); cards.Controls.Add(card, i, 0);
        }
        layout.Controls.Add(cards, 0, 2);
        var routeCards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        for (int i = 0; i < routeValues.Length; i++)
        {
            routeCards.ColumnStyles.Add(new(SizeType.Percent, 100f / 3));
            routeValues[i] = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0), AutoEllipsis = true };
            routeCards.Controls.Add(routeValues[i], i, 0);
        }
        layout.Controls.Add(routeCards, 0, 3);
        layout.Controls.Add(new Label { Text = "线路统计来自采样连接，含已结束连接 · 短连接可能漏采，未归因差额不强行分配线路", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(0, 5, 0, 0) };
        grouping.Items.AddRange(["程序 + 域名", "按域名", "按程序（同名合并）"]); grouping.SelectedIndex = 0;
        sort.Items.AddRange(["本次上传 ↓", "当前上传 ↓", "本次下载 ↓", "当前下载 ↓", "连接累计上传 ↓", "代理上传 ↓", "代理下载 ↓", "直连上传 ↓", "直连下载 ↓", "其他上传 ↓", "其他下载 ↓"]); sort.SelectedIndex = 0;
        routeFilter.Items.AddRange(["全部线路", "只看代理 / VPN", "只看直连", "其他 / 未知"]); routeFilter.SelectedIndex = 0;
        // Native DropDownList ignores BackColor under some Windows dark themes.
        foreach (var combo in new[] { grouping, sort, routeFilter })
        {
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.ItemHeight = Font.Height + 4;
            combo.DrawItem += (_, e) =>
            {
                using var brush = new SolidBrush(combo.BackColor);
                e.Graphics.FillRectangle(brush, e.Bounds);
                string text = e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) ?? "" : combo.Text;
                TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds, combo.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                e.DrawFocusRectangle();
            };
        }
        var searchLabel = new Label { Text = "搜索", AutoSize = true };
        foreach (var c in new Control[] { grouping, sort, routeFilter, searchLabel, search, uploadsOnly, activeOnly, startup }) { c.Margin = new Padding(0, 0, 12, 0); filters.Controls.Add(c); }
        startup.Margin = new Padding(0, 4, 0, 0); StartupRegistration.Bind(startup, this);
        searchLabel.Margin = new Padding(0, 4, 8, 0);
        uploadsOnly.Margin = new Padding(0, 4, 12, 0); activeOnly.Margin = new Padding(0, 4, 0, 0); layout.Controls.Add(filters, 0, 5);
        routeFilter.SelectedIndexChanged += (_, _) => Render();
        grouping.SelectedIndexChanged += (_, _) => Render(); sort.SelectedIndexChanged += (_, _) => Render();
        search.TextChanged += (_, _) => { if (search.TextLength > 0) uploadsOnly.Checked = false; Render(); };
        uploadsOnly.CheckedChanged += (_, _) => Render(); activeOnly.CheckedChanged += (_, _) => Render();
        var split = new SplitContainer { Size = new Size(1000, 500), Orientation = Orientation.Horizontal, Dock = DockStyle.Fill, Panel1MinSize = 100, Panel2MinSize = 120 };
        var summaryTab = new TabPage("程序汇总（同名合并）"); var rankingTab = new TabPage("域名 / 连接排行"); var historyTab = new TabPage("每日历史（自动保存）");
        summaryTab.Controls.Add(summary); rankingTab.Controls.Add(ranking); historyTab.Controls.Add(historyPanel); views.TabPages.AddRange([summaryTab, rankingTab, historyTab]);
        split.Panel1.Controls.Add(views); split.Panel1.Controls.Add(empty);
        var detailLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 8, 0, 0) };
        detailLayout.RowStyles.Add(new(SizeType.Absolute, 36)); detailLayout.RowStyles.Add(new(SizeType.Percent, 100));
        var domainsTab = new TabPage("选中程序的域名明细"); var connectionsTab = new TabPage("连接明细");
        domainsTab.Controls.Add(domains); connectionsTab.Controls.Add(details); detailViews.TabPages.AddRange([domainsTab, connectionsTab]);
        detailLayout.Controls.Add(selectionInfo, 0, 0); detailLayout.Controls.Add(detailViews, 0, 1); split.Panel2.Controls.Add(detailLayout);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new(SizeType.Percent, 72)); footer.ColumnStyles.Add(new(SizeType.Percent, 28));
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(saveStatus, 1, 0);
        layout.Controls.Add(split, 0, 6); layout.Controls.Add(footer, 0, 7);
        MainForm.AddColumns(ranking, ("程序", 145), ("域名 / 目标", 235), ("本次上传", 105), ("本次下载", 105), ("上传 / 秒", 105), ("下载 / 秒", 105), ("活动 / 观察", 100), ("代理链", 250));
        MainForm.AddColumns(summary, ("程序名（同名合并）", 190), ("本次上传", 110), ("本次下载", 110), ("代理上传", 110), ("代理下载", 110),
            ("直连上传", 110), ("直连下载", 110), ("其他上传", 100), ("其他下载", 100), ("上传 / 秒", 110), ("下载 / 秒", 110), ("域名数", 80), ("活动 / 观察", 110), ("程序路径数", 100));
        MainForm.AddColumns(domains, ("域名 / 目标", 280), ("本次上传", 130), ("本次下载", 130), ("上传 / 秒", 120), ("下载 / 秒", 120), ("活动 / 观察", 105), ("代理链", 260));
        MainForm.AddColumns(details, ("线路", 110), ("状态", 70), ("目标 IP:端口", 200), ("协议", 70), ("本次上传", 100), ("本次下载", 100), ("连接累计上传", 125), ("连接累计下载", 125), ("规则 / 代理链", 290), ("来源", 180));
        ranking.SelectionChanged += (_, _) => { if (!filling) { selected = ranking.CurrentRow?.Tag as string; RenderDetails(); } };
        summary.SelectionChanged += (_, _) => { if (!filling) { programSelectionPinned = true; selectedProgram = summary.CurrentRow?.Tag as string; RenderDetails(); } };
        summary.ColumnHeaderMouseClick += (_, e) => { if (e.ColumnIndex is >= 1 and <= 10) sort.SelectedIndex = e.ColumnIndex switch { 1 => 0, 2 => 2, 9 => 1, 10 => 3, _ => e.ColumnIndex + 2 }; };
        views.SelectedIndexChanged += (_, _) =>
        {
            grouping.Visible = views.SelectedIndex == 1; filters.Visible = !IsHistory;
            layout.RowStyles[5].Height = IsHistory ? 0 : 76 * DeviceDpi / 96f;
            split.Panel2Collapsed = IsHistory; Render();
        };
        grouping.Visible = false;
        ranking.ColumnHeaderMouseClick += (_, e) => { if (e.ColumnIndex is >= 2 and <= 5) sort.SelectedIndex = e.ColumnIndex switch { 2 => 0, 3 => 2, 4 => 1, _ => 3 }; };
        var menu = new ContextMenuStrip(); menu.Items.Add("复制域名", null, (_, _) => CopyHost()); menu.Items.Add("查看程序位置", null, (_, _) => OpenLocation()); ranking.ContextMenuStrip = menu;
        timer.Tick += async (_, _) => await PollAsync();
        Shown += async (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width - 32), Math.Min(MinimumSize.Height, area.Height - 32));
            Size = new Size(Math.Min(Width, area.Width - 32), Math.Min(Height, area.Height - 32));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            split.SplitterDistance = split.Height * 55 / 100;
            await PollAsync(); if (!closing) timer.Start();
        };
        FormClosing += (_, _) => { closing = true; timer.Stop(); stopping.Cancel(); api?.Dispose(); api = null; store.Sampled -= history.Apply; history.Dispose(); };
        FormClosed += (_, _) => { timer.Dispose(); SystemEvents.UserPreferenceChanged -= ThemeChanged; };
        VisibleChanged += (_, _) => { if (Visible && IsHandleCreated && !paused) Render(); };
        SystemEvents.UserPreferenceChanged += ThemeChanged; ApplyTheme(); ResumeLayout(true);
    }
    private async Task PollAsync()
    {
        if (busy || closing) return;
        busy = true;
        try
        {
            await history.FlushAsync();
            if (closing) return;
            UpdateSaveStatus();
            if (api == null && DateTime.Now < nextRetry) return;
            if (reconnect) { api?.Dispose(); api = null; reconnect = false; }
            if (api == null) { connectionStatus.Text = "正在连接本机 Clash…"; api = await ClashApi.ConnectAsync(stopping.Token); }
            store.Apply(await api.ReadAsync(stopping.Token));
            await history.FlushAsync();
            if (closing) return;
            connectionStatus.Text = "已连接 · " + api.Description + " · 只读";
            TrayApplication.Update("流量侦探 1.6 · ↑ " + Units.Rate(store.Current.UploadRate) + "  ↓ " + Units.Rate(store.Current.DownloadRate));
            if (!paused && Visible && DateTime.UtcNow >= nextRender) { Render(); nextRender = DateTime.UtcNow.AddSeconds(1); }
            else if (paused) status.Text = "显示已暂停，后台继续观察并累计；点击“继续显示”恢复。";
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex)
        {
            if (closing) return;
            api?.Dispose(); api = null; nextRetry = DateTime.Now.AddSeconds(5);
            connectionStatus.Text = "尚未连接 · 5 秒后重试";
            status.Text = ex is InvalidOperationException ? ex.Message : "读取本机 Clash 暂时失败，将自动重连；历史观察记录保留。";
        }
        finally { busy = false; }
    }
    private void Render()
    {
        if (closing) return;
        var s = store.Current;
        UpdateSaveStatus();
        values[0].Text = Units.Rate(s.UploadRate); values[1].Text = Units.Rate(s.DownloadRate);
        values[2].Text = Units.Bytes(s.Uploaded); values[3].Text = Units.Bytes(s.Downloaded);
        var routes = s.Routes;
        routeValues[0].Text = "代理 / VPN（已归因）\n↑ " + Units.Bytes(routes.ProxyUp) + "    ↓ " + Units.Bytes(routes.ProxyDown);
        routeValues[1].Text = "直连（已归因）\n↑ " + Units.Bytes(routes.DirectUp) + "    ↓ " + Units.Bytes(routes.DirectDown);
        routeValues[2].Text = "其他 / 未知（已归因）\n↑ " + Units.Bytes(routes.OtherUp) + "    ↓ " + Units.Bytes(routes.OtherDown);
        if (IsHistory)
        {
            empty.Visible = false;
            _ = historyPanel.RefreshAsync();
            status.Text = "每日历史按程序保存；上方卡片仍为本次实时监控。重新计数不删除已保存历史。";
            return;
        }
        groups = ClashStore.Group(s, grouping.SelectedIndex, search.Text, uploadsOnly.Checked, activeOnly.Checked, routeFilter.SelectedIndex);
        groups = SortGroups(groups, grouping.SelectedIndex);
        int scroll = ranking.FirstDisplayedScrollingRowIndex; filling = true; ranking.Rows.Clear(); int selectedIndex = -1;
        foreach (var g in groups)
        {
            int index = ranking.Rows.Add(g.Process, g.Host, Units.Bytes(g.Uploaded), Units.Bytes(g.Downloaded), Units.Rate(g.UploadRate), Units.Rate(g.DownloadRate), g.Active + " / " + g.Connections, g.Chain);
            ranking.Rows[index].Tag = g.Key;
            if (g.Key == selected) selectedIndex = index;
        }
        if (ranking.Rows.Count > 0)
        {
            selectedIndex = Math.Max(0, selectedIndex); ranking.CurrentCell = ranking.Rows[selectedIndex].Cells[0];
            ranking.Rows[selectedIndex].Selected = true; selected = ranking.Rows[selectedIndex].Tag as string;
            if (scroll >= 0 && scroll < ranking.Rows.Count) ranking.FirstDisplayedScrollingRowIndex = scroll;
        }
        else selected = null;
        RenderPrograms(s);
        filling = false; empty.Visible = IsProgramSummary ? programGroups.Count == 0 : groups.Count == 0; RenderDetails();
        long attributed = s.Connections.Sum(c => c.Uploaded) + s.FoldedUploaded;
        long gap = Math.Max(0, s.Uploaded - attributed);
        string shown = IsProgramSummary ? programGroups.Count + " 个程序（同名合并）" : groups.Count + " 组";
        status.Text = $"从 {s.Started:HH:mm:ss} 观察 · {s.Connections.Count(c => c.Active)} 条活动连接 · 显示 {shown} · 已归因上传 {Units.Bytes(attributed)} · 未归因差额 {Units.Bytes(gap)}（短连接可能漏采）";
        if (s.FoldedConnections > 0) status.Text += $" · 更早 {s.FoldedConnections} 条已折叠";
    }
    internal async Task OpenHistoryAsync(DateTime day)
    {
        views.SelectedIndex = 2;
        await historyPanel.OpenDateAsync(day);
    }
    private void UpdateSaveStatus()
    {
        saveStatus.Text = history.Error ?? history.RecoveryNotice ?? (history.LastSaved is { } time ? "已自动保存 " + time.ToString("HH:mm:ss") : "自动保存已开启 · 每 5 秒");
        saveStatus.ForeColor = history.Error == null ? Color.FromArgb(27, 150, 115) : Color.FromArgb(230, 110, 60);
    }
    private List<ClashGroup> SortGroups(List<ClashGroup> rows, int mode) => sort.SelectedIndex switch
    {
        1 => rows.OrderByDescending(g => g.UploadRate).ToList(), 2 => rows.OrderByDescending(g => g.Downloaded).ToList(),
        3 => rows.OrderByDescending(g => g.DownloadRate).ToList(),
        4 => rows.OrderByDescending(g => ClashStore.FilterConnections(store.Current, search.Text, activeOnly.Checked, routeFilter.SelectedIndex).Where(c => string.Equals(ClashStore.GroupKey(c, mode), g.Key, StringComparison.OrdinalIgnoreCase)).Sum(c => c.Connection.Upload)).ToList(),
        5 => rows.OrderByDescending(g => g.Routes.ProxyUp).ToList(), 6 => rows.OrderByDescending(g => g.Routes.ProxyDown).ToList(),
        7 => rows.OrderByDescending(g => g.Routes.DirectUp).ToList(), 8 => rows.OrderByDescending(g => g.Routes.DirectDown).ToList(),
        9 => rows.OrderByDescending(g => g.Routes.OtherUp).ToList(), 10 => rows.OrderByDescending(g => g.Routes.OtherDown).ToList(),
        _ => rows.OrderByDescending(g => g.Uploaded).ToList()
    };
    private void RenderPrograms(ClashSnapshot snapshot)
    {
        programGroups = SortGroups(ClashStore.Group(snapshot, 2, search.Text, uploadsOnly.Checked, activeOnly.Checked, routeFilter.SelectedIndex), 2);
        int scroll = summary.FirstDisplayedScrollingRowIndex, selectedIndex = -1;
        summary.Rows.Clear();
        foreach (var g in programGroups)
        {
            int index = summary.Rows.Add(g.Process, Units.Bytes(g.Uploaded), Units.Bytes(g.Downloaded), Units.Bytes(g.Routes.ProxyUp), Units.Bytes(g.Routes.ProxyDown),
                Units.Bytes(g.Routes.DirectUp), Units.Bytes(g.Routes.DirectDown), Units.Bytes(g.Routes.OtherUp), Units.Bytes(g.Routes.OtherDown),
                Units.Rate(g.UploadRate), Units.Rate(g.DownloadRate), g.Domains, g.Active + " / " + g.Connections, g.Paths);
            summary.Rows[index].Tag = g.Key;
            if (string.Equals(g.Key, selectedProgram, StringComparison.OrdinalIgnoreCase)) selectedIndex = index;
        }
        if (summary.Rows.Count == 0) { selectedProgram = null; return; }
        selectedIndex = programSelectionPinned ? Math.Max(0, selectedIndex) : 0;
        summary.CurrentCell = summary.Rows[selectedIndex].Cells[0]; summary.Rows[selectedIndex].Selected = true;
        selectedProgram = summary.Rows[selectedIndex].Tag as string;
        if (scroll >= 0 && scroll < summary.Rows.Count) summary.FirstDisplayedScrollingRowIndex = scroll;
    }
    private List<ClashObserved> SelectedConnections() => ClashStore.FilterConnections(store.Current, search.Text, activeOnly.Checked, routeFilter.SelectedIndex)
        .Where(c => string.Equals(ClashStore.GroupKey(c, CurrentGrouping), CurrentKey, StringComparison.OrdinalIgnoreCase)).OrderByDescending(c => c.Uploaded).ToList();
    private void RenderDetails()
    {
        details.Rows.Clear(); domains.Rows.Clear(); var rows = SelectedConnections();
        var group = (IsProgramSummary ? programGroups : groups).FirstOrDefault(g => string.Equals(g.Key, CurrentKey, StringComparison.OrdinalIgnoreCase));
        selectionInfo.Text = group == null ? "选中上方程序，查看它的域名和具体连接。点击上传或下载列可排序。" : IsProgramSummary
            ? $"{group.Process} · {group.Domains} 个域名 · {group.Connections} 条观察连接 · {group.Paths} 个程序路径 · 同名程序合并"
            : group.Process + "  →  " + group.Host + "  |  " + (rows.FirstOrDefault()?.Connection.ProcessOrigin ?? "") + "  |  " + (rows.FirstOrDefault()?.Connection.ProcessPath ?? "");
        foreach (var g in ClashStore.Group(store.Current with { Connections = rows }, 1, "", false, false).OrderByDescending(g => g.Uploaded))
            domains.Rows.Add(g.Host, Units.Bytes(g.Uploaded), Units.Bytes(g.Downloaded), Units.Rate(g.UploadRate), Units.Rate(g.DownloadRate), g.Active + " / " + g.Connections, g.Chain);
        foreach (var c in rows)
        {
            var i = c.Connection;
            string destination = i.DestinationIp.Length > 0 ? i.DestinationIp : i.Host;
            if (destination.Contains(':')) destination = "[" + destination + "]";
            details.Rows.Add(ClashRoutes.Label(i.Route), c.Active ? "活动" : "已结束", destination + ":" + i.DestinationPort, i.Network.ToUpperInvariant(), Units.Bytes(c.Uploaded), Units.Bytes(c.Downloaded),
                Units.Bytes(i.Upload), Units.Bytes(i.Download), i.Rule + " | " + i.Chain, i.Source);
        }
    }
    private void CopyHost() { var host = SelectedConnections().FirstOrDefault()?.Connection.Host; if (host?.Length > 0) Clipboard.SetText(host); }
    private void OpenLocation()
    {
        var path = SelectedConnections().FirstOrDefault(c => File.Exists(c.Connection.ProcessPath))?.Connection.ProcessPath;
        if (path == null) { MessageBox.Show(this, "Clash 未提供可读取的程序路径。", "程序位置"); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
    }
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV 报告 (*.csv)|*.csv", FileName = "Clash流量-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv", InitialDirectory = AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { ClashVerification.Export(dialog.FileName, store.Current, IsProgramSummary ? programGroups : groups); status.Text = "已导出：" + dialog.FileName; }
        catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message); }
    }
    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs args) { if (IsHandleCreated && !closing) BeginInvoke((Action)ApplyTheme); }
    private void ApplyTheme()
    {
        bool dark = false;
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); dark = (int?)key?.GetValue("AppsUseLightTheme") == 0; } catch { }
        Color background = dark ? Color.FromArgb(25, 29, 36) : Color.FromArgb(245, 247, 250), surface = dark ? Color.FromArgb(33, 38, 47) : Color.White,
            text = dark ? Color.FromArgb(230, 233, 240) : Color.FromArgb(31, 43, 58);
        void Theme(Control c)
        {
            c.BackColor = c is DataGridView or TextBox or ComboBox ? surface : background; c.ForeColor = text;
            if (c is DataGridView g)
            {
                g.BackgroundColor = surface; g.DefaultCellStyle.BackColor = surface; g.DefaultCellStyle.ForeColor = text;
                g.AlternatingRowsDefaultCellStyle.BackColor = dark ? Color.FromArgb(38, 43, 51) : Color.FromArgb(248, 250, 252);
                g.ColumnHeadersDefaultCellStyle.BackColor = background; g.ColumnHeadersDefaultCellStyle.ForeColor = text; g.GridColor = background;
                g.DefaultCellStyle.SelectionBackColor = dark ? Color.FromArgb(47, 71, 88) : Color.FromArgb(225, 239, 246); g.DefaultCellStyle.SelectionForeColor = text;
            }
            foreach (Control child in c.Controls) Theme(child);
        }
        Theme(this);
        for (int i = 0; i < 4; i++) values[i].ForeColor = i % 2 == 0 ? Color.FromArgb(216, 97, 53) : Color.FromArgb(27, 132, 153);
    }
}
