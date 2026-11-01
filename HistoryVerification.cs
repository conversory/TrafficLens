using System.Text.Json;

namespace TrafficLens;

internal static class HistoryVerification
{
    private static void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
    private static ClashConnection Connection(string id, string process, ClashRoute route, DateTime start, long up = 0, long down = 0) =>
        new(id, process, "C:\\Sample\\" + process, "sample.test", "1.2.3.4", "443", "127.0.0.1:50000", "tcp", "HTTP", "Test", "Match", up, down, start, "Clash", route, "Test");

    internal static object Verify(string directory)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json").Any())
            throw new InvalidOperationException("测试目录已有历史文件，请使用新的空测试目录；原文件未修改。");
        var checks = new List<string>();
        DateTime start = new(2026, 10, 15, 23, 59, 59, DateTimeKind.Local), end = start.AddSeconds(2);
        var c = Connection("p", "Sample.exe", ClashRoute.Proxy, start.AddMinutes(-5), 1000, 2000);
        var d = Connection("d", "sample.EXE", ClashRoute.Direct, start.AddMinutes(-4), 100, 200);
        using (var history = new DailyHistory(directory, automatic: false))
        {
            var store = new ClashStore(); store.Sampled += history.Apply;
            store.Apply(new(5000, 9000, [c, d], start));
            Check(history.ReadDay(start).Programs.Count == 0, "Pre-observation traffic was backfilled");
            var later = new ClashFrame(5400, 9800, [c with { Upload = 1300, Download = 2600 }, d with { Upload = 200, Download = 400 }], end);
            store.Apply(later); store.Apply(later with { Captured = end.AddSeconds(1) });
            var yesterday = history.ReadDay(start); var today = history.ReadDay(end);
            Check(yesterday.Programs.Single().Value.Routes == new ClashRouteTotals(150, 300, 50, 100), "Midnight split or same-name routes incorrect");
            Check(today.Programs.Single().Value.Routes == yesterday.Programs.Single().Value.Routes, "Midnight bytes lost");
            checks.Add("Midnight split preserves integer upload/download totals and merges same-name routes");
            Check(yesterday.KernelUploaded + today.KernelUploaded == 400 && yesterday.KernelDownloaded + today.KernelDownloaded == 800, "Daily kernel deltas incorrect");
            checks.Add("Baselines and identical frames do not add history twice");
            store.Clear(); Check(history.ReadDay(end).Programs.Single().Value.Routes.Uploaded == 200, "Realtime reset erased history");
            checks.Add("Realtime reset retains daily history");
            Check(history.FlushAsync(true).GetAwaiter().GetResult(), "Initial save failed");
            var csv = Path.Combine(directory, "day.csv"); DailyHistory.Export(csv, yesterday, yesterday.Programs.Values);
            Check(File.ReadAllBytes(csv)[0] == 0xef && File.ReadAllText(csv).Contains("代理上传 B"), "Daily CSV invalid");
            checks.Add("Daily CSV contains per-route totals and UTF-8 BOM");
            using var second = new DailyHistory(directory, automatic: false);
            Check(!second.CanWrite, "Multiple writers accepted");
            second.Apply(new(end, end, 999, 999, []));
            Check(second.ReadDay(end).KernelUploaded == 200, "Readonly window duplicated traffic");
            checks.Add("Second writer becomes read-only without double counting");
        }
        using (var reopened = new DailyHistory(directory, automatic: false))
        {
            var store = new ClashStore(); store.Sampled += reopened.Apply;
            store.Apply(new(5400, 9800, [c with { Upload = 1300, Download = 2600 }], end.AddSeconds(2)));
            Check(reopened.ReadDay(end).Programs.Single().Value.Routes.Uploaded == 200, "Restart re-added cumulative connection");
            store.Apply(new(5500, 10000, [c with { Upload = 1400, Download = 2800 }], end.AddSeconds(3)));
            var unknown = Connection("unknown", "Other.exe", ClashRoute.Unknown, end.AddSeconds(4), 6, 14);
            store.Apply(new(5506, 10014, [c with { Upload = 1400, Download = 2800 }, unknown], end.AddSeconds(5)));
            Check(reopened.ReadDay(end).Programs.Values.Sum(p => p.Routes.Uploaded) == 306, "Restart continuation incorrect");
            Check(reopened.ReadDay(end).Programs["OTHER"].Routes.OtherDown == 14, "Unknown route lost");
            Check(DailyHistory.Filter(reopened.ReadDay(end), "sample", 2, false).Single().Routes == new ClashRouteTotals(DirectUp:50, DirectDown:100), "Daily search or direct filter incorrect");
            checks.Add("Reopen retains saved totals; new sessions add deltas without replay");
            checks.Add("Unknown routes, daily program search, and route filters reconcile");
            // Dispose must flush outstanding increments even before the five-second timer.
        }
        using (var saved = new DailyHistory(directory, automatic: false))
            Check(saved.ReadDay(end).Programs.Values.Sum(p => p.Routes.Downloaded) == 614, "Exit did not flush pending data");
        checks.Add("Graceful exit flushes pending daily increments");

