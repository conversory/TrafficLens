using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TrafficLens;

internal static class HistoryPaths
{
    internal static string DirectoryPath { get; private set; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "TrafficLens-Data", "History"));
    internal static void Configure(string[] args)
    {
        int index = Array.IndexOf(args, "--data-dir");
        if (index >= 0 && index + 1 < args.Length) DirectoryPath = Path.GetFullPath(args[index + 1]);
        else if (args.Length >= 2 && args[0] is "--ui-check" or "--preview" or "--tray-test" or "--background-check" or "--history-ui-check" or "--history-test" or "--history-live-test")
            DirectoryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "history-check-" + Path.GetFileNameWithoutExtension(args[1]));
    }
}

internal sealed record DailyProgram(string Key, string Process, ClashRouteTotals Routes, DateTime FirstSeen, DateTime LastSeen);

internal sealed record DailyDestination(string Key, string Process, string Host, string Outbound, ClashRoute Route, long Uploaded, long Downloaded);

internal sealed class DailyTraffic
{
    public int Schema { get; set; } = 1;
    public string Day { get; set; } = "";
    public long KernelUploaded { get; set; }
    public long KernelDownloaded { get; set; }
    public DateTime SavedAtUtc { get; set; }
    public Dictionary<string, DailyProgram> Programs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DailyDestination> Destinations { get; set; } = new();
    public long OverflowDetailUploaded { get; set; }
    public long OverflowDetailDownloaded { get; set; }
    public DateTime? FirstObserved { get; set; }
    public DateTime? LastObserved { get; set; }
    public double ObservedSeconds { get; set; }
    public int SamplingGaps { get; set; }
    public long Samples { get; set; }
    public long AttributionUploadDifference => KernelUploaded - Programs.Values.Sum(p => p.Routes.Uploaded);
    public long AttributionDownloadDifference => KernelDownloaded - Programs.Values.Sum(p => p.Routes.Downloaded);
    internal long Revision;
    internal bool Dirty;
    internal DailyTraffic Copy() => new() { Schema = Schema, Day = Day, KernelUploaded = KernelUploaded,
        KernelDownloaded = KernelDownloaded, SavedAtUtc = SavedAtUtc, Revision = Revision, Dirty = Dirty,
        Programs = new(Programs, StringComparer.OrdinalIgnoreCase), Destinations = new(Destinations),
        OverflowDetailUploaded = OverflowDetailUploaded, OverflowDetailDownloaded = OverflowDetailDownloaded,
        FirstObserved = FirstObserved, LastObserved = LastObserved, ObservedSeconds = ObservedSeconds,
        SamplingGaps = SamplingGaps, Samples = Samples };
}

