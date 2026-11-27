using PCOptimizer.Services.Tweaks;

namespace PCOptimizer.Services;

/// <summary>Shared by the tray menu and the dashboard score. Both ask before changing anything.</summary>
public static class QuickRunners
{
    /// <summary>Quick Clean: old user temp files and the Recycle Bin.</summary>
    public static async Task<string> CleanAsync(IReadOnlyList<CleanCategory> categories, int minimumAgeDays,
        bool isAdmin, Func<string, bool> confirm, Func<string, bool>? isRunning = null)
    {
        var plan = await Task.Run(() => CleanRunner.Plan(categories, minimumAgeDays, isAdmin, isRunning));
        var run = plan.Where(p => p.WillRun).ToList();
        if (run.Count == 0) return "Nothing to clean right now.";
        var question = "Quick Clean will permanently delete:\n\n" +
            string.Join("\n", run.Select(p => $"• {p.Scan.Category.Name}: {CleanupEngine.Size(p.Scan.Bytes)}")) +
            $"\n\nTemp files must be older than {minimumAgeDays} days.";
        if (run.Any(p => p.Scan.Category.Kind == CategoryKind.RecycleBin))
            question += "\nEVERYTHING in the Recycle Bin will be removed.";
        if (!confirm(question + "\n\nContinue?")) return "Cancelled. Nothing was deleted.";

        var state = new CleanProgressState();
        var (results, failures) = await Task.Run(() =>
            CleanRunner.Run(run, minimumAgeDays, isAdmin, state, default, isRunning));
        var summary = $"Freed {CleanupEngine.Size(state.Freed)}." +
                      (failures.Count > 0 ? " Some items failed." : "");
        ActivityLog.Post(ActivityLog.Cleanup, "Quick Clean", failures.Count == 0, state.Freed, string.Join("; ",
            results.Select(r => $"{r.Category.Name}: {r.Deleted:N0} removed, {r.Skipped:N0} skipped").Concat(failures)));
        return summary;
    }

    /// <summary>Applies the Safe-rated tweaks, the same set as One-Click Optimize.</summary>
    public static async Task<string> OptimizeAsync(TweakEngine engine, bool isAdmin, Func<string, bool> confirm)
    {
        var targets = await Task.Run(() => engine.OneClickTargets(isAdmin));
        if (targets.Count == 0) return "All available Safe tweaks are already applied.";
        if (!confirm($"Apply these {targets.Count} Safe tweaks? Some change how Windows looks or what it suggests. " +
                     "They can be restored with Reset to Defaults on the System Tweaks page.\n\n" +
                     string.Join("\n", targets.Select(t => "• " + t.Name))))
            return "Cancelled. Nothing was changed.";
        var report = await Task.Run(() => engine.Apply(targets, isAdmin));
        ActivityLog.Post("Tweaks", "Optimize (Safe tweaks)", report.Problems.Count == 0, null,
            $"Applied {report.Changed}: {string.Join(", ", targets.Select(t => t.Name))}. " +
            string.Join(" ", report.Problems));
        return $"Applied {report.Changed} tweak(s)." +
               (report.Problems.Count > 0 ? $" {report.Problems.Count} problem(s); see History." : "");
    }
}
