using System.Windows.Threading;
using PCOptimizer.Core;

namespace PCOptimizer.Services.MemoryTools;

public static class SmartCleanerRules
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    public const long MinimumStandby = 256L << 20;   // below this, purging gains almost nothing

    public static bool ShouldPurge(MemorySnapshot memory, long thresholdBytes, DateTime? lastPurgeUtc) =>
        memory.Free < thresholdBytes && memory.Standby >= MinimumStandby &&
        (lastPurgeUtc == null || memory.TimeUtc - lastPurgeUtc >= Cooldown);
}

/// <summary>Keeps the 2-minute history and runs the automatic features, even when the page is closed.</summary>
public sealed class MemoryService : IDisposable
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private readonly AppContextServices _services;
    private readonly MemoryReader _reader = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<MemorySnapshot> _history = new();
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private DateTime? _lastPurge;
    private int _ticks;
    private bool _purging, _limiting;

    public AppLimiter Limiter { get; } = new();
    public IReadOnlyList<MemorySnapshot> History => _history;
    public string SmartStatus { get; private set; } = "";
    public int LimiterFailures { get; private set; }
    public event Action? Updated;

    public MemoryService(AppContextServices services)
    {
        _services = services;
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    private async Task TickAsync()
    {
        var snapshot = _reader.Read();
        if (snapshot != null)
        {
            _history.Add(snapshot);
            _history.RemoveAll(s => snapshot.TimeUtc - s.TimeUtc > Window);
            await RunSmartCleanerAsync(snapshot);
        }
        if (_ticks++ % 5 == 0) ApplyLimits();
        Updated?.Invoke();
    }

    private async Task RunSmartCleanerAsync(MemorySnapshot memory)
    {
        var settings = _services.Settings.Current.Memory;
        if (!settings.SmartCleanerEnabled) { SmartStatus = "Off."; return; }
        if (!_isAdmin) { SmartStatus = "Waiting: purging needs administrator rights."; return; }
        if (_purging || !SmartCleanerRules.ShouldPurge(memory, settings.FreeThresholdMb * 1048576L, _lastPurge))
        {
            if (_lastPurge == null) SmartStatus = $"Watching. Purges when free memory is below {settings.FreeThresholdMb:N0} MB.";
            return;
        }
        _purging = true;
        _lastPurge = memory.TimeUtc;   // also applies the cooldown after a failure
        try
        {
            var result = await Task.Run(() => MemoryActions.Run(MemoryAction.PurgeStandbyList, true));
            ActivityLog.Post("Memory", "Smart Standby Cleaner purge", result.Ok, null, result.Message);
            SmartStatus = $"{DateTime.Now:T}: " + (result.Ok
                ? $"purged standby list (free was {CleanupEngine.Size(memory.Free)})."
                : "purge failed. " + result.Message);
        }
        finally { _purging = false; }
    }

    /// <summary>Applies the saved limiter choices to running and newly started processes.</summary>
    public void ApplyLimits()
    {
        if (_limiting) return;
        _limiting = true;
        try { LimiterFailures = Limiter.Apply(_services.Settings.Current.Memory.LimitedApps); }
        finally { _limiting = false; }
    }

    public void Dispose()
    {
        _timer.Stop();
        Limiter.RestoreAll();   // leave apps as they were found
        _reader.Dispose();
    }
}
