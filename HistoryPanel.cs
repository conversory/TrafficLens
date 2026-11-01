using System.Diagnostics;

namespace TrafficLens;

internal sealed class HistoryPanel : UserControl
{
    private readonly DailyHistory history;
    private readonly DateTimePicker date = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd", Width = 155 };
    private readonly TextBox search = new() { PlaceholderText = "搜索程序名", Width = 180 };
    private readonly ComboBox route = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly ComboBox sort = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly Label info = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DataGridView grid = MainForm.Grid();
    private DailyTraffic? day;
    private List<DailyProgram> rows = [];
    private bool loading;
    private long lastVersion = -1;
    private DateTime lastDate = DateTime.MinValue;
    internal int Rows => grid.Rows.Count;
    internal DateTime SelectedDate => date.Value.Date;
    internal long Uploaded => rows.Sum(p => p.Routes.Uploaded);
    internal long Downloaded => rows.Sum(p => p.Routes.Downloaded);
    internal string Info => info.Text;

    internal HistoryPanel(DailyHistory history)
    {
        this.history = history; Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(5) };
        layout.RowStyles.Add(new(SizeType.Absolute, 116)); layout.RowStyles.Add(new(SizeType.Absolute, 100)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(0, 7, 0, 0) };
        var previous = new Button { Text = "前一天", AutoSize = true }; var next = new Button { Text = "后一天", AutoSize = true };
        var today = new Button { Text = "今天", AutoSize = true }; var refresh = new Button { Text = "保存 / 刷新", AutoSize = true };
        var export = new Button { Text = "导出当天 CSV", AutoSize = true }; var folder = new Button { Text = "历史目录", AutoSize = true };
        var destinations = new Button { Text = "域名 / 节点明细", AutoSize = true };
        destinations.Click += (_, _) => ShowDestinations();
        route.Items.AddRange(["全部线路", "只看代理 / VPN", "只看直连", "其他 / 未知"]); route.SelectedIndex = 0;
        sort.Items.AddRange(["上传量 ↓", "下载量 ↓", "代理上传 ↓", "代理下载 ↓", "直连上传 ↓", "直连下载 ↓", "其他上传 ↓", "其他下载 ↓"]); sort.SelectedIndex = 0;
        foreach (var combo in new[] { route, sort })
        {
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += (_, e) =>
            {
                using var brush = new SolidBrush(combo.BackColor); e.Graphics.FillRectangle(brush, e.Bounds);
                string text = e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) ?? "" : combo.Text;
                TextRenderer.DrawText(e.Graphics, text, combo.Font, e.Bounds, combo.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                e.DrawFocusRectangle();
            };
        }
        foreach (Control control in new Control[] { date, previous, next, today, search, route, sort, refresh, export, folder, destinations })
        { control.Margin = new Padding(0, 0, 8, 6); tools.Controls.Add(control); }
        layout.Controls.Add(tools, 0, 0); layout.Controls.Add(info, 0, 1); layout.Controls.Add(grid, 0, 2); Controls.Add(layout);
        MainForm.AddColumns(grid, ("程序名（同名合并）", 220), ("当天上传", 130), ("当天下载", 130), ("代理上传", 120), ("代理下载", 120),
            ("直连上传", 120), ("直连下载", 120), ("其他上传", 110), ("其他下载", 110), ("最近观察", 160));
        previous.Click += (_, _) => date.Value = date.Value.AddDays(-1); next.Click += (_, _) => date.Value = date.Value.AddDays(1);
        today.Click += (_, _) => { date.Value = DateTime.Today; _ = RefreshAsync(true); };
        date.ValueChanged += async (_, _) => await RefreshAsync(true);
        search.TextChanged += (_, _) => Fill(); route.SelectedIndexChanged += (_, _) => Fill(); sort.SelectedIndexChanged += (_, _) => Fill();
        grid.ColumnHeaderMouseClick += (_, e) => { if (e.ColumnIndex is >= 1 and <= 8) sort.SelectedIndex = e.ColumnIndex - 1; };
        refresh.Click += async (_, _) => { await history.FlushAsync(true); await RefreshAsync(true); };
        export.Click += (_, _) => Export();
        folder.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + history.DirectoryPath + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开历史目录：" + ex.Message); }
        };
    }
    internal async Task OpenDateAsync(DateTime selected)
    {
        date.Value = selected.Date;
        while (loading && !IsDisposed) await Task.Delay(20);
        await RefreshAsync(true);
    }
    internal async Task RefreshAsync(bool force = false)
    {
        if (loading || IsDisposed) return;
        var requested = date.Value.Date;
        long version = history.Version;
        if (!force && requested == lastDate && version == lastVersion) return;
        loading = true;
        try
        {
            var loaded = await Task.Run(() => history.ReadDay(requested));
            if (IsDisposed || requested != date.Value.Date) return;
            day = loaded; lastDate = requested; lastVersion = version; Fill();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { if (!IsDisposed) { day = null; grid.Rows.Clear(); info.Text = "历史读取失败：" + ex.Message; } }
        finally { loading = false; }
    }
    private void Fill()
    {
        if (day == null) return;
        rows = DailyHistory.Filter(day, search.Text, route.SelectedIndex, false);
        Func<DailyProgram, long> key = sort.SelectedIndex switch
        {
            1 => p => p.Routes.Downloaded, 2 => p => p.Routes.ProxyUp, 3 => p => p.Routes.ProxyDown,
            4 => p => p.Routes.DirectUp, 5 => p => p.Routes.DirectDown, 6 => p => p.Routes.OtherUp,
            7 => p => p.Routes.OtherDown, _ => p => p.Routes.Uploaded
        };
        rows = rows.OrderByDescending(key).ThenBy(p => p.Process).ToList();
        string? selected = grid.CurrentRow?.Tag as string; int scroll = grid.FirstDisplayedScrollingRowIndex;
        grid.Rows.Clear(); int chosen = 0;
        foreach (var p in rows)
        {
            int index = grid.Rows.Add(p.Process, Units.Bytes(p.Routes.Uploaded), Units.Bytes(p.Routes.Downloaded), Units.Bytes(p.Routes.ProxyUp), Units.Bytes(p.Routes.ProxyDown),
                Units.Bytes(p.Routes.DirectUp), Units.Bytes(p.Routes.DirectDown), Units.Bytes(p.Routes.OtherUp), Units.Bytes(p.Routes.OtherDown), p.LastSeen.ToString("HH:mm:ss"));
            grid.Rows[index].Tag = p.Key; if (p.Key == selected) chosen = index;
        }
        if (grid.Rows.Count > 0)
        {
            grid.CurrentCell = grid.Rows[chosen].Cells[0];
            if (scroll >= 0 && scroll < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = scroll;
        }
        info.Text = $"{day.Day} · {rows.Count} 个程序 · 上传 {Units.Bytes(Uploaded)} / 下载 {Units.Bytes(Downloaded)} · " +
            $"代理 ↑{Units.Bytes(rows.Sum(p => p.Routes.ProxyUp))} ↓{Units.Bytes(rows.Sum(p => p.Routes.ProxyDown))} · " +
            $"直连 ↑{Units.Bytes(rows.Sum(p => p.Routes.DirectUp))} ↓{Units.Bytes(rows.Sum(p => p.Routes.DirectDown))}";
        info.Text += $"\n全部线路核对：内核 ↑{Units.Bytes(day.KernelUploaded)} ↓{Units.Bytes(day.KernelDownloaded)} · 差额（内核减明细） ↑{day.AttributionUploadDifference:N0} B ↓{day.AttributionDownloadDifference:N0} B";
        info.Text += day.Samples == 0 ? "\n旧记录无采样覆盖数据" : $"\n有效采样间隔累计 {TimeSpan.FromSeconds(day.ObservedSeconds):g} · 长间隔 {day.SamplingGaps} 次（>5秒）；差额不分配到 VPN，不能视为精确漏记量";
        if (rows.Count == 0) info.Text += " · 当天暂无已保存的匹配记录";
    }
    private void ShowDestinations()
    {
        if (day == null) return;
        using var window = new Form { Text = day.Day + " · 程序 / 域名 / 出口", Width = 1100, Height = 650, StartPosition = FormStartPosition.CenterParent };
        var table = MainForm.Grid(); table.Dock = DockStyle.Fill;
        MainForm.AddColumns(table, ("程序", 180), ("域名 / 目标", 260), ("出口节点", 220), ("线路", 90), ("上传", 130), ("下载", 130));
        foreach (var item in day.Destinations.Values.Where(d => d.Process.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) && ClashRoutes.Matches(d.Route, route.SelectedIndex)).OrderByDescending(d => d.Uploaded))
            table.Rows.Add(item.Process, item.Host, item.Outbound, item.Route, Units.Bytes(item.Uploaded), Units.Bytes(item.Downloaded));
        var note = new Label { Dock = DockStyle.Bottom, Height = 48, Text = $"明细从 1.6 开始记录，旧历史无法补录。每天最多保留 10,000 个组合。\n超出上限的明细：↑{Units.Bytes(day.OverflowDetailUploaded)} ↓{Units.Bytes(day.OverflowDetailDownloaded)}；程序总量仍完整累计。" };
        window.Controls.Add(table); window.Controls.Add(note); window.ShowDialog(this);
    }
    internal void Export()
    {
        if (day == null) return;
        using var dialog = new SaveFileDialog { Filter = "CSV 报告 (*.csv)|*.csv", FileName = "每日流量-" + day.Day + ".csv", InitialDirectory = AppContext.BaseDirectory };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { DailyHistory.Export(dialog.FileName, day, rows); }
        catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message); }
    }
}
