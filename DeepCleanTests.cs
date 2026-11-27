using System.Xml.Linq;
using PCOptimizer.Services;

namespace PCOptimizer;

/// <summary>Cleaning tests use only files created here. No real cleanup is run.</summary>
public static class DeepCleanTests
{
    public static void Run(string work, Action<string, bool> check)
    {
        var root = Directory.CreateDirectory(Path.Combine(work, "deep")).FullName;
        void Fill() { for (var i = 1; i <= 5; i++) File.WriteAllBytes(Path.Combine(root, $"f{i}.tmp"), new byte[i * 100]); }
        var category = new CleanCategory("x", "Fixture", "", new[] { new CleanTarget(root) });
        var needsAdmin = category with { Id = "y", Name = "Admin fixture", RequiresAdmin = true };

        Fill();
        var plan = CleanRunner.Plan(new[] { needsAdmin }, 7, false, _ => false);
        var state = new CleanProgressState();
        CleanRunner.Run(plan, 7, false, state, default, _ => false);
        check("run skips admin items without rights",
              !plan[0].WillRun && Directory.GetFiles(root).Length == 5 && state.Freed == 0);

        plan = CleanRunner.Plan(new[] { category }, 7, false, _ => false);
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        CleanRunner.Run(plan, 7, false, new CleanProgressState(), cancelled.Token, _ => false);
        check("cancel before cleaning deletes nothing", Directory.GetFiles(root).Length == 5);

        var midway = new CancellationTokenSource();
        var count = 0;
        var partial = CleanupEngine.Clean(plan[0].Scan, 7, false, _ => false,
            (_, _) => { if (++count == 2) midway.Cancel(); }, midway.Token);
        check("cancel stops partway", partial.Deleted == 2 && Directory.GetFiles(root).Length == 3);

        Fill();
        File.WriteAllBytes(Path.Combine(root, "locked.tmp"), new byte[50]);
        plan = CleanRunner.Plan(new[] { category }, 7, false, _ => false);
        state = new CleanProgressState();
        using (new FileStream(Path.Combine(root, "locked.tmp"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var (results, _) = CleanRunner.Run(plan, 7, false, state, default, _ => false);
            check("bytes freed counts only deleted files; progress still completes",
                  state.Freed == 1500 && state.Done == state.Total && state.Total == 1550 &&
                  results[0].Skipped == 1);
        }

        var ran = false;
        var tool = category with { Id = "z", Kind = CategoryKind.WindowsTool, Tool = () => { ran = true; return null; } };
        CleanupEngine.Clean(CleanupEngine.Scan(tool, 7), 7, false, _ => false);
        check("Windows tool category deletes no files itself", ran && File.Exists(Path.Combine(root, "locked.tmp")));

        var quick = CleanRunner.Categories(CleanMode.Quick).Select(c => c.Id).ToList();
        var deep = CleanRunner.Categories(CleanMode.Deep);
        check("Quick Clean is only user temp and Recycle Bin", quick.SequenceEqual(new[] { "user-temp", "recycle-bin" }));
        check("Deep Clean excludes Recycle Bin, prefetch and Windows.old",
              deep.Count == 14 && !deep.Any(c => c.Id is "recycle-bin" or "prefetch" or "windows-old"));
        check("scheduled Quick Clean leaves the Recycle Bin unless chosen",
              CleanRunner.Categories(CleanMode.Quick, false).Select(c => c.Id).SequenceEqual(new[] { "user-temp" }));

        check("only exact automatic cleaning arguments are accepted",
              AutoCleanArguments.TryParse(new[] { "--auto-clean", "deep" }, out var m, out var r) && m == CleanMode.Deep && !r &&
              AutoCleanArguments.TryParse(new[] { "--auto-clean", "quick", "--include-recycle-bin" }, out _, out r) && r &&
              !AutoCleanArguments.TryParse(new[] { "--auto-clean", "none" }, out _, out _) &&
              !AutoCleanArguments.TryParse(new[] { "--auto-clean", "quick", "--other" }, out _, out _) &&
              !AutoCleanArguments.TryParse(new[] { "--auto-clean" }, out _, out _));

        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var descriptions = new List<string>();
        var limited = true;
        foreach (var frequency in Enum.GetValues<AutoFrequency>())
        {
            var schedule = new AutoSchedule(frequency, new TimeOnly(3, 30), DayOfWeek.Friday, 31, CleanMode.Deep, true);
            var xml = TaskSchedulerService.BuildXml(schedule, @"C:\A & B\app.exe", "S-1-5-21-1", new DateTime(2099, 1, 1));
            descriptions.Add(TaskSchedulerService.Describe(xml));
            limited &= xml.Descendants(ns + "RunLevel").Single().Value == "LeastPrivilege" &&
                       xml.Descendants(ns + "Arguments").Single().Value == "--auto-clean deep";
        }
        check("task definitions never ask for elevation", limited);
        check("schedules are described correctly", descriptions.SequenceEqual(new[]
        {
            "Daily at 03:30: Deep Clean.", "Weekly on Friday at 03:30: Deep Clean.",
            "Monthly on day 28 at 03:30: Deep Clean."
        }));

        // Registers real throwaway tasks dated 2099 whose argument cleans nothing, then removes them.
        var name = "PCOptimizer SelfTest " + Guid.NewGuid().ToString("N");
        var harmless = Path.Combine(Environment.SystemDirectory, "where.exe");
        var service = new TaskSchedulerService(name, harmless);
        try
        {
            var ok = true;
            foreach (var frequency in Enum.GetValues<AutoFrequency>())
            {
                service.Save(new AutoSchedule(frequency, new TimeOnly(4, 15), DayOfWeek.Monday, 5, CleanMode.Quick, false),
                             new DateTime(2099, 1, 1));
                var saved = service.Read();
                ok &= saved != null && TaskSchedulerService.Describe(saved).StartsWith(frequency.ToString()) &&
                      !saved.Descendants(ns + "RunLevel").Any(x => x.Value == "HighestAvailable") &&
                      !service.PointsElsewhere(saved);
            }
            check("Task Scheduler accepts daily, weekly and monthly tasks", ok);
        }
        finally { service.Delete(); }
        check("test task is removed", service.Read() == null);
    }
}
