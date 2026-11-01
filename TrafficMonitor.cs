using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace TrafficLens;

internal enum TrafficKind { External, Lan, Local, Proxy }
internal enum TrafficScope { NonLocal, Public, Local, All }
internal sealed record ProcessIdentity(string Key, int Pid, string Name, string Path, bool Running);
internal sealed record TrafficRow(string Key, int Pid, string Name, string Path, bool Running,
    long Uploaded, long Downloaded, double UploadRate, double DownloadRate, int Targets);
internal sealed record TargetRow(string ProcessKey, string Protocol, string Address, int Port,
    TrafficKind Kind, long Uploaded, long Downloaded, DateTime LastSeen);
internal sealed record TrafficSnapshot(DateTime Started, DateTime Captured, List<TrafficRow> Processes,
    List<TargetRow> Targets, long Events, long LostEvents, string? Error);

internal sealed class TrafficStore
{
    private sealed class Count { public long Up, Down, LastUp, LastDown; }
    private sealed class Entry(ProcessIdentity identity)
    {
        public ProcessIdentity Identity = identity;
        public readonly Count[] Counts = Enumerable.Range(0, 4).Select(_ => new Count()).ToArray();
        public readonly Dictionary<string, TargetRow> Targets = new();
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new();
    private readonly Dictionary<int, string> current = new();
    private readonly HashSet<IPAddress> localAddresses;
    private long generation;
    private long events;
    private DateTime started = DateTime.Now;
    private long lastSample = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, (double Up, double Down)[]> rates = new();

