namespace PCOptimizer.Services;

public enum CleanMode { Quick, Deep }

public sealed record PlannedItem(CategoryScan Scan, string? Blocked)
{
    public bool WillRun => Blocked == null &&
        (Scan.Items > 0 || Scan.Bytes > 0 || (Scan.Category.Kind == CategoryKind.WindowsTool && Scan.Incomplete));
}

/// <summary>Counters updated from the cleaning thread and read by the window.</summary>
public sealed class CleanProgressState
{
    private long _freed, _done;
    public long Total;
    public volatile string Current = "";
    public long Freed => Interlocked.Read(ref _freed);
    public long Done => Interlocked.Read(ref _done);
    public void Add(long freed, long processed)
    {
        Interlocked.Add(ref _freed, freed);
        Interlocked.Add(ref _done, processed);
    }
}

public static class CleanRunner
{
    private static readonly string[] QuickIds = { "user-temp", "recycle-bin" };
    private static readonly string[] DeepIds =
    {
        "user-temp", "windows-temp", "chrome", "edge", "firefox", "brave", "shaders", "update-cache",
        "delivery-optimization", "iis-logs", "crash-dumps", "user-error-reports", "system-error-reports", "windows-logs"
    };

    public static IReadOnlyList<CleanCategory> Categories(CleanMode mode, bool includeRecycleBin = true)
    {
        var all = CleanupCatalog.Create().Concat(CleanupCatalog.DeepExtras()).ToDictionary(c => c.Id);
        return (mode == CleanMode.Quick ? QuickIds : DeepIds)
            .Where(id => includeRecycleBin || id != "recycle-bin")
            .Select(id => all[id]).ToList();
    }

    /// <summary>Read-only.</summary>
    public static List<PlannedItem> Plan(IEnumerable<CleanCategory> categories, int minimumAgeDays, bool isAdmin,
        Func<string, bool>? isRunning = null, CleanProgressState? state = null, CancellationToken token = default)
    {
        var plan = new List<PlannedItem>();
        foreach (var category in categories)
        {
            token.ThrowIfCancellationRequested();
            if (state != null) state.Current = "Checking " + category.Name;
            var scan = CleanupEngine.Scan(category, minimumAgeDays, token);
            plan.Add(new PlannedItem(scan, CleanupEngine.WhyBlocked(category, isAdmin, isRunning)));
        }
        return plan;
    }

    /// <summary>Deletes what was planned. One failing category does not stop the others.</summary>
    public static (List<CleanResult> Results, List<string> Failures) Run(IEnumerable<PlannedItem> plan,
        int minimumAgeDays, bool isAdmin, CleanProgressState state, CancellationToken token = default,
        Func<string, bool>? isRunning = null)
    {
        var items = plan.Where(p => p.WillRun).ToList();
        state.Total = items.Sum(p => p.Scan.Bytes);
        var results = new List<CleanResult>();
        var failures = new List<string>();
        foreach (var item in items)
        {
            if (token.IsCancellationRequested) break;
            var category = item.Scan.Category;
            state.Current = "Cleaning " + category.Name;
            try
            {
                var result = CleanupEngine.Clean(item.Scan, minimumAgeDays, isAdmin, isRunning,
                    (file, freed) => state.Add(freed, file.Size), token);
                // The Recycle Bin and Windows tools report only once they have finished.
                if (category.Kind is CategoryKind.RecycleBin or CategoryKind.WindowsTool)
                    state.Add(result.BytesDeleted, item.Scan.Bytes);
                results.Add(result);
            }
            catch (Exception ex) { failures.Add($"{category.Name}: {ex.Message}"); }
        }
        return (results, failures);
    }

    /// <summary>
    /// Used by Task Scheduler. Never uses administrator rights, even if started with them, and empties
    /// the Recycle Bin only if that was chosen.
    /// </summary>
    public static string RunUnattended(CleanMode mode, bool includeRecycleBin, int minimumAgeDays)
    {
        var plan = Plan(Categories(mode, includeRecycleBin), minimumAgeDays, isAdmin: false);
        var state = new CleanProgressState();
        var (results, failures) = Run(plan, minimumAgeDays, false, state);
        ActivityLog.Record(ActivityLog.Cleanup, $"Automatic {mode} Clean", failures.Count == 0, state.Freed,
            string.Join("; ", results.Select(r => $"{r.Category.Name}: {r.Deleted:N0} removed").Concat(failures)));
        var lines = new List<string>
        {
            $"{DateTime.Now:yyyy-MM-dd HH:mm}  {mode} auto clean: removed {CleanupEngine.Size(state.Freed)}"
        };
        lines.AddRange(results.Select(r => $"    {r.Category.Name}: {r.Deleted:N0} removed, {r.Skipped:N0} skipped"));
        lines.AddRange(plan.Where(p => p.Blocked != null).Select(p => $"    {p.Scan.Category.Name}: not cleaned. {p.Blocked}"));
        lines.AddRange(failures.Select(f => "    Failed: " + f));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
