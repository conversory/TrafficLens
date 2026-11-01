namespace TrafficLens;

internal enum ClashRoute { Unknown, Proxy, Direct, Other }

internal sealed record ClashRouteTotals(long ProxyUp = 0, long ProxyDown = 0, long DirectUp = 0, long DirectDown = 0,
    long OtherUp = 0, long OtherDown = 0)
{
    public long Uploaded => ProxyUp + DirectUp + OtherUp;
    public long Downloaded => ProxyDown + DirectDown + OtherDown;
    internal ClashRouteTotals Add(ClashRouteTotals b) => new(ProxyUp + b.ProxyUp, ProxyDown + b.ProxyDown,
        DirectUp + b.DirectUp, DirectDown + b.DirectDown, OtherUp + b.OtherUp, OtherDown + b.OtherDown);
    internal static ClashRouteTotals Sum(IEnumerable<ClashObserved> rows)
    {
        long pu = 0, pd = 0, du = 0, dd = 0, ou = 0, od = 0;
        foreach (var c in rows)
            switch (c.Connection.Route)
            {
                case ClashRoute.Proxy: pu += c.Uploaded; pd += c.Downloaded; break;
                case ClashRoute.Direct: du += c.Uploaded; dd += c.Downloaded; break;
                default: ou += c.Uploaded; od += c.Downloaded; break;
            }
        return new(pu, pd, du, dd, ou, od);
    }
}

internal static class ClashRoutes
{
    // Mihomo's Chain.Last() is chains[0]: the actual outbound, not the selector group.
    // Match adapter types from /proxies, so a custom-named Direct also counts as direct.
    internal static ClashRoute Classify(string outbound, IReadOnlyDictionary<string, string>? types)
    {
        if (types != null && types.TryGetValue(outbound, out string? type))
            return type.ToUpperInvariant() switch
            {
                "DIRECT" => ClashRoute.Direct,
                "REJECT" or "REJECTDROP" or "DNS" or "PASS" or "PASSRULE" or "REMATCH" => ClashRoute.Other,
                "SHADOWSOCKS" or "SHADOWSOCKSR" or "SNELL" or "SOCKS5" or "HTTP" or "VMESS" or "VLESS" or "TROJAN"
                    or "HYSTERIA" or "HYSTERIA2" or "WIREGUARD" or "TUIC" or "SSH" or "MIERU" or "ANYTLS" or "SUDOKU"
                    or "MASQUE" or "TRUSTTUNNEL" or "SHADOWQUIC" or "OPENVPN" or "TAILSCALE" or "ZEROTIER" or "EASYTIER" or "GOSTRELAY" => ClashRoute.Proxy,
                _ => ClashRoute.Unknown
            };
        return outbound switch
        {
            "DIRECT" => ClashRoute.Direct,
            "REJECT" or "REJECT-DROP" or "PASS" => ClashRoute.Other,
            _ => ClashRoute.Unknown
        };
    }
    internal static string Label(ClashRoute route) => route switch
    {
        ClashRoute.Proxy => "代理 / VPN", ClashRoute.Direct => "直连", ClashRoute.Other => "其他", _ => "未知"
    };
    internal static bool Matches(ClashRoute route, int filter) => filter switch
    {
        1 => route == ClashRoute.Proxy, 2 => route == ClashRoute.Direct,
        3 => route is ClashRoute.Unknown or ClashRoute.Other, _ => true
    };
}
