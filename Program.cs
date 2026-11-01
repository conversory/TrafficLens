using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TrafficLens;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        HistoryPaths.Configure(args);
        if (args.Length >= 2 && args[0] is "--history-test" or "--history-live-test" or "--history-read")
        {
            try
            {
                object result;
                if (args[0] == "--history-test") result = HistoryVerification.Verify(HistoryPaths.DirectoryPath);
                else if (args[0] == "--history-live-test") result = HistoryVerification.LiveAsync(HistoryPaths.DirectoryPath).GetAwaiter().GetResult();
                else
                {
                    using var history = new DailyHistory(HistoryPaths.DirectoryPath, automatic: false);
                    int dayIndex = Array.IndexOf(args, "--day");
                    var selected = dayIndex >= 0 ? DateTime.ParseExact(args[dayIndex + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.Today;
                    var data = history.ReadDay(selected);
                    result = new { Passed = true, Day = data.Day, Programs = data.Programs.Count, Uploaded = data.Programs.Values.Sum(p => p.Routes.Uploaded), Downloaded = data.Programs.Values.Sum(p => p.Routes.Downloaded), Data = data };
                }
                Reports.WriteJson(args[1], result); return 0;
            }
            catch (Exception ex) { Reports.WriteJson(args[1], new { Passed = false, Error = ex.ToString() }); return 1; }
        }
        if (args.Length >= 2 && args[0] is "--startup-test" or "--startup-enable" or "--startup-disable" or "--startup-status")
        {
            try
            {
                if (args[0] == "--startup-test") Reports.WriteJson(args[1], StartupRegistration.Verify());
                else
                {
                    var state = args[0] == "--startup-status" ? StartupRegistration.Read() : StartupRegistration.SetEnabled(args[0] == "--startup-enable");
                    Reports.WriteJson(args[1], new { Passed = !state.DisabledByWindows, State = state, Program = Environment.ProcessPath });
                    if (state.DisabledByWindows) return 1;
                }
                return 0;
            }
            catch (Exception ex) { Reports.WriteJson(args[1], new { Passed = false, Error = ex.Message }); return 1; }
        }
        if (args.Length >= 2 && args[0] == "--clash-test")
        {
            try { Reports.WriteJson(args[1], ClashVerification.LiveTestAsync(csvPath: Path.ChangeExtension(args[1], ".csv")).GetAwaiter().GetResult()); return 0; }
            catch (Exception ex) { Reports.WriteJson(args[1], new { Passed = false, ErrorType = ex.GetType().Name, Error = ex.Message }); return 1; }
        }
        if (args.Length >= 2 && args[0] == "--self-test")
        {
            try { var result = Verification.SelfTest(); Reports.WriteJson(args[1], result); return 0; }
            catch (Exception ex) { Reports.WriteJson(args[1], new { Passed = false, Error = ex.ToString() }); return 1; }
        }
        if (args.Length >= 2 && args[0] == "--validate")
        {
            try
            {
                var result = Verification.ValidateAsync().GetAwaiter().GetResult();
                Reports.WriteJson(args[1], result);
                if (args.Contains("--open")) Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--system") { UseShellExecute = true });
                return result.Passed ? 0 : 1;
            }
            catch (Exception ex) { Reports.WriteJson(args[1], new { Passed = false, Error = ex.ToString() }); return 1; }
        }
        if (args.Length >= 3 && args[0] == "--diagnose")
        {
            try
            {
                int seconds = Math.Clamp(int.Parse(args[1]), 5, 3600);
                using var monitor = new TrafficMonitor(); monitor.Start();
                if (!monitor.IsRunning) throw new InvalidOperationException(monitor.Error);
                Thread.Sleep(seconds * 1000); monitor.Store.ResolveNames();
                Reports.WriteJson(args[2], monitor.Store.Snapshot(TrafficScope.NonLocal, monitor.LostEvents, monitor.Error)); return 0;
            }
            catch (Exception ex) { Reports.WriteJson(args[2], new { Passed = false, Error = ex.ToString() }); return 1; }
        }
        Application.ThreadException += (_, e) => MessageBox.Show("界面遇到问题：" + e.Exception.Message, "流量侦探");
        bool diagnostic = args.Length > 0 && args[0] is "--tray-test" or "--ui-check" or "--preview" or "--history-ui-check";
        using var instance = !diagnostic && !args.Contains("--system") ? new SingleInstance() : null;
        if (instance is { IsPrimary: false }) { instance.SignalExisting(args.Contains("--background")); return 0; }
        Form form = args.Contains("--system") ? new MainForm() : new ClashForm();
        form.Shown += (_, _) => instance?.Attach(form);
        TrayApplication.Initialize(form);
        object? trayTest = null;
        bool trayPassed = false;
        if (args.Contains("--background"))
        {
            form.Opacity = 0; form.ShowInTaskbar = false;
            form.Shown += (_, _) => form.BeginInvoke((Action)(() =>
            {
                TrayApplication.HideWindow(form, notify: false);
                form.Opacity = 1; form.ShowInTaskbar = true;
            }));
        }
        if (args.Length >= 2 && args[0] == "--background-check")
        {
            form.Shown += async (_, _) =>
            {
                await Task.Delay(2500);
                bool hidden = !form.Visible && TrayApplication.IconVisible;
                long before = (form as ClashForm)?.Frames ?? 0;
                await Task.Delay(7500);
                long after = (form as ClashForm)?.Frames ?? 0;
                Reports.WriteJson(args[1], new { Passed = hidden && after > before && form.Visible, StartedHidden = hidden,
                    FramesBefore = before, FramesAfter = after, DuplicateManualLaunchRestored = form.Visible,
                    TrayVisible = TrayApplication.IconVisible, UI = (form as ClashForm)?.Diagnostics });
                TrayApplication.Exit();
            };
        }
        if (args.Length >= 2 && args[0] == "--tray-test")
        {
            form.Shown += async (_, _) =>
            {
                try
                {
                    if (form is not ClashForm clash) throw new InvalidOperationException("Tray test requires Clash view");
                    for (int i = 0; i < 40 && clash.Frames < 2; i++) await Task.Delay(500);
                    long before = clash.Frames;
                    form.Close(); // Same UserClosing path as clicking X.
                    bool hidden = !form.Visible && !form.IsDisposed && TrayApplication.IconVisible;
                    await Task.Delay(3500);
                    long after = clash.Frames;
                    TrayApplication.ShowWindow(form);
                    bool restored = form.Visible && !form.IsDisposed;
                    trayPassed = before >= 2 && hidden && after > before && restored;
                    trayTest = new { Passed = trayPassed, HiddenWithoutDisposal = hidden, FramesBefore = before, FramesWhileHidden = after,
                        MonitoringContinued = after > before, Restored = restored, UI = clash.Diagnostics };
                }
                catch (Exception ex) { trayTest = new { Passed = false, Error = ex.Message }; }
                finally { TrayApplication.Exit(); }
            };
        }
        if (args.Length >= 2 && args[0] is "--ui-check" or "--preview" or "--history-ui-check")
        {
            form.Shown += async (_, _) =>
            {
                await Task.Delay(3500);
                if (args[0] == "--history-ui-check" && form is ClashForm historyForm)
                {
                    int dayIndex = Array.IndexOf(args, "--day");
                    var day = dayIndex >= 0 ? DateTime.ParseExact(args[dayIndex + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateTime.Today;
                    await historyForm.OpenHistoryAsync(day);
                    await Task.Delay(300);
                }
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                Reports.WriteJson(Path.ChangeExtension(args[1], ".json"), form is ClashForm clash ? clash.Diagnostics : ((MainForm)form).Diagnostics);
                if (args[0] != "--preview") TrayApplication.Exit();
            };
        }
        try { Application.Run(form); }
        finally { TrayApplication.Dispose(); }
        if (args.Length >= 2 && args[0] == "--tray-test")
        {
            bool exited = form.IsDisposed && TrayApplication.Disposed && Application.OpenForms.Count == 0;
            Reports.WriteJson(args[1], new { Passed = trayPassed && exited, Test = trayTest, ExplicitExitDisposedEverything = exited });
            return trayPassed && exited ? 0 : 1;
        }
        return 0;
    }
}

internal static class Reports
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    }
    internal static string Csv(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        // Reports can contain executable names. Prevent spreadsheet formula evaluation.
        if (text.Length > 0 && ("=+-@\t\r\n".Contains(text[0]) || "=+-@".Contains(text.TrimStart().FirstOrDefault()))) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    public static void WriteCsv(string path, TrafficSnapshot snapshot, List<AdapterReading> adapters, TrafficScope scope)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        void Row(params object?[] values) => writer.WriteLine(string.Join(",", values.Select(Csv)));
        Row("流量侦探 1.6", "开始", snapshot.Started.ToString("O"), "导出", snapshot.Captured.ToString("O"), "范围", scope);
        Row("说明", "单位为字节 B，速率 B/s；网卡与程序口径不同，代理和虚拟网卡可能重复计数。", "ETW 丢失事件", snapshot.LostEvents);
        Row("网卡", "设备", "上传 B/s", "下载 B/s", "本次上传 B", "本次下载 B");
        foreach (var a in adapters) Row(a.Name, a.Description, a.UpRate, a.DownRate, a.Uploaded, a.Downloaded);
        Row(); Row("程序", "PID", "上传 B/s", "下载 B/s", "累计上传 B", "累计下载 B", "状态", "路径");
        foreach (var p in snapshot.Processes.OrderByDescending(p => p.Uploaded)) Row(p.Name, p.Pid, p.UploadRate, p.DownloadRate, p.Uploaded, p.Downloaded, p.Running ? "运行中" : "已结束", p.Path);
        Row(); Row("程序", "PID", "协议", "目标 IP", "目标端口", "类型", "上传 B", "下载 B", "最近活动");
        var names = snapshot.Processes.ToDictionary(p => p.Key);
        foreach (var t in snapshot.Targets.OrderByDescending(t => t.Uploaded))
        {
            names.TryGetValue(t.ProcessKey, out var p);
            Row(p?.Name, p?.Pid, t.Protocol, t.Address, t.Port, Units.Kind(t.Kind), t.Uploaded, t.Downloaded, t.LastSeen.ToString("O"));
        }
    }
}

