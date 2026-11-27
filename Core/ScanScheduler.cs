using System.Windows.Threading;

namespace PCOptimizer.Core;

/// <summary>Runs due scans while the app is open (including in the tray). Scans are read-only.</summary>
public sealed class ScanScheduler
{
    private readonly AppContextServices _services;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _busy;

    public event Action<string, string>? ScanFinished;   // title, message

    public ScanScheduler(AppContextServices services)
    {
        _services = services;
        _timer.Tick += async (_, _) => await RunDueAsync(DateTime.UtcNow);
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public static bool IsDue(ModuleSchedule schedule, DateTime nowUtc)
    {
        if (schedule.Frequency == ScanFrequency.Off) return false;
        if (schedule.LastRunUtc is not { } last) return true;
        var interval = TimeSpan.FromDays(schedule.Frequency == ScanFrequency.Daily ? 1 : 7);
        return nowUtc - last >= interval;
    }

    public async Task RunDueAsync(DateTime nowUtc)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            foreach (var module in _services.Modules.All.OfType<IScannableModule>())
            {
                if (!_services.Settings.Current.Schedules.TryGetValue(module.Id, out var schedule)
                    || !IsDue(schedule, nowUtc)) continue;
                string message;
                try { message = await module.ScanAsync(_services.Settings.Current.Cleanup, CancellationToken.None); }
                catch (Exception ex) { message = "Scheduled scan failed: " + ex.Message; }

                // Settings may have been edited during the scan, so look the schedule up again.
                if (_services.Settings.Current.Schedules.TryGetValue(module.Id, out var latest))
                    latest.LastRunUtc = nowUtc;
                try { _services.Settings.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { message += " (Could not save the scan time.)"; }
                ScanFinished?.Invoke(module.Title, message);
            }
        }
        finally { _busy = false; }
    }
}
