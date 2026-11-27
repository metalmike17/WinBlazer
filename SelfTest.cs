using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCOptimizer.Core;
using PCOptimizer.Modules;
using PCOptimizer.Services;

namespace PCOptimizer;

/// <summary>
/// Run with: PCOptimizer.exe --selftest OUTPUT_FOLDER
/// Uses throwaway settings and files. Writes selftest.log and page images.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var log = new List<string>();
        var failures = 0;
        void Check(string name, bool ok)
        {
            log.Add($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (!ok) failures++;
        }
        var work = Directory.CreateTempSubdirectory("pcopt-test-").FullName;
        Services.ActivityLog.Configure(work);   // the real History is never written by the self-test
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var path = Path.Combine(work, "settings.json");
            var first = new SettingsService(path);
            first.Current.Cleanup.MinimumAgeDays = 14;
            first.Current.HiddenModules.Add("disk-cleaner");
            first.Current.Schedules["disk-cleaner"] = new ModuleSchedule { Frequency = ScanFrequency.Weekly };
            first.Save();
            var second = new SettingsService(path);
            Check("settings round trip", second.Current.Cleanup.MinimumAgeDays == 14 &&
                  second.Current.HiddenModules.Contains("disk-cleaner") &&
                  second.Current.Schedules["disk-cleaner"].Frequency == ScanFrequency.Weekly);

            File.WriteAllText(path, "{ not json");
            var broken = new SettingsService(path);
            Check("damaged settings fall back to defaults and are kept",
                  broken.LoadWarning != null && File.Exists(path + ".bad"));
            File.WriteAllText(path, "{\"Cleanup\":{\"MinimumAgeDays\":-5},\"HiddenModules\":null}");
            Check("bad values are repaired", new SettingsService(path).Current.Cleanup.MinimumAgeDays == 1);

            var now = DateTime.UtcNow;
            Check("schedule rules",
                !ScanScheduler.IsDue(new ModuleSchedule(), now) &&
                ScanScheduler.IsDue(new ModuleSchedule { Frequency = ScanFrequency.Daily }, now) &&
                !ScanScheduler.IsDue(new ModuleSchedule { Frequency = ScanFrequency.Weekly, LastRunUtc = now.AddDays(-6) }, now) &&
                ScanScheduler.IsDue(new ModuleSchedule { Frequency = ScanFrequency.Weekly, LastRunUtc = now.AddDays(-8) }, now));

            await TestCleanerAsync(work, Check);
            BrowserTests.Run(work, Check);
            SecurityTests.Run(work, Check);
            RegistryTests.Run(work, Check);
            DeepCleanTests.Run(work, Check);
            MemoryTests.Run(Check, log);
            StartupTests.Run(work, Check, log);
            await GameModeTests.RunAsync(work, Check, log);
            TweakTests.Run(work, Check, log);
            await RepairTests.RunAsync(work, Check, log);
            ShortcutTests.Run(work, Check, log);
            DriverTests.Run(work, Check);
            BloatwareTests.Run(work, Check);
            await NetworkTests.RunAsync(work, Check);
            MonitorTests.Run(work, Check);
            await QolTests.RunAsync(work, new ModuleRegistry(typeof(App).Assembly), Check);
            Services.ActivityLog.Configure(work);

            var settings = new SettingsService(Path.Combine(work, "ui.json"));
            var services = new AppContextServices(settings, new ModuleRegistry(typeof(App).Assembly));
            log.Add("Modules: " + string.Join(", ", services.Modules.All.Select(m => m.Id)));
            Check("seventeen modules discovered", services.Modules.All.Count == 17);

            settings.Current.Schedules["disk-cleaner"] = new ModuleSchedule { Frequency = ScanFrequency.Daily };
            var scheduler = new ScanScheduler(services);
            string? message = null;
            scheduler.ScanFinished += (_, m) => message = m;
            await scheduler.RunDueAsync(now);   // read-only scan of the real temp folder
            Check("scheduled scan runs and records its time",
                  message != null && settings.Current.Schedules["disk-cleaner"].LastRunUtc == now);
            log.Add("Scheduled scan said: " + message);

            var window = new MainWindow(services, enableBackgroundFeatures: false)
            {
                ShowInTaskbar = false, Left = -4000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            await Task.Delay(9000);
            var dashboard = (DashboardView)window.CurrentView;
            Check("dashboard collects samples", dashboard.SampleCount >= 3);
            log.Add("Latest reading: " + dashboard.Latest);
            for (var i = 0; i < 400 && dashboard.Score.Result == null; i++) await Task.Delay(250);
            var score = dashboard.Score.Result;
            Check("the dashboard shows a live optimization score", score?.Score is >= 0 and <= 100 && score.Areas.Count == 5);
            log.Add($"Live score: {score?.Score}");
            foreach (var a in score?.Areas ?? new()) log.Add($"  score {a.Title}: {a.Percent}% - {a.Detail}");
            await Task.Delay(700);
            Render(window, Path.Combine(output, "dashboard.png"));

            window.Navigate("disk-cleaner");
            var disk = (DiskCleanerView)window.CurrentView;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await disk.Cleaner.ScanAsync();   // real folders, read-only
            log.Add($"Real cleanup scan took {clock.Elapsed.TotalSeconds:N1}s");
            Check("all 13 cards are scanned", disk.Cleaner.CardCount == 13 && disk.Cleaner.ScannedCount == 13);
            Check("nothing is pre-ticked", disk.Cleaner.TickedCount == 0);
            foreach (var category in CleanupCatalog.Create())
            {
                var found = CleanupEngine.Scan(category, 7);
                log.Add($"  {category.Name}: {CleanupEngine.Size(found.Bytes)}, {found.Items} items" +
                        (found.Incomplete ? " (incomplete)" : "") + (found.Error is null ? "" : " " + found.Error));
            }
            await Task.Delay(600);
            Render(window, Path.Combine(output, "disk-cleaner.png"));

            disk.ShowAnalyzer();
            await Task.Delay(300);
            clock.Restart();
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            await disk.Analyzer.AnalyzeAsync(profile);
            log.Add($"Analyzer scanned the user folder in {clock.Elapsed.TotalSeconds:N1}s, " +
                    CleanupEngine.Size(disk.Analyzer.Map.Node?.Size ?? 0));
            Check("analyzer shows folders", disk.Analyzer.Map.Node != null && disk.Analyzer.ListCount > 0);
            await Task.Delay(600);
            Render(window, Path.Combine(output, "disk-analyzer.png"));

            window.Navigate("browser-cleaner");   // looks only; nothing real is cleared
            var browsers = (BrowserCleanerView)window.CurrentView;
            await Task.Delay(500);
            Check("20 browser switches, all off", browsers.BrowserSwitchCount == 20 && browsers.SelectedCount == 0);
            Render(window, Path.Combine(output, "browser-cleaner.png"));
            browsers.SetEraserMode(true);
            await Task.Delay(500);
            Check("tracks eraser starts with everything off", browsers.EraserMode && browsers.SelectedCount == 0);
            Render(window, Path.Combine(output, "tracks-eraser.png"));
            foreach (var b in BrowserCleaner.Detect())
                log.Add($"  Detected {b.Name}: {b.Profiles.Count} profile(s)");

            window.Navigate("registry-cleaner");   // read-only scan of the real registry
            var registry = (RegistryCleanerView)window.CurrentView;
            await registry.ScanAsync();
            Check("registry page: six groups, nothing ticked", registry.GroupCount == 6 && registry.TickedCount == 0);
            var realScanner = new Services.RegistryTools.RegistryScanner(new Services.RegistryTools.RegistryStore());
            var realIssues = realScanner.Scan();
            log.Add($"Real registry: {realIssues.Count} entries, {realScanner.Unreadable} unreadable locations");
            foreach (var group in realIssues.GroupBy(i => (i.Kind, i.Risk)))
            {
                log.Add($"  {group.Key.Kind} / {group.Key.Risk}: {group.Count()}");
                foreach (var issue in group.Take(4)) log.Add($"      {issue.Title} [{issue.Hive}] - {issue.Reason}");
            }
            await Task.Delay(600);
            Render(window, Path.Combine(output, "registry-cleaner.png"));

            // Made-up entries, only to check how the tree looks. Nothing is cleaned.
            Services.RegistryTools.RegistryIssue Sample(Services.RegistryTools.IssueKind kind,
                Services.RegistryTools.Risk risk, string title) => new(kind, risk, title, "Sample",
                Services.RegistryTools.Hive.CurrentUser, @"SOFTWARE\Sample\" + title, null);
            registry.Populate(new[]
            {
                Sample(Services.RegistryTools.IssueKind.AppPath, Services.RegistryTools.Risk.Safe, "sample-app.exe"),
                Sample(Services.RegistryTools.IssueKind.OrphanedUninstall, Services.RegistryTools.Risk.Moderate, "Sample Orphan"),
                Sample(Services.RegistryTools.IssueKind.InvalidUninstall, Services.RegistryTools.Risk.Risky, "Sample Invalid")
            });
            await Task.Delay(500);
            Check("tree shows sample entries unticked", registry.ItemCount == 3 && registry.TickedCount == 0);
            Render(window, Path.Combine(output, "registry-sample.png"));

            window.Navigate("temp-cache-cleaner");
            await Task.Delay(1500);
            Check("temp and cache page opens idle",
                  window.CurrentView is TempCacheCleanerView { Busy: false });
            Render(window, Path.Combine(output, "temp-cache.png"));
            foreach (var item in CleanRunner.Plan(CleanRunner.Categories(CleanMode.Deep), 7,
                                                  CleanupEngine.IsAdministrator()))   // read-only
                log.Add($"  Deep plan: {item.Scan.Category.Name}: {CleanupEngine.Size(item.Scan.Bytes)}" +
                        (item.Scan.Incomplete ? " (incomplete)" : "") + (item.Blocked is null ? "" : " - " + item.Blocked));

            window.Navigate("memory-optimizer");
            var memoryView = (MemoryOptimizerView)window.CurrentView;
            await Task.Delay(9000);
            Check("memory chart collects samples", memoryView.SampleCount >= 5);
            Check("only Empty Working Sets is enabled without rights",
                  CleanupEngine.IsAdministrator() || memoryView.ActionButtonsEnabled == 1);
            Check("limiter lists apps", memoryView.AppCount > 0);
            Render(window, Path.Combine(output, "memory.png"));

            window.Navigate("startup-manager");   // reads the real entries; changes nothing
            var startup = (StartupManagerView)window.CurrentView;
            for (var i = 0; i < 100 && startup.EntryCount == 0; i++) await Task.Delay(200);
            Check("startup page lists real entries", startup.EntryCount > 0 && startup.ShownCount == startup.EntryCount);
            log.Add(startup.ImpactSource);
            foreach (var e in startup.Entries)
                log.Add($"  [{(e.Enabled ? "on " : "off")}] {e.Source}: {e.Name} | {e.Publisher} | " +
                        $"{(e.LockReason is null ? "changeable" : "locked")}{(e.Hidden ? " hidden" : "")} | {e.Impact}");
            await Task.Delay(800);
            Render(window, Path.Combine(output, "startup.png"));

            window.Navigate("game-mode");   // never activated here
            var gameView = (GameModeView)window.CurrentView;
            await Task.Delay(5000);
            Check("game mode page is idle and lists games or a note",
                  gameView.StateText.StartsWith("Game Mode is off") && gameView.ListedGames > 0);
            Render(window, Path.Combine(output, "gamemode.png"));

            window.Navigate("system-tweaks");   // reads real settings; changes nothing
            var tweakView = (SystemTweaksView)window.CurrentView;
            for (var i = 0; i < 100 && tweakView.LoadedCount == 0; i++) await Task.Delay(200);
            await Task.Delay(800);
            Check("tweaks page lists every tweak", tweakView.LoadedCount >= 50 && tweakView.ShownCount == tweakView.LoadedCount);
            Render(window, Path.Combine(output, "tweaks.png"));
            tweakView.Show(PCOptimizer.Services.Tweaks.TweakCategory.Network);
            await Task.Delay(800);
            Check("category filter narrows the list", tweakView.ShownCount is > 0 and < 20);
            Render(window, Path.Combine(output, "tweaks-network.png"));

            window.Navigate("disk-repair");   // no tool is started
            var repairView = (DiskRepairView)window.CurrentView;
            await Task.Delay(1500);
            Check("repair tools list drives", repairView.Tools.DriveCount > 0);
            Check("repair tools are disabled without rights",
                  CleanupEngine.IsAdministrator() || repairView.Tools.ToolsEnabled == 0);
            Render(window, Path.Combine(output, "repair.png"));
            repairView.ShowHealth();
            for (var i = 0; i < 100 && !repairView.Health.HasLoaded; i++) await Task.Delay(200);
            await Task.Delay(800);
            Check("drive health page shows drives", repairView.Health.DriveCount > 0);
            Render(window, Path.Combine(output, "repair-health.png"));

            window.Navigate("shortcut-fixer");   // scans real shortcuts; changes nothing
            var shortcutView = (ShortcutFixerView)window.CurrentView;
            for (var i = 0; i < 300 && !shortcutView.HasScanned; i++) await Task.Delay(200);
            await Task.Delay(800);
            Check("shortcut page scans and lists results", shortcutView.HasScanned && shortcutView.ShownCount > 0);
            Render(window, Path.Combine(output, "shortcuts.png"));

            window.Navigate("driver-manager");   // reads drivers and checks for updates; installs nothing
            var driverView = (DriverManagerView)window.CurrentView;
            for (var i = 0; i < 900 && !driverView.HasLoaded; i++) await Task.Delay(200);
            Check("driver page lists installed drivers", driverView.DriverCount > 20 && driverView.ShownCount > 20);
            await driverView.CheckForTestAsync();   // live and read-only; results are logged, not asserted
            log.Add("Driver sources: " + driverView.SourcesText);
            foreach (var d in driverView.Items.Where(d => d.Health != PCOptimizer.Services.DriverTools.DriverHealth.Working
                         || d.WindowsUpdate != null || d.Vendor != null))
                log.Add($"  # {d.Health}: {d.Name} x{d.Count} v{d.Version} {d.Date:yyyy-MM-dd} code {d.ProblemCode} " +
                        $"wu={d.WindowsUpdate?.Title} notNewer={d.OfferIsNotNewer} vendor={d.Vendor?.Version}");
            await Task.Delay(800);
            Render(window, Path.Combine(output, "drivers.png"));

            window.Navigate("bloatware-remover");   // lists apps only; removes and installs nothing
            var bloatView = (BloatwareView)window.CurrentView;
            for (var i = 0; i < 600 && !bloatView.HasLoaded; i++) await Task.Delay(200);
            Check("bloatware page loads the live app list", bloatView.HasLoaded && bloatView.Entries.Count > 30);
            log.Add("Bloatware status: " + bloatView.StatusText);
            foreach (var e in bloatView.Entries.Where(e => e.Installed))
                log.Add($"  * installed: {e.App.Title} app={e.AppBytes} data={e.DataBytes}");
            await Task.Delay(600);
            Render(window, Path.Combine(output, "bloatware.png"));
            bloatView.ShowAbsentForTest();
            await Task.Delay(400);
            Render(window, Path.Combine(output, "bloatware-all.png"));

            window.Navigate("network-tools");   // live ping, DNS lookups, TCP read and a small download; changes nothing
            var netView = (NetworkToolsView)window.CurrentView;
            for (var i = 0; i < 100 && netView.Adapters.Count == 0; i++) await Task.Delay(100);   // read in the background
            Check("network page shows the current adapter", netView.Adapters.Count > 0 && netView.Adapters[0].Gateways.Count > 0);
            await netView.PingAsync(8);
            log.Add($"Live ping: n={netView.Stats.Samples.Count} avg={netView.Stats.Average} jitter={netView.Stats.Jitter} loss={netView.Stats.LossPercent}");
            await Task.Delay(500);
            Render(window, Path.Combine(output, "network-ping.png"));
            await netView.OpenTabAsync("DNS");
            await netView.RunDnsTestAsync(1);
            foreach (var s in netView.Scores) log.Add($"  dns {s.Name}: {s.MedianMs:0.0} ms {s.Answered}/{s.Sent}");
            await Task.Delay(500);
            Render(window, Path.Combine(output, "network-dns.png"));
            await netView.OpenTabAsync("TCP/IP");
            await Task.Delay(500);
            log.Add("TCP rows: " + netView.TcpRowCount);
            Check("live TCP/IP settings are read", netView.TcpRowCount > 3);
            Render(window, Path.Combine(output, "network-tcp.png"));
            await netView.OpenTabAsync("Speed");
            await netView.RunSpeedAsync(TimeSpan.FromSeconds(6), 40_000_000);
            log.Add("Live speed: " + netView.SpeedText.Replace('\n', ' '));
            await Task.Delay(500);
            Render(window, Path.Combine(output, "network-speed.png"));

            window.Navigate("system-monitor");   // read only
            var monitorView = (SystemMonitorView)window.CurrentView;
            for (var i = 0; i < 200 && monitorView.SampleCount < 12; i++) await Task.Delay(250);
            var hw = monitorView.Latest!;
            log.Add($"Monitor: samples={monitorView.SampleCount} cores={hw.Cores.Length} gpu={hw.Gpu} disks={hw.Disks.Count} down={hw.Download} up={hw.Upload}");
            Check("live monitor reads cores, memory, disk and network", hw.Cores.Length == Environment.ProcessorCount &&
                  monitorView.CoreCharts == hw.Cores.Length && hw.MemoryUsed > 0 && hw.Disks.Count > 0 && hw.Download != null);
            Render(window, Path.Combine(output, "monitor.png"));
            monitorView.ScrollToEnd();
            await Task.Delay(1500);
            Render(window, Path.Combine(output, "monitor-lower.png"));
            var cpuBefore = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            await Task.Delay(5000);
            log.Add($"Monitor page CPU cost: {(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalSeconds / 5 / Environment.ProcessorCount:P1} of total");
            monitorView.OpenProcessManager();
            await Task.Delay(2500);
            log.Add($"Process manager: groups={monitorView.Groups.Count} shown={monitorView.ShownGroups} locked={monitorView.Groups.Count(g => g.LockReason != null)}");
            foreach (var g in monitorView.Groups.OrderByDescending(g => g.Memory).Take(12))
                log.Add($"  grp {g.Title} x{g.Processes.Count} {g.Memory / 1048576} MB {(g.LockReason ?? "can end")}");
            Check("process manager lists app groups", monitorView.ShownGroups > 20);
            Render(window, Path.Combine(output, "processes.png"));
            window.Navigate("dashboard");
            await Task.Delay(1500);
            var stoppedAt = monitorView.SampleCount;
            await Task.Delay(2500);
            Check("monitoring stops when the page is left", monitorView.SampleCount == stoppedAt);

            window.Navigate("settings");
            window.Search.Open();
            window.Search.Query = "hibernat";
            await Task.Delay(600);
            Check("the search box shows results", window.Search.IsOpen && window.Search.ResultCount > 0);
            Render(window, Path.Combine(output, "search.png"));
            log.Add("Search selected: " + window.Search.Selected);
            window.Search.Choose();
            var tweaksView = (SystemTweaksView)window.CurrentView;
            for (var i = 0; i < 200 && tweaksView.FocusedTweak == null; i++) await Task.Delay(100);
            log.Add($"After choose: page={window.CurrentModuleId} focused={tweaksView.FocusedTweak} loaded={tweaksView.LoadedCount} shown={tweaksView.ShownCount} status={tweaksView.StatusText}");
            Check("choosing a result jumps to that item", !window.Search.IsOpen && window.CurrentModuleId == "system-tweaks" &&
                  tweaksView.FocusedTweak != null);
            window.Navigate("system-monitor", "processes");
            await Task.Delay(300);
            Check("search can open a sub-page", window.CurrentModuleId == "system-monitor");

            Services.ActivityLog.Record(Services.ActivityLog.Cleanup, "Sample cleanup (self-test)", true, 734003200, "Test entry.");
            Services.ActivityLog.Record("Network", "Sample network change (self-test)", false, null, "Test entry.");
            window.Navigate("history");
            await Task.Delay(1000);
            var historyView = (HistoryView)window.CurrentView;
            Check("history page lists actions and totals", historyView.ShownCount >= 2 && historyView.SummaryText.Contains("MB"));
            Check("a toast appears when cleanup finishes", window.Toast.IsShowing && window.Toast.Text.Contains("freed 700.0 MB"));
            Render(window, Path.Combine(output, "history.png"));

            window.Navigate("settings");
            await Task.Delay(600);
            Check("opens settings", window.CurrentModuleId == "settings");
            Render(window, Path.Combine(output, "settings.png"));
            var count = dashboard.SampleCount;
            await Task.Delay(2500);
            Check("dashboard stops sampling when hidden", dashboard.SampleCount <= count + 1);

            await PolishTests.RunAsync(window, services, work, output, Check, log, Render);

            settings.Current.HiddenModules.Add("disk-cleaner");
            settings.Save();
            Check("hidden module leaves the sidebar", !window.SidebarIds.Contains("disk-cleaner"));
            settings.Current.HiddenModules.Remove("disk-cleaner");
            settings.Save();
            Check("shown module returns to the sidebar", window.SidebarIds.Contains("disk-cleaner"));
            window.Close();

            // Create and remove the tray icon once.
            using (new TrayIcon(() => { }, () => { })) await Task.Delay(300);
            Check("tray icon can be created", true);

            SecurityTests.RunRedirection(work, Check);   // last: it cannot be undone
        }
        catch (Exception ex) { Check("unexpected error: " + ex, false); }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (IOException) { }
        }
        log.Add($"Failures: {failures}");
        File.WriteAllLines(Path.Combine(output, "selftest.log"), log);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Deletion rules, tested only on files created here.</summary>
    private static async Task TestCleanerAsync(string work, Action<string, bool> check)
    {
        var root = Directory.CreateDirectory(Path.Combine(work, "clean", "root")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(work, "clean", "outside")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        string Make(string folder, string name, bool old)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "data");
            if (old)
            {
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-30));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            }
            return path;
        }
        async Task Junction(string link, string target)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
                { CreateNoWindow = true, UseShellExecute = false })!;
            await p.WaitForExitAsync();
        }

        var old = Make(Path.Combine(root, "sub"), "old.tmp", true);
        var fresh = Make(root, "new.tmp", false);
        var precious = Make(outside, "precious.tmp", true);
        await Junction(Path.Combine(root, "link"), outside);
        check("test junction was created", File.Exists(Path.Combine(root, "link", "precious.tmp")));

        var aged = new CleanCategory("t", "Test", "", new[] { new CleanTarget(root) }) { UsesMinimumAge = true };
        var scan = CleanupEngine.Scan(aged, 7);
        check("scan finds only old files and ignores the junction", scan.Files.Count == 1 && scan.Files[0].Path == old);

        var linkRoot = aged with { Targets = new[] { new CleanTarget(Path.Combine(root, "link")) } };
        check("a linked root folder is not scanned", CleanupEngine.Scan(linkRoot, 7).Files.Count == 0);

        CleanupEngine.Clean(scan, 7, false);
        check("clean deletes only what was scanned", !File.Exists(old) && File.Exists(fresh) && File.Exists(precious));

        // Swap a scanned folder for a junction after the scan: the outside file must survive.
        var swap = Directory.CreateDirectory(Path.Combine(root, "swap")).FullName;
        Make(swap, "precious.tmp", true);
        var before = CleanupEngine.Scan(aged, 7);
        Directory.Delete(swap, true);
        await Junction(swap, outside);
        var swapped = CleanupEngine.Clean(before, 7, false);
        check("folder swapped for a link is refused", File.Exists(precious) && swapped.Deleted == 0 && swapped.Skipped == 1);

        var browser = new CleanCategory("b", "Browser", "", new[] { new CleanTarget(root) })
            { BlockingProcesses = new[] { "fakebrowser" } };
        var open = CleanupEngine.Clean(CleanupEngine.Scan(browser, 7), 7, false, _ => true);
        check("running browser blocks cleaning", File.Exists(fresh) && open.Deleted == 0);
        var admin = browser with { RequiresAdmin = true };
        CleanupEngine.Clean(CleanupEngine.Scan(admin, 7), 7, false, _ => false);
        check("admin category is refused without rights", File.Exists(fresh));
        var report = browser with { Kind = CategoryKind.ReportOnly, BlockingProcesses = Array.Empty<string>() };
        CleanupEngine.Clean(CleanupEngine.Scan(report, 7), 7, true, _ => false);
        check("report-only category is never deleted", File.Exists(fresh));

        var keep = Make(root, "keep.txt", false);
        var patterned = new CleanCategory("p", "Pattern", "", new[] { new CleanTarget(root, "*.tmp", false) });
        CleanupEngine.Clean(CleanupEngine.Scan(patterned, 7), 7, false);
        check("only matching files are deleted", !File.Exists(fresh) && File.Exists(keep) && File.Exists(precious));
        check("paths outside the root are refused", !CleanupEngine.IsSafePath(precious, root));
        check("unsafe %TEMP% values are rejected",
              !CleanupCatalog.IsPlausibleTemp(@"C:\") && !CleanupCatalog.IsPlausibleTemp(@"C:\Temp") &&
              !CleanupCatalog.IsPlausibleTemp(@"C:\Users\someone") &&
              CleanupCatalog.IsPlausibleTemp(@"C:\Users\someone\Temp"));
        // Remove junctions themselves so later cleanup cannot go through them.
        Directory.Delete(swap);
        Directory.Delete(Path.Combine(root, "link"));

        var tree = Directory.CreateDirectory(Path.Combine(work, "tree", "big")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(tree, "big", "a.bin"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(tree, "b.bin"), new byte[1000]);
        var result = DiskTree.Scan(tree, null, CancellationToken.None);
        check("folder sizes add up", result.Root.Size == 4000 && result.Root.Children[0].Name == "big" &&
                                     result.Root.Children[0].Size == 3000);

        var bounds = new Rect(0, 0, 600, 400);
        var sizes = new double[] { 6, 6, 4, 3, 2, 2, 1, 0 };
        var boxes = Controls.Treemap.Layout(sizes, s => s, bounds);
        var proportional = boxes.All(b => Math.Abs(b.Area.Width * b.Area.Height - b.Item / 24 * 240000) < 1);
        var inside = boxes.All(b => b.Area.Left >= -0.01 && b.Area.Top >= -0.01 &&
                                    b.Area.Right <= 600.01 && b.Area.Bottom <= 400.01);
        var overlap = boxes.SelectMany((a, i) => boxes.Skip(i + 1).Select(b => Rect.Intersect(a.Area, b.Area)))
            .Any(x => !x.IsEmpty && x.Width > 0.01 && x.Height > 0.01);
        check("treemap areas are proportional, in bounds, without overlap",
              boxes.Count == 7 && proportional && inside && !overlap);
    }

    private static void Render(Window window, string file)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();   // a window's own background is not part of its content
        using (var dc = background.RenderOpen())
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
