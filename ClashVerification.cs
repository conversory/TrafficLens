using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;

namespace TrafficLens;

internal static class ClashVerification
{
    private static void Check(bool result, string error) { if (!result) throw new InvalidOperationException(error); }
    public static object SelfTest()
    {
        Check(ClashEndpoint.Scalar("external-controller:\nsecret: demo", "external-controller") == "", "Empty YAML field consumed next line");
        Check(ClashEndpoint.Scalar("secret: 'de''mo'", "secret") == "de'mo", "Quoted YAML field incorrect");
        using var doc = JsonDocument.Parse("""
        {"uploadTotal":5000,"downloadTotal":9000,"connections":[{"id":"a","metadata":{"network":"tcp","type":"HTTP(S)","host":"AB.ChatGPT.com.","processPath":"C:\\App\\sample.exe","destinationIP":"1.2.3.4","destinationPort":443},"upload":1000,"download":2000,"start":"2026-01-01T00:00:00Z","chains":["Node","Group"],"rule":"Domain","rulePayload":"chatgpt.com"}]}
        """);
        var frame = ClashApi.Parse(doc.RootElement, DateTime.Now);
        var c = frame.Connections.Single();
        Check(c.Host == "ab.chatgpt.com" && c.Process == "sample.exe" && c.DestinationPort == "443" && c.Chain == "Group → Node", "Connection metadata mapping incorrect");
        var store = new ClashStore(); store.Apply(frame);
        Check(store.Current.Uploaded == 0 && store.Current.Connections.Single().Uploaded == 0, "Existing connection counted before observation");
        var later = frame with { Captured = frame.Captured.AddSeconds(2), UploadTotal = 5400, DownloadTotal = 9800, Connections = [c with { Upload = 1400, Download = 2800 }] };
        store.Apply(later); store.Apply(later with { Captured = later.Captured.AddSeconds(2) });
        Check(store.Current.Uploaded == 400 && store.Current.Connections.Single().Uploaded == 400 && store.Current.UploadRate == 0, "Counters counted twice");
        var b = c with { Id = "b", Host = "example.com", Start = later.Captured.AddSeconds(1).ToUniversalTime(), Upload = 77, Download = 88 };
        store.Apply(later with { Captured = later.Captured.AddSeconds(4), UploadTotal = 5477, DownloadTotal = 9888, Connections = [b] });
        Check(store.Current.Connections.Count == 2 && !store.Current.Connections.Single(x => x.Connection.Id == "a").Active, "Closed history lost");
        Check(store.Current.Connections.Single(x => x.Connection.Id == "b").Uploaded == 77, "UTC start time did not count newly opened connection");
        Check(ClashStore.Group(store.Current, 0, "AB.CHATGPT.COM", true, false).Single().Uploaded == 400, "Search or grouping incorrect");
        Check(ClashStore.Group(store.Current, 0, "", true, true).Count == 1, "Active filter incorrect");
        var groupedStore = new ClashStore(); groupedStore.Apply(frame);
        groupedStore.Apply(later with { Connections = [later.Connections[0], c with { Id = "quiet", Upload = 0, Download = 100, Start = frame.Captured.AddSeconds(1).ToUniversalTime() }] });
        var inclusive = ClashStore.Group(groupedStore.Current, 0, "", true, false).Single();
        Check(inclusive.Connections == 2 && inclusive.Downloaded == 900, "Upload filter removed quiet/downloading members of a visible group");
        store.Apply(later with { Captured = later.Captured.AddSeconds(6), UploadTotal = 5, DownloadTotal = 7, Connections = [] });
        Check(store.Current.Uploaded == 482 && store.Current.Restarts == 1, "Kernel counter restart handling incorrect");
        store.Clear(); Check(store.Current.Uploaded == 0 && store.Current.Connections.Count == 0, "Reset retained old data");
        Check(Reports.Csv("=cmd").StartsWith("\"'="), "CSV injection escaping lost");
        VerifyProgramMerging(c);
        VerifyRoutes(c);
        VerifyLocalOwners(c);
        return new { Passed = true, Checks = new[] { "Read-only YAML parsing", "Metadata and chain mapping", "Existing-connection baseline", "No double counting", "UTC new connection", "Closed history", "Grouping/search", "Kernel restart", "Reset", "CSV escaping", "Windows TCP/UDP IPv4/IPv6 source ownership", "Same-name merge across paths/case/exe suffix", "Program upload/download/rate reconciliation", "Actual outbound type/direct/unknown classification", "Mixed-route same-program totals and filters", "Route totals retained after 20k history fold" } };
    }
    private static void VerifyRoutes(ClashConnection template)
    {
        using var typesJson = JsonDocument.Parse("""{"proxies":{"Local":{"type":"Direct"},"DIRECT-Node":{"type":"Vmess"},"Picker":{"type":"Selector"},"Block":{"type":"Reject"}}}""");
        var types = ClashApi.ParseTypes(typesJson.RootElement);
        Check(ClashRoutes.Classify("Local", types) == ClashRoute.Direct, "Custom-named direct not recognized");
        Check(ClashRoutes.Classify("DIRECT-Node", types) == ClashRoute.Proxy, "Node name mistaken for DIRECT");
        Check(ClashRoutes.Classify("DIRECT", null) == ClashRoute.Direct && ClashRoutes.Classify("REJECT", null) == ClashRoute.Other, "Built-in outbound fallback incorrect");
        Check(ClashRoutes.Classify("Picker", types) == ClashRoute.Unknown && ClashRoutes.Classify("Unlisted", types) == ClashRoute.Unknown && ClashRoutes.Classify("", types) == ClashRoute.Unknown, "Unconfirmed route guessed");
        using var json = JsonDocument.Parse("""{"connections":[{"id":"a","metadata":{},"chains":["Local","Picker"]},{"id":"b","metadata":{},"chains":["DIRECT-Node","DIRECT"]}]}""");
        var parsed = ClashApi.Parse(json.RootElement, DateTime.Now, types);
        Check(parsed.Connections[0].Route == ClashRoute.Direct && parsed.Connections[1].Route == ClashRoute.Proxy, "Group name used instead of actual outbound");
        var now = DateTime.Now;
        var rows = new List<ClashObserved>
        {
            new(template with { Id="proxy", Route=ClashRoute.Proxy }, 100, 200, 10, 20, true, now, now),
            new(template with { Id="direct", Route=ClashRoute.Direct }, 300, 400, 30, 40, true, now, now),
            new(template with { Id="unknown", Route=ClashRoute.Unknown }, 50, 60, 0, 0, false, now, now),
            new(template with { Id="other", Route=ClashRoute.Other }, 7, 8, 0, 0, false, now, now)
        };
        var snapshot = new ClashSnapshot(now, now, 457, 668, 40, 60, rows, 0, 2);
        var group = ClashStore.Group(snapshot, 2, "", false, false).Single();
        Check(group.Routes == new ClashRouteTotals(100, 200, 300, 400, 57, 68), "Mixed program route split incorrect");
        Check(group.Routes.Uploaded == group.Uploaded && group.Routes.Downloaded == group.Downloaded, "Route sums do not reconcile");
        Check(ClashStore.Group(snapshot, 2, "", false, false, 2).Single().Uploaded == 300, "Direct filter incorrect");
        Check(ClashStore.FilterConnections(snapshot, "", false, 1).Single().Connection.Id == "proxy", "Proxy detail filter incorrect");
        Check(ClashStore.Group(snapshot, 2, "", false, false, 3).Single().Downloaded == 68, "Other/unknown filter incorrect");
        var store = new ClashStore(); store.Apply(new(0, 0, [], now));
        var many = Enumerable.Range(0, 20001).Select(i => template with { Id=i.ToString(), Route=ClashRoute.Direct, Upload=10, Download=20, Start=now.AddSeconds(1) }).ToList();
        store.Apply(new(200010, 400020, many, now.AddSeconds(2)));
        store.Apply(new(200010, 400020, [], now.AddSeconds(3)));
        Check(store.Current.FoldedConnections == 2001 && store.Current.Routes == new ClashRouteTotals(DirectUp:200010, DirectDown:400020), "Folded route totals lost");
        store.Clear(); Check(store.Current.Routes.Uploaded == 0 && store.Current.Routes.Downloaded == 0, "Folded route totals not reset");
    }
    private static void VerifyProgramMerging(ClashConnection template)
    {
        var now = DateTime.Now;
        var rows = new List<ClashObserved>
        {
            new(template with { Id="a", Process="Sample.exe", ProcessPath=@"C:\One\Sample.exe", Host="a.example" }, 100, 200, 10, 20, true, now, now),
            new(template with { Id="b", Process="sample.EXE", ProcessPath=@"D:\Two\sample.EXE", Host="b.example" }, 300, 400, 30, 40, true, now, now),
            new(template with { Id="c", Process="sample", ProcessPath="", Host="c.example" }, 50, 60, 0, 0, false, now, now),
            new(template with { Id="d", Process="Other.exe", ProcessPath=@"C:\Other.exe", Host="a.example" }, 0, 800, 0, 80, true, now, now)
        };
        var snapshot = new ClashSnapshot(now, now, 450, 1460, 40, 140, rows, 0, 2);
        var grouped = ClashStore.Group(snapshot, 2, "", false, false);
        Check(grouped.Count == 2, "Same executable name was split by path, case, or .exe suffix");
        var sample = grouped.Single(g => g.Key == "SAMPLE");
        Check(sample.Uploaded == 450 && sample.Downloaded == 660 && sample.UploadRate == 40 && sample.DownloadRate == 60, "Program merged byte/rate totals incorrect");
        Check(sample.Domains == 3 && sample.Paths == 2 && sample.Connections == 3 && sample.Active == 2, "Program domain/path/connection counts incorrect");
        Check(grouped.Sum(g => g.Uploaded) == rows.Sum(c => c.Uploaded) && grouped.Sum(g => g.Downloaded) == rows.Sum(c => c.Downloaded), "Program summary does not reconcile with details");
        Check(grouped.OrderByDescending(g => g.Downloaded).First().Process == "Other.exe", "Download-only program missing from default summary");
        Check(ClashStore.Group(snapshot, 2, "b.example", false, false).Single().Uploaded == 300, "Program summary search did not respect connection filter");
        Check(ClashStore.Group(snapshot, 2, "", false, true).Single(g => g.Key == "SAMPLE").Uploaded == 400, "Active program summary retained ended connection");
    }
    private static void VerifyLocalOwners(ClashConnection template)
    {
        foreach (var address in Socket.OSSupportsIPv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : new[] { IPAddress.Loopback })
        {
            var listener = new TcpListener(address, 0); listener.Start();
            try
            {
                using var client = new TcpClient(address.AddressFamily); client.Connect((IPEndPoint)listener.LocalEndpoint);
                using var server = listener.AcceptTcpClient();
                using var udp = new UdpClient(new IPEndPoint(address, 0));
                var tcpEndpoint = (IPEndPoint)client.Client.LocalEndPoint!; var udpEndpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
                var rows = new[] { template with { Id = "tcp", Process = "未知程序", ProcessPath = "", Network = "tcp", Source = address + ":" + tcpEndpoint.Port }, template with { Id = "udp", Process = "未知程序", ProcessPath = "", Network = "udp", Source = address + ":" + udpEndpoint.Port } };
                var resolved = new ClashProcessResolver().Enrich(new(0, 0, rows.ToList(), DateTime.Now));
                using var self = Process.GetCurrentProcess();
                Check(resolved.Connections.All(c => c.Process == self.ProcessName + ".exe" && c.ProcessOrigin == "系统连接匹配"), "Windows source process matching failed: " + address);
            }
            finally { listener.Stop(); }
        }
    }
    public static async Task<object> LiveTestAsync(int seconds = 12, string? csvPath = null)
    {
        var tests = SelfTest();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 20));
        using var api = await ClashApi.ConnectAsync(timeout.Token);
        var store = new ClashStore();
        for (int i = 0; i < seconds; i++)
        {
            store.Apply(await api.ReadAsync(timeout.Token));
            await Task.Delay(1000, timeout.Token);
        }
        var snapshot = store.Current;
        var groups = ClashStore.Group(snapshot, 0, "", false, false).OrderByDescending(g => g.Uploaded).ToList();
        var programs = ClashStore.Group(snapshot, 2, "", false, false).OrderByDescending(g => g.Uploaded).ToList();
        bool programTotalsReconcile = programs.Sum(g => g.Uploaded) == snapshot.Connections.Sum(c => c.Uploaded)
            && programs.Sum(g => g.Downloaded) == snapshot.Connections.Sum(c => c.Downloaded);
        Check(programTotalsReconcile, "Live program totals do not reconcile");
        bool routesReconcile = programs.All(g => g.Routes.Uploaded == g.Uploaded && g.Routes.Downloaded == g.Downloaded)
            && snapshot.Routes.Uploaded == programs.Sum(g => g.Uploaded) && snapshot.Routes.Downloaded == programs.Sum(g => g.Downloaded);
        Check(routesReconcile, "Live route totals do not reconcile");
        if (csvPath != null)
        {
            Export(csvPath, snapshot, groups);
            var header = File.ReadAllBytes(csvPath);
            Check(header.Length > 3 && header[0] == 0xef && header[1] == 0xbb && header[2] == 0xbf, "CSV UTF-8 BOM missing");
            var csv = File.ReadAllText(csvPath);
            Check(csv.Contains("代理上传 B") && csv.Contains("直连上传 B") && csv.Contains("线路分类"), "CSV route columns missing");
        }
        return new { Passed = snapshot.Frames >= seconds, Tests = tests, Transport = api.Description,
            snapshot.Started, snapshot.Captured, snapshot.Uploaded, snapshot.Downloaded, snapshot.Frames,
            Active = snapshot.Connections.Count(c => c.Active), ObservedConnections = snapshot.Connections.Count,
            WithProcess = snapshot.Connections.Count(c => c.Connection.Process != "未知程序"),
            WithDomain = snapshot.Connections.Count(c => c.Connection.Host != c.Connection.DestinationIp), Groups = groups,
            Programs = programs, ProgramTotalsReconcile = programTotalsReconcile,
            RouteTotals = snapshot.Routes, RouteTotalsReconcile = routesReconcile,
            Routes = snapshot.Connections.GroupBy(c => c.Connection.Route).Select(g => new { Route = ClashRoutes.Label(g.Key), Connections = g.Count(), Uploaded = g.Sum(c => c.Uploaded), Downloaded = g.Sum(c => c.Downloaded) }),
            AttributedUpload = snapshot.Connections.Sum(c => c.Uploaded),
            CsvVerified = csvPath != null, Scope = "只读本机 Clash /connections；密钥未写入此报告。" };
    }
    public static void Export(string path, ClashSnapshot snapshot, List<ClashGroup> groups)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        void Row(params object?[] cells) => writer.WriteLine(string.Join(",", cells.Select(Reports.Csv)));
        Row("Clash 流量明细 1.5", "开始", snapshot.Started.ToString("O"), "采样", snapshot.Captured.ToString("O"));
        Row("内核本次上传 B", snapshot.Uploaded, "内核本次下载 B", snapshot.Downloaded, "短连接可能漏采，域名汇总可能小于内核总量。");
        var r = snapshot.Routes;
        Row("全部已归因线路（含折叠）", "代理上传 B", r.ProxyUp, "代理下载 B", r.ProxyDown, "直连上传 B", r.DirectUp, "直连下载 B", r.DirectDown,
            "其他/未知上传 B", r.OtherUp, "其他/未知下载 B", r.OtherDown);
        Row(); Row("程序汇总（同名合并）");
        Row("程序名", "本次观察上传 B", "本次观察下载 B", "上传 B/s", "下载 B/s", "域名数", "活动连接", "观察连接", "程序路径数", "代理上传 B", "代理下载 B", "直连上传 B", "直连下载 B", "其他/未知上传 B", "其他/未知下载 B");
        foreach (var g in ClashStore.Group(snapshot, 2, "", false, false).OrderByDescending(g => g.Uploaded))
            Row(g.Process, g.Uploaded, g.Downloaded, g.UploadRate, g.DownloadRate, g.Domains, g.Active, g.Connections, g.Paths,
                g.Routes.ProxyUp, g.Routes.ProxyDown, g.Routes.DirectUp, g.Routes.DirectDown, g.Routes.OtherUp, g.Routes.OtherDown);
        Row(); Row("当前筛选视图");
        Row("程序", "域名 / 目标", "代理链", "本次观察上传 B", "本次观察下载 B", "上传 B/s", "下载 B/s", "活动连接", "观察连接");
        foreach (var g in groups.OrderByDescending(g => g.Uploaded)) Row(g.Process, g.Host, g.Chain, g.Uploaded, g.Downloaded, g.UploadRate, g.DownloadRate, g.Active, g.Connections);
        Row(); Row("程序", "域名", "目标 IP", "端口", "协议", "代理链", "规则", "本次上传 B", "本次下载 B", "连接生命周期上传 B", "连接生命周期下载 B", "状态", "来源", "程序路径", "程序识别依据", "开始", "最近观察", "线路分类", "实际出口");
        foreach (var c in snapshot.Connections.OrderByDescending(c => c.Uploaded))
        {
            var i = c.Connection;
            Row(i.Process, i.Host, i.DestinationIp, i.DestinationPort, i.Network, i.Chain, i.Rule, c.Uploaded, c.Downloaded, i.Upload, i.Download,
                c.Active ? "活动" : "已结束", i.Source, i.ProcessPath, i.ProcessOrigin, i.Start.ToString("O"), c.LastSeen.ToString("O"), ClashRoutes.Label(i.Route), i.Outbound);
        }
    }
}
