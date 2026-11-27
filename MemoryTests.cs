using System.Diagnostics;
using System.Runtime.InteropServices;
using PCOptimizer.Services.MemoryTools;

namespace PCOptimizer;

/// <summary>Process changes are made only to a helper process started by this test.</summary>
public static class MemoryTests
{
    [DllImport("kernel32.dll")]
    private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref Throttling info, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Throttling { public uint Version, ControlMask, StateMask; }

    private static bool? Efficiency(Process p)
    {
        var state = new Throttling { Version = 1 };
        if (!GetProcessInformation(p.Handle, 4, ref state, Marshal.SizeOf<Throttling>())) return null;
        return (state.ControlMask & 1) != 0 && (state.StateMask & 1) != 0;
    }

    /// <summary>The path-matching route the app uses, against a temporary copy of this program.</summary>
    private static void TestApplyByPath(Action<string, bool> check)
    {
        var folder = Directory.CreateTempSubdirectory("pcopt-limit-").FullName;
        try
        {
            var source = AppContext.BaseDirectory;
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(folder, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var program = Path.Combine(folder, Path.GetFileName(Environment.ProcessPath!));
            using var other = Process.Start(new ProcessStartInfo(program, "--selftest-sleep") { UseShellExecute = false })!;
            try
            {
                Thread.Sleep(1500);
                bool IsIdle() { other.Refresh(); return other.PriorityClass == ProcessPriorityClass.Idle; }
                check("chosen app appears in the limiter list", AppLimiter.ListApps()
                    .Any(a => a.Path.Equals(program, StringComparison.OrdinalIgnoreCase)));

                var limiter = new AppLimiter();
                var failed = limiter.Apply(new[] { program });
                limiter.Apply(new[] { program });   // repeating must not overwrite the saved original
                check("apply by path limits the chosen app", failed == 0 && limiter.LimitedCount == 1 && IsIdle());
                limiter.Apply(Array.Empty<string>());
                check("unticking restores the app", limiter.LimitedCount == 0 && !IsIdle() &&
                      other.PriorityClass == ProcessPriorityClass.Normal && Efficiency(other) != true);
                limiter.Apply(new[] { program });
                limiter.RestoreAll();
                check("exit restores all apps", !IsIdle() && limiter.LimitedCount == 0);
            }
            finally { other.Kill(); other.WaitForExit(5000); }
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Run(Action<string, bool> check, List<string> log)
    {
        using (var reader = new MemoryReader())
        {
            var m = reader.Read();
            log.Add("Memory: " + m);
            check("memory lists are read and add up", m != null && m.InUse > 0 && m.Standby >= 0 &&
                  m.InUse + m.Modified + m.Standby + m.Free == m.Total && m.Available <= m.Total);
        }

        foreach (var action in Enum.GetValues<MemoryAction>().Where(MemoryActions.NeedsAdmin))
            check($"{action} is refused without rights", !MemoryActions.Run(action, false).Ok);

        var sample = new MemorySnapshot(DateTime.UtcNow, 16L << 30, 8L << 30, 0, 4L << 30, 500L << 20);
        var threshold = 1024L << 20;
        check("smart cleaner rules",
            SmartCleanerRules.ShouldPurge(sample, threshold, null) &&
            !SmartCleanerRules.ShouldPurge(sample with { Free = threshold }, threshold, null) &&
            !SmartCleanerRules.ShouldPurge(sample with { Standby = 100L << 20 }, threshold, null) &&
            !SmartCleanerRules.ShouldPurge(sample, threshold, sample.TimeUtc.AddSeconds(-30)) &&
            SmartCleanerRules.ShouldPurge(sample, threshold, sample.TimeUtc.AddSeconds(-61)));

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        check("Windows components cannot be limited",
              AppLimiter.IsProtected(Path.Combine(windows, "explorer.exe")) &&
              AppLimiter.IsProtected(Path.Combine(windows, "System32", "svchost.exe")) &&
              AppLimiter.IsProtected(Environment.ProcessPath!) &&
              !AppLimiter.IsProtected(@"C:\Program Files\Example\app.exe"));
        check("app list contains no protected apps", AppLimiter.ListApps().All(a => !AppLimiter.IsProtected(a.Path)));

        TestApplyByPath(check);

        using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--selftest-sleep")
            { UseShellExecute = false })!;
        try
        {
            Thread.Sleep(700);
            helper.PriorityClass = ProcessPriorityClass.BelowNormal;   // a non-default original
            var limited = AppLimiter.LimitProcess(helper.Id, out var original);
            helper.Refresh();
            var low = helper.PriorityClass == ProcessPriorityClass.Idle;
            var efficient = Efficiency(helper);
            log.Add($"Efficiency mode read back: {efficient?.ToString() ?? "not readable"}");
            check("limit applies low priority and efficiency mode", limited && low && efficient != false);
            var restored = AppLimiter.RestoreProcess(helper.Id, original);
            helper.Refresh();
            check("restore returns the original priority", restored &&
                  helper.PriorityClass == ProcessPriorityClass.BelowNormal && Efficiency(helper) != true);

            AppLimiter.LimitProcess(helper.Id, out original);
            helper.PriorityClass = ProcessPriorityClass.AboveNormal;   // changed by someone else
            AppLimiter.RestoreProcess(helper.Id, original);
            helper.Refresh();
            check("restore keeps a priority changed elsewhere", helper.PriorityClass == ProcessPriorityClass.AboveNormal);
            check("working set can be trimmed for one process", MemoryActions.TryEmptyWorkingSet(helper.Id));
        }
        finally { helper.Kill(); helper.WaitForExit(5000); }
    }
}
