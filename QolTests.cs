using PCOptimizer.Core;
using PCOptimizer.Services;

namespace PCOptimizer;

/// <summary>Uses a temporary history file and test files. No real files are cleaned.</summary>
public static class QolTests
{
    public static async Task RunAsync(string work, ModuleRegistry modules, Action<string, bool> check)
    {
        var history = Directory.CreateDirectory(Path.Combine(work, "history-test")).FullName;
        ActivityLog.Configure(history);
        var seen = new List<Activity>();
        void Watch(Activity a) => seen.Add(a);
        ActivityLog.Added += Watch;
        try
        {
            ActivityLog.Record(ActivityLog.Cleanup, "first", true, 1000);
            File.AppendAllText(Path.Combine(history, "history.jsonl"), "not json" + Environment.NewLine + "{}" + Environment.NewLine);
            ActivityLog.Record("Network", "second", false, null, new string('x', 5000));
            var read = ActivityLog.Read();
            check("history keeps timestamps and space freed, newest first, skipping damaged lines",
                  read.Count == 2 && read[0].Action == "second" && read[1].BytesFreed == 1000 &&
                  (DateTime.Now - read[1].Time).TotalMinutes < 1 && read[0].Details.Length < 2100 && seen.Count == 2);
            for (var i = 0; i < 2600; i++) ActivityLog.Record("Test", "bulk " + i, true, 1, new string('y', 600));
            var trimmed = ActivityLog.Read();
            check("a large history is trimmed and keeps the newest",
                  trimmed.Count is >= 1900 and < 2600 && trimmed[0].Action == "bulk 2599");
            ActivityLog.Clear();
            check("history can be cleared", ActivityLog.Read().Count == 0);

            var blocked = Path.Combine(work, "history-blocked");
            Directory.CreateDirectory(Path.Combine(blocked, "history.jsonl"));
            ActivityLog.Configure(blocked);
            ActivityLog.Record("Test", "cannot write", true);
            check("a history that cannot be written does not break the action", ActivityLog.LastError != null);
            ActivityLog.Configure(history);

            // Quick Clean on a test folder only.
            var junk = Directory.CreateDirectory(Path.Combine(work, "quick-junk")).FullName;
            var file = Path.Combine(junk, "old.tmp");
            File.WriteAllBytes(file, new byte[4096]);
            File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(-30));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-30));
            var category = CleanRunner.Categories(CleanMode.Quick).First(c => c.Id == "user-temp") with
            { Targets = new[] { new CleanTarget(junk) }, BlockingProcesses = Array.Empty<string>() };
            seen.Clear();
            var asked = "";
            var declined = await QuickRunners.CleanAsync(new[] { category }, 7, false, q => { asked = q; return false; }, _ => false);
            check("quick clean asks first and does nothing on No",
                  asked.Contains("permanently delete") && File.Exists(file) && declined.StartsWith("Cancelled") && seen.Count == 0);
            var done = await QuickRunners.CleanAsync(new[] { category }, 7, false, _ => true, _ => false);
            ActivityLog.Flush();
            check("quick clean deletes, reports and records space freed", !File.Exists(file) && done.StartsWith("Freed") &&
                  seen.Single() is { Category: ActivityLog.Cleanup, BytesFreed: 4096 });
        }
        finally { ActivityLog.Added -= Watch; }

        // A throwaway name, so an open copy of the app is not affected.
        var instanceName = @"Local\PCOptimizerSelfTest-" + Guid.NewGuid().ToString("N");
        using (var firstCopy = SingleInstance.TryAcquire(TimeSpan.Zero, instanceName))
        {
            using var asked = new ManualResetEventSlim();
            firstCopy?.Listen(asked.Set);
            using var secondCopy = SingleInstance.TryAcquire(TimeSpan.Zero, instanceName);
            check("only one copy can open", firstCopy != null && secondCopy == null);
            check("a second copy asks the open one to show itself",
                  SingleInstance.AskOpenCopyToShow(instanceName) && asked.Wait(TimeSpan.FromSeconds(5)));
        }
        var waiting = Task.Run(() => SingleInstance.TryAcquire(TimeSpan.FromSeconds(10), instanceName + "-restart"));
        using (var oldCopy = SingleInstance.TryAcquire(TimeSpan.Zero, instanceName + "-handover"))
        {
            (await waiting)?.Dispose();
            var restarted = Task.Run(() => SingleInstance.TryAcquire(TimeSpan.FromSeconds(10), instanceName + "-handover"));
            await Task.Delay(500);
            var waitedForOld = !restarted.IsCompleted;
            oldCopy?.Dispose();
            using var newCopy = await restarted;
            check("a restarted copy waits for the old one to close", waitedForOld && newCopy != null);
        }

        var crashFile = Path.Combine(work, "crash-test", "crash.log");
        check("a crash is saved with its details",
              CrashLog.Write("Test", new InvalidOperationException("sample failure"), crashFile) &&
              File.ReadAllText(crashFile).Contains("sample failure"));
        File.WriteAllText(crashFile, new string('x', 1_100_000));
        CrashLog.Write("Test", null, crashFile);
        check("a large crash log is rotated", File.Exists(crashFile + ".old") && new FileInfo(crashFile).Length < 10_000);
        Directory.CreateDirectory(Path.Combine(work, "crash-blocked", "crash.log"));
        check("a crash log that cannot be written does not throw",
              !CrashLog.Write("Test", null, Path.Combine(work, "crash-blocked", "crash.log")));

        var fakeProgramFiles = Directory.CreateDirectory(Path.Combine(work, "Program Files")).FullName;
        string Program(params string[] parts)
        {
            var path = Path.Combine(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            return path;
        }
        var installed = Program(fakeProgramFiles, "WinBlazer", "app.exe");
        check("only a copy inside Program Files may restart as administrator",
              InstallLocation.IsProtected(installed, fakeProgramFiles) &&
              !InstallLocation.IsProtected(Program(work, "Downloads", "app.exe"), fakeProgramFiles) &&
              !InstallLocation.IsProtected(Program(work, "Program Files Fake", "app.exe"), fakeProgramFiles) &&
              !InstallLocation.IsProtected(Path.Combine(fakeProgramFiles, "..", "Downloads", "app.exe"), fakeProgramFiles) &&
              !InstallLocation.IsProtected(Path.Combine(fakeProgramFiles, "missing", "app.exe"), fakeProgramFiles) &&
              !InstallLocation.IsProtected(null, fakeProgramFiles));
        check("the copy running this test is not treated as installed",
              !InstallLocation.IsProtected(Path.Combine(work, "app.exe")));

        const long mb = 1048576;
        var perfect = OptimizationScore.Calculate(new ScoreInputs(0, 50, 10, 10, 2, 60));
        var worst = OptimizationScore.Calculate(new ScoreInputs(9000 * mb, 2, 0, 10, 30, 1));
        check("the score runs from 0 to 100", perfect.Score == 100 && worst.Score == 0);
        var mixed = OptimizationScore.Calculate(new ScoreInputs(0, 50, 9, 10, 2, 60));
        check("the lowest areas come first and have fixes", mixed.Areas[0].Id == "tweaks" &&
              mixed.Areas[0].FixAction == "optimize" && mixed.Score is 98 &&
              mixed.Areas.Where(a => a.Id is "startup" or "disk" or "memory").All(a => a.FixAction == null));
        check("100 is not shown while anything remains",
              OptimizationScore.Calculate(new ScoreInputs(0, 50, 199, 200, 2, 60)).Score == 99);
        var partial = OptimizationScore.Calculate(new ScoreInputs(null, 50, null, null, null, 60));
        check("unreadable areas are left out of the score", partial.Score == 100 &&
              OptimizationScore.Calculate(new ScoreInputs(null, null, null, null, null, null)).Score == null);

        var index = SearchIndex.Build(modules);
        SearchEntry? Top(string query) => SearchIndex.Find(index, query).FirstOrDefault();
        check("search finds pages, sub-pages, settings and tweaks",
              Top("history")?.ModuleId == "history" && Top("process manager")?.Section == "processes" &&
              Top("tray")?.ModuleId == "settings" && Top("hibernat")?.ModuleId == "system-tweaks" &&
              Top("solitaire")?.ModuleId == "bloatware-remover" && Top("dns speed")?.Section == "DNS");
        check("every module is searchable and nonsense finds nothing",
              modules.All.All(m => SearchIndex.Find(index, m.Title).Any(e => e.ModuleId == m.Id)) &&
              SearchIndex.Find(index, "qqzzxx").Count == 0);

        var quick = modules.All.OfType<IQuickActionProvider>().Select(m => m.Id).ToHashSet();
        check("Clean, Optimize and Game Mode provide quick actions",
              quick.SetEquals(new[] { "temp-cache-cleaner", "system-tweaks", "game-mode" }));
        var clicks = 0;
        using var tray = new TrayIcon(() => { }, () => { },
            new (Func<string>, Action)[] { (() => "Clean", () => clicks++), (() => "Optimize", () => { }) });
        check("the tray menu lists the quick actions without running them",
              tray.MenuLabels.SequenceEqual(new[] { "Open WinBlazer", "Clean", "Optimize", "Exit" }) && clicks == 0);
    }
}