    public TrafficStore()
    {
        localAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => { try { return n.GetIPProperties().UnicastAddresses.Select(a => Normalize(a.Address)).ToArray(); } catch { return []; } })
            .ToHashSet();
    }
    public void BeginProcess(int pid, string name, string path = "")
    {
        lock (gate)
        {
            if (current.TryGetValue(pid, out var old) && entries.TryGetValue(old, out var previous))
                previous.Identity = previous.Identity with { Running = false };
            var key = pid + ":" + ++generation;
            entries[key] = new(new(key, pid, name, path, true));
            current[pid] = key;
        }
    }
    public void EndProcess(int pid)
    {
        lock (gate)
            if (current.Remove(pid, out var key) && entries.TryGetValue(key, out var entry))
                entry.Identity = entry.Identity with { Running = false };
    }
    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    public TrafficKind Classify(IPAddress ip)
    {
        ip = Normalize(ip);
        if (IPAddress.IsLoopback(ip) || localAddresses.Contains(ip)) return TrafficKind.Local;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return TrafficKind.Proxy;
            if (b[0] == 10 || b[0] == 0 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224) return TrafficKind.Lan;
        }
        else if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xfe) == 0xfc
            || ip.Equals(IPAddress.IPv6Any)) return TrafficKind.Lan;
        return TrafficKind.External;
    }
    public void Record(int pid, int bytes, bool upload, string protocol, IPAddress remote, int port, DateTime timestamp)
    {
        if (bytes <= 0 || pid < 0) return;
        var kind = Classify(remote);
        lock (gate)
        {
            if (!current.TryGetValue(pid, out var key))
            {
                key = pid + ":" + ++generation;
                current[pid] = key;
                entries[key] = new(new(key, pid, pid == 4 ? "System" : "PID " + pid, "", true));
            }
            var entry = entries[key];
            var count = entry.Counts[(int)kind];
            if (upload) count.Up += bytes; else count.Down += bytes;
            events++;
            var address = remote.ToString();
            var targetKey = protocol + "|" + address + "|" + port;
            if (entry.Targets.Count >= 512 && !entry.Targets.ContainsKey(targetKey))
            {
                address = "（其余目标合计）"; port = 0;
                targetKey = "overflow|" + kind;
            }
            entry.Targets.TryGetValue(targetKey, out var target);
            entry.Targets[targetKey] = new(key, protocol, address, port, kind,
                (target?.Uploaded ?? 0) + (upload ? bytes : 0),
                (target?.Downloaded ?? 0) + (upload ? 0 : bytes), timestamp);
        }
    }
    public void ResolveNames()
    {
        ProcessIdentity[] unknown;
        lock (gate) unknown = entries.Values.Select(e => e.Identity)
            .Where(p => p.Running && (p.Path == "" || p.Name.StartsWith("PID "))).ToArray();
        foreach (var identity in unknown)
        {
            try
            {
                using var p = Process.GetProcessById(identity.Pid);
                var path = ProcessPath.Get(identity.Pid);
                var name = p.ProcessName;
                lock (gate)
                    if (entries.TryGetValue(identity.Key, out var e) && e.Identity.Running)
                        e.Identity = e.Identity with { Name = name, Path = path };
            }
            catch { }
        }
    }
    internal static bool InScope(TrafficKind kind, TrafficScope scope) => scope switch
    {
        TrafficScope.NonLocal => kind != TrafficKind.Local,
        TrafficScope.Public => kind == TrafficKind.External,
        TrafficScope.Local => kind == TrafficKind.Local,
        _ => true
    };
    public TrafficSnapshot Snapshot(TrafficScope scope, long lost = 0, string? error = null, bool advanceRates = true)
    {
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Math.Max(0.001, (now - lastSample) / (double)Stopwatch.Frequency);
            var rows = new List<TrafficRow>();
            var targets = new List<TargetRow>();
            foreach (var e in entries.Values)
            {
                long up = 0, down = 0;
                double ur = 0, dr = 0;
                if (!rates.TryGetValue(e.Identity.Key, out var er))
                    rates[e.Identity.Key] = er = new (double Up, double Down)[4];
                for (int k = 0; k < 4; k++)
                {
                    var c = e.Counts[k];
                    if (advanceRates)
                    {
                        er[k] = ((c.Up - c.LastUp) / elapsed, (c.Down - c.LastDown) / elapsed);
                        c.LastUp = c.Up; c.LastDown = c.Down;
                    }
                    if (!InScope((TrafficKind)k, scope)) continue;
                    up += c.Up; down += c.Down; ur += er[k].Up; dr += er[k].Down;
                }
                var et = e.Targets.Values.Where(t => InScope(t.Kind, scope)).ToArray();
                if (up == 0 && down == 0) continue;
                var i = e.Identity;
                rows.Add(new(i.Key, i.Pid, i.Name, i.Path, i.Running, up, down, ur, dr, et.Length));
                targets.AddRange(et);
            }
            if (advanceRates) lastSample = now;
            return new(started, DateTime.Now, rows, targets, events, lost, error);
        }
    }
    public void Clear()
    {
        lock (gate)
        {
            foreach (var e in entries.Values)
            {
                foreach (var c in e.Counts) { c.Up = c.Down = c.LastUp = c.LastDown = 0; }
                e.Targets.Clear();
            }
            foreach (var k in entries.Where(p => !p.Value.Identity.Running).Select(p => p.Key).ToArray()) entries.Remove(k);
            rates.Clear(); events = 0; started = DateTime.Now; lastSample = Stopwatch.GetTimestamp();
        }
    }
}

