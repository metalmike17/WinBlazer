using PCOptimizer.Core;
using PCOptimizer.Services.MemoryTools;
using PCOptimizer.Services.RegistryTools;
using PCOptimizer.Services.StartupTools;
using PCOptimizer.Services.Tweaks;

namespace PCOptimizer.Services;

/// <summary>Null means the value could not be read.</summary>
public sealed record ScoreInputs(long? JunkBytes, double? FreeDiskPercent, int? TweaksApplied, int? TweaksTotal,
                                 int? StartupApps, double? AvailableMemoryPercent);

/// <summary>
/// FixAction is a quick action id (runs after confirmation); otherwise the fix button opens a page where
/// the user decides.
/// </summary>
public sealed record ScoreArea(string Id, string Title, int Weight, double? Fraction, string Detail,
    string FixLabel, string? FixAction, string FixModule, string? FixSection = null)
{
    public int? Percent => Fraction is { } f ? (int)Math.Floor(f * 100) : null;
}

public sealed record ScoreResult(int? Score, List<ScoreArea> Areas);

/// <summary>This app's own housekeeping checklist. It does not measure speed.</summary>
public static class OptimizationScore
{
    /// <summary>1 at or beyond <paramref name="good"/>, 0 at or beyond <paramref name="bad"/>, linear between.</summary>
    private static double Scale(double value, double good, double bad) => Math.Clamp((value - bad) / (good - bad), 0, 1);

    public static ScoreResult Calculate(ScoreInputs i)
    {
        const double mb = 1048576;
        var areas = new List<ScoreArea>
        {
            new("junk", "Junk files", 25, i.JunkBytes is { } j ? Scale(j, 200 * mb, 5120 * mb) : null,
                i.JunkBytes is { } size ? $"{CleanupEngine.Size(size)} in old temp files and the Recycle Bin (full marks up to 200 MB)"
                    : "Could not be measured", "Quick Clean", "clean", "temp-cache-cleaner"),
            new("disk", "System drive space", 25, i.FreeDiskPercent is { } d ? Scale(d, 20, 5) : null,
                i.FreeDiskPercent is { } free ? $"{free:0}% free (full marks from 20%)" : "Could not be measured",
                "Open Disk Cleaner", null, "disk-cleaner", "analyzer"),
            new("tweaks", "Safe tweaks", 20,
                i is { TweaksTotal: > 0, TweaksApplied: { } a } ? (double)a / i.TweaksTotal.Value : null,
                i.TweaksTotal is { } total ? $"{i.TweaksApplied} of {total} available Safe tweaks applied. These are preferences."
                    : "Could not be read", "Optimize", "optimize", "system-tweaks"),
            new("startup", "Startup apps", 15, i.StartupApps is { } s ? Scale(s, 5, 15) : null,
                i.StartupApps is { } n ? $"{n} optional startup item(s) enabled (full marks up to 5)" : "Could not be read",
                "Review startup apps", null, "startup-manager"),
            new("memory", "Available memory", 15,
                i.AvailableMemoryPercent is { } m ? Scale(m, 30, 5) : null,
                i.AvailableMemoryPercent is { } available ? $"{available:0}% available right now (full marks from 30%)"
                    : "Could not be measured", "See memory use", null, "system-monitor", "processes")
        };
        var known = areas.Where(a => a.Fraction != null).ToList();
        int? score = null;
        if (known.Count > 0)
        {
            var value = (int)Math.Round(100 * known.Sum(a => a.Weight * a.Fraction!.Value) / known.Sum(a => a.Weight));
            // 100 is only shown when nothing at all is left.
            score = value == 100 && known.Any(a => a.Fraction < 1) ? 99 : value;
        }
        // Lowest first: these are the ones worth fixing.
        return new ScoreResult(score, areas.OrderBy(a => a.Fraction ?? 2).ThenByDescending(a => a.Weight).ToList());
    }

    public static TweakEngine CreateTweakEngine(SettingsService settings) => new(TweakCatalog.Create(),
        new TweakContext(new RegistryStore(), new WindowsTweakSystem()),
        Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "tweaks-journal.json"));

    /// <summary>Read-only. A reading that fails leaves its area out of the score.</summary>
    public static ScoreInputs Collect(SettingsService settings, bool isAdmin)
    {
        T? Try<T>(Func<T?> read) where T : struct
        {
            try { return read(); }
            catch (Exception) { return null; }
        }

        var junk = Try<long>(() =>
        {
            var plan = CleanRunner.Plan(CleanRunner.Categories(CleanMode.Quick),
                                        settings.Current.Cleanup.MinimumAgeDays, isAdmin);
            return plan.All(p => p.Scan.Error != null) ? null : plan.Where(p => p.WillRun).Sum(p => p.Scan.Bytes);
        });
        var disk = Try<double>(() =>
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            return 100.0 * drive.AvailableFreeSpace / drive.TotalSize;
        });
        var tweaks = Try<(int, int)>(() =>
        {
            var engine = CreateTweakEngine(settings);
            var states = engine.Tweaks.Where(t => t.Risk == TweakRisk.Safe && (isAdmin || !t.NeedsAdmin))
                .Select(t => engine.Status(t).State).Where(s => s != TweakState.Unavailable).ToList();
            return states.Count == 0 ? null : (states.Count(s => s == TweakState.Applied), states.Count);
        });
        var startup = Try<int>(() => new StartupCatalog(tasks: new ScheduledTaskSource()).Scan()
            .Count(e => e.Enabled && e.LockReason == null && e.BulkSkipReason == null));
        var memory = Try<double>(() =>
        {
            using var reader = new MemoryReader();
            return reader.Read() is { Total: > 0 } m ? 100.0 * m.Available / m.Total : null;
        });
        return new ScoreInputs(junk, disk, tweaks?.Item1, tweaks?.Item2, startup, memory);
    }
}