        var foldDirectory = Path.Combine(directory, "fold-test");
        using (var folded = new DailyHistory(foldDirectory, automatic: false))
        {
            var store = new ClashStore(); store.Sampled += folded.Apply; store.Apply(new(0, 0, [], end));
            var many = Enumerable.Range(0, 20001).Select(i => Connection(i.ToString(), "Many.exe", ClashRoute.Direct, end.AddSeconds(1), 10, 20)).ToList();
            store.Apply(new(200010, 400020, many, end.AddSeconds(2))); store.Apply(new(200010, 400020, [], end.AddSeconds(3)));
            Check(store.Current.FoldedConnections == 2001 && folded.ReadDay(end).Programs.Single().Value.Routes.DirectUp == 200010, "History lost folded connection bytes");
        }
        checks.Add("Daily program totals survive 20k connection detail folding");

        var recoveryDirectory = Path.Combine(directory, "recovery-test");
        string recoveryPath = Path.Combine(recoveryDirectory, "2026-10-16.json");
        using (var history = new DailyHistory(recoveryDirectory, automatic: false))
        {
            history.Apply(new(end, end, 10, 20, [new(c, 10, 20, end)])); Check(history.FlushAsync(true).GetAwaiter().GetResult(), "Recovery fixture save failed");
            history.Apply(new(end, end, 30, 40, [new(c, 30, 40, end)])); Check(history.FlushAsync(true).GetAwaiter().GetResult(), "Backup creation failed");
        }
        File.WriteAllText(recoveryPath, "{truncated");
        using (var recovered = new DailyHistory(recoveryDirectory, automatic: false))
        {
            Check(recovered.ReadDay(end).Programs.Single().Value.Routes.ProxyUp == 10 && recovered.RecoveryNotice != null, "Backup recovery failed");
            recovered.Apply(new(end, end, 2, 3, [new(c, 2, 3, end)]));
        }
        using (var healthy = new DailyHistory(recoveryDirectory, automatic: false))
        {
            Check(healthy.ReadDay(end).Programs.Single().Value.Routes.ProxyUp == 12, "Recovered data was not safely saved");
            Check(File.ReadAllText(recoveryPath + ".bak").Contains("Sample.exe"), "Good backup overwritten by corrupt primary");
            healthy.Apply(new(end, end, 5, 7, [new(c, 5, 7, end)]));
            using (var held = new FileStream(recoveryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(!healthy.FlushAsync(true).GetAwaiter().GetResult() && healthy.Error != null, "Write failure was hidden");
            Check(healthy.FlushAsync(true).GetAwaiter().GetResult() && healthy.Error == null, "Pending data did not retry after write failure");
        }
        checks.Add("Corrupt primary restores good backup and retains corrupt copy");
        checks.Add("Write failure is visible and retries without dropping pending bytes");

        string badDirectory = Path.Combine(directory, "bad-test"); Directory.CreateDirectory(badDirectory);
        string badPath = Path.Combine(badDirectory, "2026-10-16.json"); File.WriteAllText(badPath, "{bad");
        using (var bad = new DailyHistory(badDirectory, automatic: false))
        {
            bad.Apply(new(end, end, 1, 1, [new(c, 1, 1, end)])); bad.FlushAsync(true).GetAwaiter().GetResult();
            Check(bad.Error != null && File.ReadAllText(badPath) == "{bad", "Unreadable history silently overwritten");
        }
        checks.Add("Unreadable history is preserved rather than reset silently");

        using (var bounded = new DailyHistory(Path.Combine(directory, "cache-test"), automatic: false))
        {
            for (int i = 0; i < 12; i++)
            {
                var time = end.AddDays(-i);
                bounded.Apply(new(time, time, 1, 2, [new(c, 1, 2, time)])); bounded.FlushAsync(true).GetAwaiter().GetResult();
            }
            for (int i = 0; i < 12; i++) bounded.ReadDay(end.AddDays(-i));
            Check(bounded.CachedDays <= 4 && bounded.Days().Count == 12, "History cache grows with all days");
        }
        checks.Add("Only a few days are cached; older day files remain available");
        using (var audit = new DailyHistory(Path.Combine(directory, "audit-test"), automatic: false))
        {
            var store = new ClashStore(); store.Sampled += audit.Apply;
            store.Apply(new(0, 0, [], end));
            var shortConnection = Connection("reuse", "Audit.exe", ClashRoute.Proxy, end.AddMilliseconds(100), 100, 200);
            store.Apply(new(140, 260, [shortConnection], end.AddSeconds(1)));
            var day = audit.ReadDay(end);
            Check(day.AttributionUploadDifference == 40 && day.AttributionDownloadDifference == 60, "Missing connection bytes must remain unassigned");
            Check(day.Programs["AUDIT"].Routes.ProxyUp == 100 && day.Destinations.Values.Single().Uploaded == 100, "Destination ledger differs from program totals");
            store.Apply(new(210, 350, [shortConnection with { Start = end.AddSeconds(1), Upload = 70, Download = 90 }], end.AddSeconds(2)));
            Check(store.Current.Routes.ProxyUp == 170, "Reused connection ID loses or duplicates bytes");
            store.Apply(new(210, 350, [], end.AddSeconds(12)));
            day = audit.ReadDay(end);
            Check(day.SamplingGaps == 1 && day.ObservedSeconds == 2, "Long gaps falsely counted as observed time");
            Check(day.AttributionUploadDifference == 40, "Signed reconciliation drifted");
            Check(audit.FlushAsync(true).GetAwaiter().GetResult(), "Audit persistence failed");
        }
        using (var audit = new DailyHistory(Path.Combine(directory, "audit-test"), automatic: false))
        {
            var day = audit.ReadDay(end);
            Check(day.Destinations.Values.Sum(d => d.Uploaded) == 170 && day.SamplingGaps == 1, "Audit/destinations lost after reopen");
        }
        checks.Add("Unattributed differences, reused IDs, observation gaps and destination persistence");
        return new { Passed = true, Checks = checks, FixtureDirectory = directory, EarlierDay = "2026-10-15", LaterDay = "2026-10-16", ExpectedLaterUploaded = 306, ExpectedLaterDownloaded = 614 };
    }

    internal static async Task<object> LiveAsync(string directory)
    {
        var regression = ClashVerification.SelfTest();
        var store = new ClashStore();
        using (var history = new DailyHistory(directory))
        using (var api = await ClashApi.ConnectAsync(CancellationToken.None))
        {
            store.Sampled += history.Apply;
            for (int i = 0; i < 8; i++) { store.Apply(await api.ReadAsync(CancellationToken.None)); await Task.Delay(1000); }
            Check(history.LastSaved != null, "Background autosave timer did not save");
        }
        using var reopened = new DailyHistory(directory, automatic: false);
        var day = reopened.ReadDay(store.Current.Captured);
        long up = day.Programs.Values.Sum(p => p.Routes.Uploaded), down = day.Programs.Values.Sum(p => p.Routes.Downloaded);
        Check(up == store.Current.Connections.Sum(c => c.Uploaded) && down == store.Current.Connections.Sum(c => c.Downloaded), "Reopened live history does not reconcile");
        DailyHistory.Export(Path.Combine(directory, "live-daily.csv"), day, day.Programs.Values);
        return new { Passed = true, Regression = regression, store.Current.Frames, Day = day.Day, Programs = day.Programs.Count,
            Uploaded = up, Downloaded = down, ReloadReconciles = true, Directory = directory, Data = day };
    }
}
