using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using PCOptimizer.Core;
using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer.Services.GameTools;

/// <summary>Detects games and runs Game Mode, even while its page is closed. Used from the UI thread.</summary>
public sealed class GameModeService : IDisposable
{
    private readonly AppContextServices _services;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly GameWatcher _watcher = new();
    private DateTime _libraryRead = DateTime.MinValue;
    private Process? _watchdog;
    private bool _busy, _polling;
    private volatile bool _disposed;

    public GameModeEngine Engine { get; }
    public List<GameInfo> Games { get; private set; } = new();
    public List<RunningGame> Running { get; private set; } = new();
    public bool IsActive { get; private set; }
    public bool IsBusy => _busy;
    public List<string> LastReport { get; private set; } = new();
    public RunningGame? Current =>
        Running.FirstOrDefault(r => r.Game.Id == _watcher.Attached) ?? Running.FirstOrDefault();
    public event Action? Updated;

    public GameModeService(AppContextServices services, bool watch = true)
    {
        _services = services;
        var journal = Path.Combine(Path.GetDirectoryName(services.Settings.FilePath)!, "gamemode-session.json");
        Engine = new GameModeEngine(new WindowsGameSystem(), new RegistryStore(), journal,
                                    ProtectedFolder.ForThisPc(), CleanupEngine.IsAdministrator());
        _timer.Tick += async (_, _) => await PollAsync();
        _ = StartAsync(watch);
    }

    private async Task StartAsync(bool watch)
    {
        _busy = true;
        try
        {
            // Left over from a crash, power cut or restart.
            var recovered = await Task.Run(() => Engine.Recover());
            if (recovered != null)
            {
                LastReport = recovered.Lines.Prepend(recovered.Ok ? "Recovered from an interrupted session:"
                    : "An interrupted session still needs attention:").ToList();
                ActivityLog.Post("Game Mode", "Restore after an interrupted session", recovered.Ok, null,
                                   string.Join(" ", LastReport));
            }
        }
        catch (Exception ex) { LastReport = new() { "Recovery failed: " + ex.Message }; }
        finally { _busy = false; }
        if (_disposed) return;
        if (watch) _timer.Start();
        await PollAsync();
    }

    public void ReloadLibrary() => _libraryRead = DateTime.MinValue;

    public async Task PollAsync()
    {
        if (_polling || _disposed) return;
        _polling = true;
        try
        {
            var settings = _services.Settings.Current.GameMode;
            var reload = DateTime.UtcNow - _libraryRead > TimeSpan.FromMinutes(5);
            var custom = settings.CustomPaths.ToList();
            var known = Games;
            var (games, running) = await Task.Run(() =>
            {
                var list = reload ? GameLibrary.Scan(custom) : known;
                return (list, GameLibrary.Match(list, GameLibrary.RunningProcesses()));
            });
            if (reload) _libraryRead = DateTime.UtcNow;
            (Games, Running) = (games, running.Where(r => !settings.IgnoredGames.Contains(r.Game.Id)).ToList());

            if (!_busy && _timer.IsEnabled)
            {
                var decision = _watcher.Poll(Running.Select(r => r.Game.Id).ToList(), IsActive, settings.AutoActivate);
                if (decision == WatchDecision.Activate) await ActivateAsync(false);
                else if (decision == WatchDecision.Deactivate) await RestoreAsync(false);
            }
        }
        catch (Exception ex) { LastReport = new() { "Game detection failed: " + ex.Message }; }
        finally { _polling = false; Updated?.Invoke(); }
    }

    public async Task ActivateAsync(bool manual)
    {
        if (_busy || IsActive || _disposed) return;
        _busy = true;
        Updated?.Invoke();
        try
        {
            var options = _services.Settings.Current.GameMode.Copy();
            // Never close a game, or anything inside a game's folder.
            var gameProcesses = GameLibrary.Match(Games, options.AppsToClose.Select((p, i) => new ProcessInfo(i, p)).ToList());
            var blocked = gameProcesses.SelectMany(g => g.ProcessIds).Select(i => options.AppsToClose[i]).ToHashSet();
            options.AppsToClose.RemoveAll(blocked.Contains);
            var report = await Task.Run(() =>
            {
                if (_disposed) return new GameModeReport(false, new() { "The app is closing." });
                var result = Engine.Activate(options);
                if (_disposed && result.Ok) Engine.Restore();   // exit happened while activating
                return result;
            });
            LastReport = report.Lines.Prepend(manual ? "Activated manually:" : "Activated for a game:").ToList();
            IsActive = report.Ok;
            ActivityLog.Post("Game Mode", "Game Mode turned on", report.Ok, null, string.Join(" ", LastReport));
            if (IsActive) StartWatchdog();
        }
        catch (Exception ex)
        {
            LastReport = new() { "Activation stopped: " + ex.Message };
            IsActive = Engine.HasSession;   // so that Restore stays available
        }
        finally { _busy = false; Updated?.Invoke(); }
    }

    public async Task RestoreAsync(bool manual)
    {
        if (_busy || _disposed) return;
        _busy = true;
        if (manual) _watcher.ManualOff(Running.Select(r => r.Game.Id));
        Updated?.Invoke();
        try
        {
            var report = await Task.Run(() => Engine.Restore());
            LastReport = report.Lines.Prepend(manual ? "Turned off manually:" : "Game closed:").ToList();
            IsActive = false;
            ActivityLog.Post("Game Mode", "Game Mode turned off", report.Ok, null, string.Join(" ", LastReport));
        }
        catch (Exception ex) { LastReport = new() { "Restore failed: " + ex.Message }; }
        finally { _busy = false; Updated?.Invoke(); }
    }

    /// <summary>A small second process that restores settings if this one crashes or is ended.</summary>
    private void StartWatchdog()
    {
        if (_watchdog is { HasExited: false }) return;
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--gamemode-watchdog");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            _watchdog = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            LastReport.Add("Warning: the crash watchdog could not start. " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        try
        {
            if (Engine.HasSession && !Engine.OwnedByAnotherInstance()) Engine.Restore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        // If restoring failed, the journal stays for the watchdog, next sign-in and next start.
    }
}