internal static class Verification
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static object SelfTest()
    {
        var store = new TrafficStore(); var t = DateTime.Now;
        store.BeginProcess(900001, "first", "C:/first.exe");
        store.Record(900001, 1000, true, "TCP", IPAddress.Parse("8.8.8.8"), 443, t);
        store.Record(900001, 2000, false, "TCP", IPAddress.Parse("8.8.8.8"), 443, t);
        store.Record(900001, 333, true, "TCP", IPAddress.Loopback, 7890, t);
        store.Record(900001, 444, true, "UDP", IPAddress.Parse("192.168.55.201"), 5000, t);
        store.Record(900001, 555, true, "UDP", IPAddress.Parse("198.18.1.2"), 443, t);
        var external = store.Snapshot(TrafficScope.NonLocal);
        Check(external.Processes.Single().Uploaded == 1999 && external.Processes.Single().Downloaded == 2000, "Non-local totals incorrect");
        Check(store.Snapshot(TrafficScope.Public, advanceRates: false).Processes.Single().Uploaded == 1000, "Public filter incorrect");
        Check(store.Snapshot(TrafficScope.Local, advanceRates: false).Processes.Single().Uploaded == 333, "Loopback filter incorrect");
        Check(store.Classify(IPAddress.Parse("::ffff:127.0.0.1")) == TrafficKind.Local, "Mapped IPv4 loopback incorrect");
        Check(store.Classify(IPAddress.Parse("fd12::1")) == TrafficKind.Lan, "IPv6 ULA incorrect");
        Check(store.Classify(IPAddress.Parse("2606:4700:4700::1111")) == TrafficKind.External, "IPv6 external incorrect");
        store.EndProcess(900001); store.BeginProcess(900001, "reused");
        store.Record(900001, 70, true, "UDP6", IPAddress.IPv6Loopback, 8888, t);
        var all = store.Snapshot(TrafficScope.All);
        Check(all.Processes.Count == 2 && all.Processes.Single(p => p.Name == "first").Running == false, "PID reuse mixed old/new processes");
        Check(all.Targets.Sum(x => x.Uploaded) == all.Processes.Sum(x => x.Uploaded), "Target totals do not reconcile");
        Check(Reports.Csv("=1+1").StartsWith("\"'="), "CSV formula was not escaped");
        Check(Reports.Csv("a,\"b").Contains("\"\""), "CSV quote escaping incorrect");
        store.Clear(); Check(store.Snapshot(TrafficScope.All).Processes.Count == 0, "Clear did not reset totals");
        return new { Passed = true, Checks = new[] { "Upload/download totals", "Scope filters", "IPv4-mapped IPv6 and ULA", "PID reuse", "Target reconciliation", "CSV escaping", "Reset" }, Time = DateTime.Now };
    }
    internal sealed record Probe(string Protocol, int ServerPort, int ClientPort, int Uploaded, int Downloaded);
    internal sealed record ProbeResult(string Protocol, int Port, long Uploaded, long Downloaded, bool Passed);
    internal sealed record ValidationResult(bool Passed, bool Administrator, string? Error, object UnitTests,
        List<ProbeResult> Probes, TrafficSnapshot Observed, string[] Notes);
    public static async Task<ValidationResult> ValidateAsync()
    {
        var unit = SelfTest();
        using var monitor = new TrafficMonitor(); monitor.Start();
        if (!monitor.IsRunning) return new(false, TrafficMonitor.IsAdministrator, monitor.Error, unit, [], monitor.Store.Snapshot(TrafficScope.All), ["ETW 未启动，未产生任何测试流量。"]);
        await Task.Delay(1800);
        var probes = new List<Probe>();
        probes.Add(await TcpProbe(IPAddress.Loopback));
        probes.Add(await UdpProbe(IPAddress.Loopback));
        if (Socket.OSSupportsIPv6)
        {
            probes.Add(await TcpProbe(IPAddress.IPv6Loopback));
            probes.Add(await UdpProbe(IPAddress.IPv6Loopback));
        }
        await Task.Delay(3000);
        monitor.Store.ResolveNames();
        var all = monitor.Store.Snapshot(TrafficScope.All, monitor.LostEvents, monitor.Error);
        var self = all.Processes.Where(p => p.Pid == Environment.ProcessId).Select(p => p.Key).ToHashSet();
        var results = probes.Select(p =>
        {
            var rows = all.Targets.Where(t => self.Contains(t.ProcessKey) && t.Protocol == p.Protocol && t.Port == p.ServerPort).ToArray();
            long up = rows.Sum(t => t.Uploaded), down = rows.Sum(t => t.Downloaded);
            return new ProbeResult(p.Protocol, p.ServerPort, up, down, up >= p.Uploaded && down >= p.Downloaded && up < p.Uploaded * 1.4 && down < p.Downloaded * 1.4);
        }).ToList();
        // Observe normal non-local traffic as well; local test traffic is excluded from this snapshot.
        await Task.Delay(15000); monitor.Store.ResolveNames();
        var observed = monitor.Store.Snapshot(TrafficScope.NonLocal, monitor.LostEvents, monitor.Error);
        return new(results.All(p => p.Passed) && monitor.Error == null && monitor.LostEvents == 0, true, monitor.Error, unit, results, observed,
            ["测试只在本机回环发送可控字节，不向外部服务器上传测试数据。", "Observed 为实际的非本机流量；加密数据的正文未读取或保存。"]);
    }
    private static async Task<Probe> TcpProbe(IPAddress address)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(address, 0); listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient(address.AddressFamily);
            var connect = client.ConnectAsync(address, port, timeout.Token);
            using var server = await listener.AcceptTcpClientAsync(timeout.Token); await connect;
            int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            byte[] request = new byte[1024 * 1024], response = new byte[256 * 1024];
            var receive = server.GetStream().ReadExactlyAsync(new byte[request.Length], timeout.Token);
            await client.GetStream().WriteAsync(request, timeout.Token); await receive;
            var receiveResponse = client.GetStream().ReadExactlyAsync(new byte[response.Length], timeout.Token);
            await server.GetStream().WriteAsync(response, timeout.Token); await receiveResponse;
            return new(address.AddressFamily == AddressFamily.InterNetwork ? "TCP" : "TCP6", port, clientPort, request.Length, response.Length);
        }
        finally { listener.Stop(); }
    }
    private static async Task<Probe> UdpProbe(IPAddress address)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new UdpClient(new IPEndPoint(address, 0));
        using var client = new UdpClient(new IPEndPoint(address, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port, clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        for (int i = 0; i < 16; i++)
        {
            await client.SendAsync(new byte[4096], new IPEndPoint(address, port), timeout.Token);
            var received = await server.ReceiveAsync(timeout.Token);
            await server.SendAsync(new byte[1024], received.RemoteEndPoint, timeout.Token);
            await client.ReceiveAsync(timeout.Token);
        }
        return new(address.AddressFamily == AddressFamily.InterNetwork ? "UDP" : "UDP6", port, clientPort, 65536, 16384);
    }
}