internal sealed class DailyHistory : IDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly Dictionary<string, DailyTraffic> cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> recovered = new(StringComparer.Ordinal);
    private readonly System.Threading.Timer? autosave;
    private FileStream? writerLease;
    private DateTime nextSave = DateTime.MinValue;
    private bool disposed;
    private string? loadError;
    internal string DirectoryPath { get; }
    internal bool CanWrite => writerLease != null && !disposed;
    internal string? Error { get; private set; }
    internal string? RecoveryNotice { get; private set; }
    internal DateTime? LastSaved { get; private set; }
    internal long Version { get; private set; }
    internal int CachedDays { get { lock (gate) return cache.Count; } }

    internal DailyHistory(string directory, bool automatic = true)
    {
        DirectoryPath = Path.GetFullPath(directory);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            writerLease = new FileStream(Path.Combine(DirectoryPath, "history.writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException) { Error = "历史目录无法写入或已有另一窗口保存；本窗口不重复记账。"; }
        catch (UnauthorizedAccessException) { Error = "历史目录没有写入权限，自动保存未生效。"; }
        if (CanWrite && automatic) autosave = new System.Threading.Timer(async _ =>
        {
            try { await FlushAsync(true); }
            catch (Exception ex) { Error = "自动保存失败：" + ex.Message; }
        }, null, 5000, 5000);
    }

    private string FilePath(string day) => Path.Combine(DirectoryPath, day + ".json");
    private static DailyTraffic ReadFile(string path, string day)
    {
        var data = JsonSerializer.Deserialize<DailyTraffic>(File.ReadAllText(path, Encoding.UTF8), Reports.JsonOptions)
            ?? throw new InvalidDataException("历史文件为空。");
        if (data.Schema != 1 || data.Day != day || data.KernelUploaded < 0 || data.KernelDownloaded < 0 || data.Programs == null)
            throw new InvalidDataException("历史文件格式或日期不正确。");
        foreach (var (key, p) in data.Programs)
            if (p == null || p.Routes == null || key != p.Key || string.IsNullOrWhiteSpace(p.Process) ||
                new[] { p.Routes.ProxyUp, p.Routes.ProxyDown, p.Routes.DirectUp, p.Routes.DirectDown, p.Routes.OtherUp, p.Routes.OtherDown }.Any(n => n < 0))
                throw new InvalidDataException("历史程序计数不正确。");
        data.Programs = new(data.Programs, StringComparer.OrdinalIgnoreCase);
        return data;
    }
    private DailyTraffic Load(string day)
    {
        if (cache.TryGetValue(day, out var data)) return data;
        string path = FilePath(day);
        try
        {
            if (File.Exists(path)) data = ReadFile(path, day);
            else if (File.Exists(path + ".bak")) { data = ReadFile(path + ".bak", day); RecoveryNotice = day + " 主文件缺失，已读取备份。"; }
            else data = new DailyTraffic { Day = day };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            try { data = ReadFile(path + ".bak", day); recovered.Add(day); RecoveryNotice = day + " 主文件异常，已从备份恢复。"; }
            catch (Exception backup) when (backup is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            { throw new IOException(day + " 历史文件和备份均无法读取，已保留原文件，未覆盖。", ex); }
        }
        cache[day] = data;
        return data;
    }

    internal static List<(string Day, long Uploaded, long Downloaded)> Split(DateTime from, DateTime to, long up, long down)
    {
        from = from.Kind == DateTimeKind.Utc ? from.ToLocalTime() : from;
        to = to.Kind == DateTimeKind.Utc ? to.ToLocalTime() : to;
        if (to <= from || from.Date == to.Date || (up == 0 && down == 0)) return [(to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), up, down)];
        var pieces = new List<(string, long, long)>();
        long totalTicks = (to - from).Ticks, usedUp = 0, usedDown = 0;
        for (var cursor = from; cursor < to;)
        {
            var end = cursor.Date.AddDays(1); if (end > to) end = to;
            decimal fraction = (decimal)(end - cursor).Ticks / totalTicks;
            long u = end == to ? up - usedUp : (long)(up * fraction);
            long d = end == to ? down - usedDown : (long)(down * fraction);
            pieces.Add((cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), u, d));
            usedUp += u; usedDown += d; cursor = end;
        }
        return pieces;
    }

    internal void Apply(ClashSample sample)
    {
        if (!CanWrite) return;
        lock (gate)
        {
            try
            {
                foreach (var part in Split(sample.From, sample.To, sample.Uploaded, sample.Downloaded))
                {
                    var day = Load(part.Day);
                    if (part.Uploaded != 0 || part.Downloaded != 0 || !File.Exists(FilePath(part.Day)))
                    {
                        day.KernelUploaded += part.Uploaded; day.KernelDownloaded += part.Downloaded;
                        day.Dirty = true; day.Revision++; Version++;
                    }
                }
                var observed = Load(sample.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                observed.FirstObserved ??= sample.To;
                observed.LastObserved = sample.To;
                observed.Samples++;
                double elapsed = (sample.To - sample.From).TotalSeconds;
                if (elapsed > 5) observed.SamplingGaps++;
                else if (elapsed > 0)
                {
                    foreach (var span in Split(sample.From, sample.To, (long)(elapsed * 1000), 0))
                    {
                        var covered = Load(span.Day);
                        covered.ObservedSeconds += span.Uploaded / 1000d;
                        covered.Dirty = true; covered.Revision++;
                    }
                }
                observed.Dirty = true; observed.Revision++; Version++;
                foreach (var delta in sample.Connections)
                foreach (var part in Split(delta.From, sample.To, delta.Uploaded, delta.Downloaded))
                {
                    if (part.Uploaded == 0 && part.Downloaded == 0) continue;
                    var day = Load(part.Day);
                    string key = ClashStore.ProgramKey(delta.Connection), name = ClashStore.DisplayProcess(delta.Connection);
                    var add = delta.Connection.Route switch
                    {
                        ClashRoute.Proxy => new ClashRouteTotals(ProxyUp: part.Uploaded, ProxyDown: part.Downloaded),
                        ClashRoute.Direct => new ClashRouteTotals(DirectUp: part.Uploaded, DirectDown: part.Downloaded),
                        _ => new ClashRouteTotals(OtherUp: part.Uploaded, OtherDown: part.Downloaded)
                    };
                    if (day.Programs.TryGetValue(key, out var old))
                        day.Programs[key] = old with { Routes = old.Routes.Add(add), LastSeen = sample.To,
                            Process = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : old.Process };
                    else day.Programs[key] = new(key, name, add, delta.From, sample.To);
                    string detailKey = JsonSerializer.Serialize(new[] { key, delta.Connection.Host, delta.Connection.Outbound, delta.Connection.Route.ToString() });
                    if (day.Destinations.TryGetValue(detailKey, out var detail))
                        day.Destinations[detailKey] = detail with { Uploaded = detail.Uploaded + part.Uploaded, Downloaded = detail.Downloaded + part.Downloaded };
                    else if (day.Destinations.Count < 10000)
                        day.Destinations[detailKey] = new(detailKey, name, delta.Connection.Host, delta.Connection.Outbound, delta.Connection.Route, part.Uploaded, part.Downloaded);
                    else { day.OverflowDetailUploaded += part.Uploaded; day.OverflowDetailDownloaded += part.Downloaded; }
                    day.Dirty = true; day.Revision++; Version++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { loadError = Error = ex.Message; }
        }
    }

    internal DailyTraffic ReadDay(DateTime date)
    {
        lock (gate)
        {
            string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!CanWrite) cache.Remove(key);
            var result = Load(key).Copy(); TrimCache(result.Day); return result;
        }
    }
    private void TrimCache(string keep = "")
    {
        foreach (string day in cache.Keys.OrderDescending().Skip(3).ToArray())
            if (day != keep && !cache[day].Dirty) cache.Remove(day);
    }
    internal async Task<bool> FlushAsync(bool force = false)
    {
        if (!CanWrite) return false;
        if (!force && DateTime.UtcNow < nextSave) return true;
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!CanWrite) return false;
            List<DailyTraffic> dirty;
            lock (gate) dirty = cache.Values.Where(d => d.Dirty).Select(d => d.Copy()).ToList();
            await Task.Run(() =>
            {
                foreach (var day in dirty)
                {
                    day.SavedAtUtc = DateTime.UtcNow;
                    string path = FilePath(day.Day), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                        { JsonSerializer.Serialize(stream, day, Reports.JsonOptions); stream.Flush(true); }
                        bool wasRecovered; lock (gate) wasRecovered = recovered.Contains(day.Day);
                        if (File.Exists(path) && wasRecovered)
                        {
                            File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), true);
                            File.Move(temp, path, true); // Preserve the last good .bak rather than backing up corrupt data.
                            lock (gate) recovered.Remove(day.Day);
                        }
                        else if (File.Exists(path)) File.Replace(temp, path, path + ".bak", true);
                        else File.Move(temp, path);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    lock (gate)
                    {
                        if (cache.TryGetValue(day.Day, out var current) && current.Revision == day.Revision)
                        { current.Dirty = false; current.SavedAtUtc = day.SavedAtUtc; }
                    }
                }
            }).ConfigureAwait(false);
            lock (gate) { TrimCache(); if (dirty.Count > 0) LastSaved = DateTime.Now; }
            if (dirty.Count > 0) Error = loadError;
            nextSave = DateTime.UtcNow.AddSeconds(5);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Error = "自动保存失败：" + ex.Message; nextSave = DateTime.UtcNow.AddSeconds(5); return false; }
        finally { writing.Release(); }
    }

    internal List<string> Days() => Directory.Exists(DirectoryPath) ? Directory.EnumerateFiles(DirectoryPath, "*.json")
        .Select(Path.GetFileNameWithoutExtension).Where(s => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        .Select(s => s!).OrderDescending().ToList() : [];

    internal static List<DailyProgram> Filter(DailyTraffic day, string search, int route, bool uploadingOnly) => day.Programs.Values
        .Where(p => p.Process.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
        .Select(p => p with { Routes = route switch
        {
            1 => new(ProxyUp: p.Routes.ProxyUp, ProxyDown: p.Routes.ProxyDown),
            2 => new(DirectUp: p.Routes.DirectUp, DirectDown: p.Routes.DirectDown),
            3 => new(OtherUp: p.Routes.OtherUp, OtherDown: p.Routes.OtherDown), _ => p.Routes
        } }).Where(p => p.Routes.Uploaded + p.Routes.Downloaded > 0 && (!uploadingOnly || p.Routes.Uploaded > 0)).ToList();

    internal static void Export(string path, DailyTraffic day, IEnumerable<DailyProgram> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        void Row(params object?[] cells) => writer.WriteLine(string.Join(",", cells.Select(Reports.Csv)));
        Row("流量侦探每日历史", day.Day, "Clash 已归因程序流量；跨午夜按采样间隔比例分配");
        Row("程序", "本次日期上传 B", "本次日期下载 B", "代理上传 B", "代理下载 B", "直连上传 B", "直连下载 B", "其他/未知上传 B", "其他/未知下载 B", "最近观察");
        foreach (var p in rows) Row(p.Process, p.Routes.Uploaded, p.Routes.Downloaded, p.Routes.ProxyUp, p.Routes.ProxyDown,
            p.Routes.DirectUp, p.Routes.DirectDown, p.Routes.OtherUp, p.Routes.OtherDown, p.LastSeen.ToString("O"));
    }
    public void Dispose()
    {
        if (disposed) return;
        autosave?.Dispose();
        if (CanWrite) FlushAsync(true).GetAwaiter().GetResult();
        writing.Wait();
        try { disposed = true; writerLease?.Dispose(); writerLease = null; }
        finally { writing.Release(); }
    }
}
