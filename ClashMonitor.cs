using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrafficLens;

// Credentials are held only by this transport object. Do not serialize it or include it in reports.
internal sealed class ClashEndpoint(string address, string pipe, string secret)
{
    public string Description => pipe.Length > 0 ? "Clash Verge 本机管道" : address;
    public HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
        Uri uri;
        if (pipe.Length > 0)
        {
            const string prefix = @"\\.\pipe\";
            if (!pipe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("仅支持本机命名管道。");
            string name = pipe[prefix.Length..];
            handler.ConnectCallback = async (_, token) =>
            {
                var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await stream.ConnectAsync(1600, token); return stream; }
                catch { stream.Dispose(); throw; }
            };
            uri = new Uri("http://localhost/");
        }
        else
        {
            var local = address.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "[::1]");
            uri = new Uri(local.Contains("://") ? local : "http://" + local);
            if (!uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new InvalidOperationException("控制接口必须是本机地址。");
        }
        var client = new HttpClient(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(4) };
        if (secret.Length > 0) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }
    public override string ToString() => Description;
    internal static string Scalar(string text, string key)
    {
        var match = Regex.Match(text, "^" + Regex.Escape(key) + @":[ \t]*([^\r\n]*)", RegexOptions.Multiline);
        if (!match.Success) return "";
        var value = match.Groups[1].Value.Trim();
        if (value.StartsWith('"')) { try { return JsonSerializer.Deserialize<string>(value) ?? ""; } catch { return ""; } }
        if (value.StartsWith('\'')) return value.Trim('\'').Replace("''", "'");
        int comment = value.IndexOf(" #", StringComparison.Ordinal); return comment >= 0 ? value[..comment].TrimEnd() : value;
    }
    public static List<ClashEndpoint> Discover()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev"), Path.Combine(appData, "io.github.clash-verge.clash-verge"),
            Path.Combine(appData, "clash-verge"), Path.Combine(home, ".config", "clash"), Path.Combine(home, ".config", "mihomo") };
        var result = new List<ClashEndpoint>(); var seen = new HashSet<string>();
        string[] livePipes = [];
        try { livePipes = Directory.GetFiles(@"\\.\pipe\").Where(p => Path.GetFileName(p).StartsWith("verge-mihomo-", StringComparison.OrdinalIgnoreCase)).ToArray(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        foreach (var root in roots)
        foreach (var file in new[] { "clash-verge.yaml", "config.yaml", "config.yml", "clash.yaml" })
        {
            try
            {
                var path = Path.Combine(root, file); if (!File.Exists(path)) continue;
                string text = File.ReadAllText(path), secret = Scalar(text, "secret"), pipe = Scalar(text, "external-controller-pipe"), http = Scalar(text, "external-controller");
                // Verge may override the YAML pipe via its core command line (production vs sidecar).
                foreach (var live in livePipes)
                    if (seen.Add(live + "\n" + secret)) result.Add(new("", live, secret));
                if (pipe.Length > 0 && seen.Add(pipe + "\n" + secret)) result.Add(new("", pipe, secret));
                if (http.Length > 0 && seen.Add(http + "\n" + secret)) result.Add(new(http, "", secret));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
}

internal sealed record ClashConnection(string Id, string Process, string ProcessPath, string Host, string DestinationIp,
    string DestinationPort, string Source, string Network, string Inbound, string Chain, string Rule,
    long Upload, long Download, DateTime Start, string ProcessOrigin = "Clash", ClashRoute Route = ClashRoute.Unknown, string Outbound = "");
internal sealed record ClashFrame(long UploadTotal, long DownloadTotal, List<ClashConnection> Connections, DateTime Captured);
internal sealed record ClashTrafficDelta(ClashConnection Connection, long Uploaded, long Downloaded, DateTime From);
internal sealed record ClashSample(DateTime From, DateTime To, long Uploaded, long Downloaded, List<ClashTrafficDelta> Connections);
internal sealed record ClashObserved(ClashConnection Connection, long Uploaded, long Downloaded,
    double UploadRate, double DownloadRate, bool Active, DateTime FirstSeen, DateTime LastSeen);
internal sealed record ClashSnapshot(DateTime Started, DateTime Captured, long Uploaded, long Downloaded,
    double UploadRate, double DownloadRate, List<ClashObserved> Connections, int Restarts, long Frames,
    long FoldedUploaded = 0, long FoldedDownloaded = 0, int FoldedConnections = 0, ClashRouteTotals? FoldedRoutes = null)
{
    public ClashRouteTotals Routes => ClashRouteTotals.Sum(Connections).Add(FoldedRoutes ?? new());
}
internal sealed record ClashGroup(string Key, string Process, string Host, string Chain, long Uploaded, long Downloaded,
    double UploadRate, double DownloadRate, int Active, int Connections, DateTime LastSeen, int Domains, int Paths, ClashRouteTotals Routes);

internal sealed class ClashApi : IDisposable
{
    private readonly HttpClient client;
    private readonly ClashProcessResolver resolver = new();
    private Dictionary<string, string> outboundTypes = new(StringComparer.Ordinal);
    private DateTime nextTypesRefresh;
    public string Description { get; }
    private ClashApi(ClashEndpoint endpoint) { Description = endpoint.Description; client = endpoint.CreateClient(); }
    public static async Task<ClashApi> ConnectAsync(CancellationToken token)
    {
        var endpoints = ClashEndpoint.Discover();
        if (endpoints.Count == 0) throw new InvalidOperationException("未找到本机 Clash 配置，请先打开 Clash Verge。");
        var failures = new List<string>();
        foreach (var endpoint in endpoints)
        {
            ClashApi? api = null;
            try { api = new(endpoint); await api.ReadAsync(token); return api; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { api?.Dispose(); throw; }
            catch (Exception ex)
            {
                api?.Dispose();
                string reason = ex is HttpRequestException h && h.StatusCode == HttpStatusCode.Unauthorized ? "鉴权失败" :
                    ex is UnauthorizedAccessException || ex.InnerException is UnauthorizedAccessException ? "访问被拒绝，可尝试以管理员运行" :
                    ex is HttpRequestException http && http.StatusCode != null ? "HTTP " + (int)http.StatusCode :
                    ex.InnerException?.GetType().Name ?? ex.GetType().Name;
                failures.Add(endpoint.Description + "：" + reason);
            }
        }
        throw new InvalidOperationException(string.Join("；", failures) + "。确认 Clash 正在运行，再点击重新连接。");
    }
    public async Task<ClashFrame> ReadAsync(CancellationToken token)
    {
        if (DateTime.UtcNow >= nextTypesRefresh)
        {
            nextTypesRefresh = DateTime.UtcNow.AddMinutes(1);
            try
            {
                using var typesResponse = await client.GetAsync("/proxies", token);
                typesResponse.EnsureSuccessStatusCode();
                using var typesStream = await typesResponse.Content.ReadAsStreamAsync(token);
                using var typesJson = await JsonDocument.ParseAsync(typesStream, cancellationToken: token);
                outboundTypes = ParseTypes(typesJson.RootElement);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
            { outboundTypes.Clear(); } // Sampling can continue; unconfirmed exits are shown as unknown.
        }
        using var response = await client.GetAsync("/connections", HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return resolver.Enrich(Parse(json.RootElement, DateTime.Now, outboundTypes));
    }
    internal static Dictionary<string, string> ParseTypes(JsonElement root)
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("proxies", out var proxies) && proxies.ValueKind == JsonValueKind.Object)
            foreach (var proxy in proxies.EnumerateObject()) types[proxy.Name] = Text(proxy.Value, "type");
        return types;
    }
    internal static ClashFrame Parse(JsonElement root, DateTime time, IReadOnlyDictionary<string, string>? types = null)
    {
        var rows = new List<ClashConnection>();
        if (root.TryGetProperty("connections", out var connections) && connections.ValueKind == JsonValueKind.Array)
        foreach (var c in connections.EnumerateArray())
        {
            if (!c.TryGetProperty("metadata", out var m)) continue;
            string id = Text(c, "id"); if (id.Length == 0) continue;
            string process = Text(m, "process"), path = Text(m, "processPath"), host = Text(m, "host");
            if (process.Length == 0 && path.Length > 0) process = Path.GetFileName(path);
            if (process.Length == 0) process = "未知程序";
            string ip = Text(m, "destinationIP");
            if (host.Length == 0) host = Text(m, "sniffHost");
            if (host.Length == 0) host = ip.Length > 0 ? ip : "未知目标";
            host = host.TrimEnd('.').ToLowerInvariant();
            string chain = "", outbound = "";
            if (c.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array)
            {
                var names = chains.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                outbound = names.FirstOrDefault() ?? "";
                chain = string.Join(" → ", names.Reverse());
            }
            string rule = Text(c, "rule"), payload = Text(c, "rulePayload"); if (payload.Length > 0) rule += ": " + payload;
            DateTime.TryParse(Text(c, "start"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start);
            rows.Add(new(id, process, path, host, ip, Text(m, "destinationPort"), Text(m, "sourceIP") + ":" + Text(m, "sourcePort"),
                Text(m, "network"), Text(m, "type"), chain, rule, Number(c, "upload"), Number(c, "download"), start,
                process == "未知程序" ? "未提供" : "Clash", ClashRoutes.Classify(outbound, types), outbound));
        }
        return new(Number(root, "uploadTotal"), Number(root, "downloadTotal"), rows, time);
    }
    private static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "" : "";
    private static long Number(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) ? Math.Max(0, n) : 0;
    public void Dispose() => client.Dispose();
}

internal sealed class ClashStore
{
    private readonly Dictionary<string, ClashObserved> connections = new();
    private ClashFrame? previous;
    private DateTime started = DateTime.Now;
    private long uploaded, downloaded, frames, foldedUp, foldedDown;
    private int restarts, foldedConnections;
    private ClashRouteTotals foldedRoutes = new();
    internal event Action<ClashSample>? Sampled;
    public void Apply(ClashFrame frame)
    {
        bool first = previous == null;
        DateTime intervalStart = previous?.Captured ?? frame.Captured;
        var deltas = new List<ClashTrafficDelta>();
        if (first) started = frame.Captured;
        double seconds = first ? 1 : Math.Max(.001, (frame.Captured - previous!.Captured).TotalSeconds);
        bool reset = !first && (frame.UploadTotal < previous!.UploadTotal || frame.DownloadTotal < previous.DownloadTotal);
        if (reset) { restarts++; foreach (var (id, c) in connections.ToArray()) connections[id] = c with { Active = false, UploadRate = 0, DownloadRate = 0 }; }
        long upDelta = first ? 0 : reset ? frame.UploadTotal : Math.Max(0, frame.UploadTotal - previous!.UploadTotal);
        long downDelta = first ? 0 : reset ? frame.DownloadTotal : Math.Max(0, frame.DownloadTotal - previous!.DownloadTotal);
        uploaded += upDelta; downloaded += downDelta;
        var active = frame.Connections.Select(c => c.Id).ToHashSet();
        foreach (var (id, c) in connections.ToArray())
            if (!active.Contains(id)) connections[id] = c with { Active = false, UploadRate = 0, DownloadRate = 0 };
        foreach (var c in frame.Connections)
        {
            if (connections.TryGetValue(c.Id, out var reused) && (reset || reused.Connection.Start != c.Start))
            {
                foldedUp += reused.Uploaded; foldedDown += reused.Downloaded; foldedConnections++;
                foldedRoutes = foldedRoutes.Add(ClashRouteTotals.Sum([reused])); connections.Remove(c.Id);
            }
            if (connections.TryGetValue(c.Id, out var old))
            {
                var resolved = c.Route == ClashRoute.Unknown && c.Outbound == old.Connection.Outbound
                    ? c with { Route = old.Connection.Route } : c;
                long u = c.Upload >= old.Connection.Upload ? c.Upload - old.Connection.Upload : c.Upload;
                long d = c.Download >= old.Connection.Download ? c.Download - old.Connection.Download : c.Download;
                connections[c.Id] = old with { Connection = resolved, Uploaded = old.Uploaded + u, Downloaded = old.Downloaded + d, UploadRate = u / seconds, DownloadRate = d / seconds, Active = true, LastSeen = frame.Captured };
                if (u > 0 || d > 0) deltas.Add(new(resolved, u, d, intervalStart));
            }
            else
            {
                // Existing connections are baselined at first observation. Newly started ones
                // count their initial payload once, so long-lived connections are not re-added.
                bool newDuringObservation = !first && c.Start != default && c.Start.ToUniversalTime() >= started.ToUniversalTime();
                long u = newDuringObservation ? c.Upload : 0, d = newDuringObservation ? c.Download : 0;
                connections[c.Id] = new(c, u, d, u / seconds, d / seconds, true, frame.Captured, frame.Captured);
                var localStart = c.Start.Kind == DateTimeKind.Utc ? c.Start.ToLocalTime() : c.Start;
                if (u > 0 || d > 0) deltas.Add(new(c, u, d, localStart > intervalStart && localStart <= frame.Captured ? localStart : intervalStart));
            }
        }
        // Bound history without silently losing its byte totals.
        if (connections.Count > 20000)
        {
            foreach (var c in connections.Values.Where(c => !c.Active).OrderBy(c => c.LastSeen).Take(connections.Count - 18000).ToArray())
            {
                foldedUp += c.Uploaded; foldedDown += c.Downloaded; foldedConnections++;
                foldedRoutes = foldedRoutes.Add(ClashRouteTotals.Sum([c])); connections.Remove(c.Connection.Id);
            }
        }
        previous = frame; frames++;
        Current = new(started, frame.Captured, uploaded, downloaded, first ? 0 : upDelta / seconds, first ? 0 : downDelta / seconds,
            connections.Values.ToList(), restarts, frames, foldedUp, foldedDown, foldedConnections, foldedRoutes);
        Sampled?.Invoke(new(intervalStart, frame.Captured, upDelta, downDelta, deltas));
    }
    public ClashSnapshot Current { get; private set; } = new(DateTime.Now, DateTime.Now, 0, 0, 0, 0, [], 0, 0);
    public void Clear()
    {
        var baseline = previous; connections.Clear(); uploaded = downloaded = frames = foldedUp = foldedDown = 0; restarts = foldedConnections = 0;
        foldedRoutes = new();
        started = DateTime.Now; previous = null;
        if (baseline != null) Apply(baseline with { Captured = started });
    }
    public static List<ClashGroup> Group(ClashSnapshot snapshot, int grouping, string search, bool uploadingOnly, bool activeOnly, int routeFilter = 0)
    {
        var rows = FilterConnections(snapshot, search, activeOnly, routeFilter);
        return rows.GroupBy(c => GroupKey(c, grouping), StringComparer.OrdinalIgnoreCase).Select(g => new ClashGroup(g.Key,
            grouping == 1 ? Join(g.Select(c => DisplayProcess(c.Connection))) : PreferredProcess(g),
            grouping == 2 ? Join(g.Select(c => c.Connection.Host)) : g.First().Connection.Host,
            Join(g.Select(c => c.Connection.Chain)), g.Sum(c => c.Uploaded), g.Sum(c => c.Downloaded),
            g.Sum(c => c.UploadRate), g.Sum(c => c.DownloadRate), g.Count(c => c.Active), g.Count(), g.Max(c => c.LastSeen),
            g.Select(c => c.Connection.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            g.Select(c => c.Connection.ProcessPath).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(), ClashRouteTotals.Sum(g)))
            .Where(g => !uploadingOnly || g.Uploaded > 0 || g.UploadRate > 0).ToList();
    }
    internal static IEnumerable<ClashObserved> FilterConnections(ClashSnapshot snapshot, string search, bool activeOnly, int routeFilter = 0)
    {
        var rows = snapshot.Connections.Where(c => (!activeOnly || c.Active) && ClashRoutes.Matches(c.Connection.Route, routeFilter));
        return search.Trim().Length == 0 ? rows : rows.Where(c => string.Join(" ", c.Connection.Process, c.Connection.ProcessPath, c.Connection.Host,
            c.Connection.DestinationIp, c.Connection.Chain, c.Connection.Rule).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
    }
    internal static string GroupKey(ClashObserved c, int grouping) => grouping switch { 1 => c.Connection.Host, 2 => ProgramKey(c.Connection), _ => (c.Connection.ProcessPath.Length > 0 ? c.Connection.ProcessPath : ProgramKey(c.Connection)) + "\n" + c.Connection.Host };
    internal static string DisplayProcess(ClashConnection c)
    {
        string name = Path.GetFileName(c.Process.Trim());
        if (name.Length == 0) name = Path.GetFileName(c.ProcessPath);
        return name.Length == 0 ? "未知程序" : name;
    }
    internal static string ProgramKey(ClashConnection c)
    {
        string name = DisplayProcess(c);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToUpperInvariant();
    }
    private static string PreferredProcess(IEnumerable<ClashObserved> rows) => rows.Select(c => DisplayProcess(c.Connection))
        .OrderByDescending(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).First();
    private static string Join(IEnumerable<string> values)
    {
        var all = values.Where(v => v.Length > 0).Distinct().ToArray();
        return all.Length <= 2 ? string.Join(" / ", all) : all[0] + " 等 " + all.Length + " 项";
    }
}
