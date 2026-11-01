using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TrafficLens;

// Match only local source endpoints with a unique owner. Never guess from the domain.
internal sealed class ClashProcessResolver
{
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    private sealed record Owner(string Network, string Address, int Port, int Pid);
    private List<Owner> owners = [];
    private HashSet<string> local = [];
    private readonly Dictionary<int, (string Name, string Path)> names = new();
    private DateTime updated = DateTime.MinValue;
    private static string AddressKey(IPAddress address) => Convert.ToHexString(TrafficStore.Normalize(address).GetAddressBytes());
    public ClashFrame Enrich(ClashFrame frame)
    {
        if (!frame.Connections.Any(c => c.Process == "未知程序")) return frame;
        if ((DateTime.Now - updated).TotalSeconds >= 2)
        {
            owners.Clear(); names.Clear();
            local = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n =>
            { try { return n.GetIPProperties().UnicastAddresses.Select(a => AddressKey(a.Address)).ToArray(); } catch { return []; } }).ToHashSet();
            foreach (int family in new[] { 2, 23 }) { ReadTable(true, family); ReadTable(false, family); }
            updated = DateTime.Now;
        }
        var rows = new List<ClashConnection>(frame.Connections.Count);
        foreach (var c in frame.Connections)
        {
            if (c.Process != "未知程序") { rows.Add(c); continue; }
            int colon = c.Source.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(c.Source[(colon + 1)..], out int port) || !IPAddress.TryParse(c.Source[..colon].Trim('[', ']'), out var ip)) { rows.Add(c); continue; }
            string address = AddressKey(ip);
            if (!IPAddress.IsLoopback(ip) && !local.Contains(address)) { rows.Add(c); continue; }
            string network = c.Network.Equals("udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
            var matches = owners.Where(o => o.Network == network && o.Port == port && (o.Address == address || o.Address == "*")).Select(o => o.Pid).Distinct().ToArray();
            if (matches.Length != 1) { rows.Add(c); continue; }
            int pid = matches[0];
            if (!names.TryGetValue(pid, out var info))
            {
                try { using var p = Process.GetProcessById(pid); info = (p.ProcessName + ".exe", ProcessPath.Get(pid)); }
                catch { info = ("", ""); }
                names[pid] = info;
            }
            rows.Add(info.Name.Length == 0 ? c : c with { Process = info.Name, ProcessPath = info.Path, ProcessOrigin = "系统连接匹配" });
        }
        return frame with { Connections = rows };
    }
    private void ReadTable(bool tcp, int family)
    {
        int size = 0, tableClass = tcp ? 5 : 1;
        uint Query(IntPtr buffer, ref int length) => tcp ? GetExtendedTcpTable(buffer, ref length, false, family, tableClass, 0) : GetExtendedUdpTable(buffer, ref length, false, family, tableClass, 0);
        if (Query(IntPtr.Zero, ref size) != 122 || size < 4 || size > 64 * 1024 * 1024) return;
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            if (Query(memory, ref size) != 0) return;
            var bytes = new byte[size]; Marshal.Copy(memory, bytes, 0, size);
            int count = BitConverter.ToInt32(bytes), rowSize = family == 2 ? tcp ? 24 : 12 : tcp ? 56 : 28;
            int addressOffset = tcp && family == 2 ? 4 : 0, addressLength = family == 2 ? 4 : 16;
            int portOffset = family == 2 ? tcp ? 8 : 4 : 20, pidOffset = family == 2 ? tcp ? 20 : 8 : tcp ? 52 : 24;
            for (int i = 0; i < count && 4L + (i + 1L) * rowSize <= size; i++)
            {
                int start = 4 + i * rowSize, pid = BitConverter.ToInt32(bytes, start + pidOffset);
                if (pid <= 0) continue;
                var ip = new IPAddress(bytes.AsSpan(start + addressOffset, addressLength));
                string key = ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ? "*" : AddressKey(ip);
                int port = bytes[start + portOffset] * 256 + bytes[start + portOffset + 1];
                owners.Add(new(tcp ? "tcp" : "udp", key, port, pid));
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