internal sealed class TrafficMonitor : IDisposable
{
    public readonly TrafficStore Store = new();
    private TraceEventSession? session;
    private Task? worker;
    private int stopping;
    public string? Error { get; private set; }
    public bool IsRunning => session != null && Error == null && Volatile.Read(ref stopping) == 0;
    public long LostEvents { get { try { return session?.Source.EventsLost ?? 0; } catch { return 0; } } }
    public static bool IsAdministrator
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    public void Start()
    {
        if (!IsAdministrator) { Error = "需要管理员权限才能显示各程序流量。点击“开启程序监控”。"; return; }
        try
        {
            var s = new TraceEventSession("TrafficLens-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
            session = s;
            s.StopOnDispose = true;
            s.BufferSizeMB = 16;
            s.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
            var kernel = s.Source.Kernel;
            kernel.ProcessStart += d => Store.BeginProcess(d.ProcessID, Path.GetFileNameWithoutExtension(d.ImageFileName));
            kernel.ProcessStop += d => Store.EndProcess(d.ProcessID);
            kernel.TcpIpSend += d => Store.Record(d.ProcessID, d.size, true, "TCP", d.daddr, d.dport, d.TimeStamp);
            // TCP kernel events describe the socket: daddr/dport are the remote endpoint
            // for both Send and Recv. UDP receive events instead use the packet source.
            kernel.TcpIpRecv += d => Store.Record(d.ProcessID, d.size, false, "TCP", d.daddr, d.dport, d.TimeStamp);
            kernel.TcpIpSendIPV6 += d => Store.Record(d.ProcessID, d.size, true, "TCP6", d.daddr, d.dport, d.TimeStamp);
            kernel.TcpIpRecvIPV6 += d => Store.Record(d.ProcessID, d.size, false, "TCP6", d.daddr, d.dport, d.TimeStamp);
            kernel.UdpIpSend += d => Store.Record(d.ProcessID, d.size, true, "UDP", d.daddr, d.dport, d.TimeStamp);
            kernel.UdpIpRecv += d => Store.Record(d.ProcessID, d.size, false, "UDP", d.saddr, d.sport, d.TimeStamp);
            kernel.UdpIpSendIPV6 += d => Store.Record(d.ProcessID, d.size, true, "UDP6", d.daddr, d.dport, d.TimeStamp);
            kernel.UdpIpRecvIPV6 += d => Store.Record(d.ProcessID, d.size, false, "UDP6", d.saddr, d.sport, d.TimeStamp);
            worker = Task.Run(() =>
            {
                try { s.Source.Process(); }
                catch (Exception ex) { if (Volatile.Read(ref stopping) == 0) Error = "采集停止：" + ex.Message; }
            });
        }
        catch (Exception ex)
        {
            Error = "无法开启程序监控：" + ex.Message;
            session?.Dispose(); session = null;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        try { session?.Dispose(); } catch { }
        try { worker?.Wait(3000); } catch { }
        session = null;
    }
}

internal static class ProcessPath
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static string Get(int pid)
    {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return "";
        try { var text = new StringBuilder(32768); int size = text.Capacity; return QueryFullProcessImageName(handle, 0, text, ref size) ? text.ToString() : ""; }
        finally { CloseHandle(handle); }
    }
}

internal sealed record AdapterReading(string Id, string Name, string Description, bool PhysicalCandidate,
    double UpRate, double DownRate, long Uploaded, long Downloaded);
internal sealed class AdapterSampler
{
    private sealed record Previous(long Sent, long Received, long Ticks, long Up, long Down);
    private readonly Dictionary<string, Previous> previous = new();
    public List<AdapterReading> Sample()
    {
        var rows = new List<AdapterReading>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var s = n.GetIPStatistics();
                var ticks = Stopwatch.GetTimestamp();
                if (!previous.TryGetValue(n.Id, out var p)) p = new(s.BytesSent, s.BytesReceived, ticks, 0, 0);
                var seconds = Math.Max(.001, (ticks - p.Ticks) / (double)Stopwatch.Frequency);
                var up = Math.Max(0, s.BytesSent - p.Sent); var down = Math.Max(0, s.BytesReceived - p.Received);
                previous[n.Id] = new(s.BytesSent, s.BytesReceived, ticks, p.Up + up, p.Down + down);
                var description = (n.Name + " " + n.Description).ToLowerInvariant();
                var virtualNames = new[] { "virtual", "vmware", "hyper-v", "wintun", "wireguard", "loopback", "tap-", "tunnel", "mihomo", "clash", "meta", "vpn", "docker", "vethernet" };
                bool physical = (n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    && !virtualNames.Any(description.Contains) && n.GetIPProperties().GatewayAddresses.Count > 0;
                rows.Add(new(n.Id, n.Name, n.Description, physical, up / seconds, down / seconds, p.Up + up, p.Down + down));
            }
            catch (NetworkInformationException) { }
        }
        var active = rows.Select(r => r.Id).ToHashSet();
        foreach (var id in previous.Keys.Where(k => !active.Contains(k)).ToArray()) previous.Remove(id);
        return rows.OrderByDescending(r => r.PhysicalCandidate).ThenBy(r => r.Name).ToList();
    }
    public void Clear() => previous.Clear();
}
