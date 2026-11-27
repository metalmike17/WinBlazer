using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.MemoryTools;

namespace PCOptimizer.Modules;

public sealed class MemoryOptimizerModule : IBackgroundModule
{
    private MemoryService? _service;

    public string Id => "memory-optimizer";
    public string Title => "Memory Optimizer";
    public string Icon => "";
    public string Description => "RAM usage, memory actions and background app limits";
    public int Order => 25;

    public IDisposable StartBackground(AppContextServices services)
    {
        var service = _service = new MemoryService(services);
        return new Stopper(() => { service.Dispose(); if (_service == service) _service = null; });
    }

    public FrameworkElement CreateView(AppContextServices services) =>
        new MemoryOptimizerView(services, _service ?? throw new InvalidOperationException(
            "The memory service is not running."));

    private sealed class Stopper(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}

public sealed class MemoryOptimizerView : UserControl
{
    private sealed record AppRow(string Path, CheckBox Check);

    private readonly AppContextServices _services;
    private readonly MemoryService _service;
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly MemoryChart _chart = new() { Height = 220 };
    private readonly TextBlock _inUse = Legend(), _modified = Legend(), _standby = Legend(), _free = Legend();
    private readonly TextBlock _result = Ui.Text("Run an action to see free memory before and after.", 12, "MutedBrush");
    private readonly List<Button> _actionButtons = new();
    private readonly TextBlock _smartStatus = Ui.Text("", 12, "AccentBrush");
    private readonly TextBlock _limitStatus = Ui.Text("", 12, "AccentBrush");
    private readonly StackPanel _appList = new();
    private readonly List<AppRow> _apps = new();
    private bool _busy;

    public int SampleCount => _service.History.Count;
    public int AppCount => _apps.Count;
    public int ActionButtonsEnabled => _actionButtons.Count(b => b.IsEnabled);

    private static TextBlock Legend() => new() { Margin = new Thickness(0, 0, 18, 4), FontSize = 12 };

    public MemoryOptimizerView(AppContextServices services, MemoryService service)
    {
        _services = services;
        _service = service;

        var legend = new WrapPanel();
        foreach (var (block, color) in new[]
                 {
                     (_inUse, MemoryChart.InUse), (_modified, MemoryChart.Modified), (_standby, MemoryChart.Standby),
                     (_free, Color.FromRgb(0x8B, 0x98, 0xBF))
                 })
        {
            block.Foreground = Ui.Brush(color == MemoryChart.Standby ? "PurpleBrush" : color == MemoryChart.InUse ? "AccentBrush"
                : color == MemoryChart.Modified ? "WarnBrush" : "MutedBrush");
            legend.Children.Add(block);
        }

        var page = Ui.Page("Memory Optimizer",
            "Standby memory is a cache Windows can reuse instantly. Clearing it rarely improves speed and can slow " +
            "the next load.");
        page.Children.Add(Card(legend, _chart));
        page.Children.Add(ActionCard());
        page.Children.Add(SmartCard());
        page.Children.Add(LimiterCard());
        Content = Ui.Scroll(page);

        Loaded += (_, _) => { _service.Updated -= Refresh; _service.Updated += Refresh; Refresh(); _ = RefreshAppsAsync(); };
        Unloaded += (_, _) => _service.Updated -= Refresh;
    }

    private static Border Card(params UIElement[] children)
    {
        var card = Ui.Card(Ui.Stack(children));
        card.Margin = new Thickness(0, 0, 12, 10);
        return card;
    }

    private void Refresh()
    {
        if (!IsVisible) return;
        _smartStatus.Text = _service.SmartStatus;
        _limitStatus.Text = _service.Limiter.LimitedCount == 0 && _service.LimiterFailures == 0
            ? "No processes are limited."
            : $"{_service.Limiter.LimitedCount} process(es) limited." +
              (_service.LimiterFailures > 0 ? $" {_service.LimiterFailures} could not be changed." : "");
        if (_service.History.LastOrDefault() is not { } m)
        {
            _inUse.Text = "Waiting for memory readings...";
            return;
        }
        Motion.Count(_inUse, m.InUse, v => $"■ In use {CleanupEngine.Size((long)v)}");
        Motion.Count(_modified, m.Modified, v => $"■ Modified {CleanupEngine.Size((long)v)}");
        Motion.Count(_standby, m.Standby, v => $"■ Standby {CleanupEngine.Size((long)v)}");
        var total = CleanupEngine.Size(m.Total);
        Motion.Count(_free, m.Free, v => $"Free {CleanupEngine.Size((long)v)}   Total {total}");
        _chart.Show(_service.History);
    }

    private Border ActionCard()
    {
        var buttons = new WrapPanel();
        foreach (var action in Enum.GetValues<MemoryAction>())
        {
            var button = new Button
            {
                Content = MemoryActions.Name(action), Margin = new Thickness(0, 0, 8, 8),
                IsEnabled = _isAdmin || !MemoryActions.NeedsAdmin(action)
            };
            button.Click += async (_, _) => await RunActionAsync(action);
            _actionButtons.Add(button);
            buttons.Children.Add(button);
        }
        var parts = new List<UIElement> { Ui.Text("Memory actions", 16, weight: FontWeights.SemiBold), buttons };
        if (!_isAdmin)
        {
            parts.Add(Ui.Text("Without administrator rights, only Empty Working Sets works, and only on some of " +
                              "your own processes.", 11, "MutedBrush"));
            var elevate = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Left };
            elevate.Click += (_, _) => RestartElevated();
            parts.Add(elevate);
        }
        parts.Add(_result);
        return Card(parts.ToArray());
    }

    private async Task RunActionAsync(MemoryAction action)
    {
        if (_busy) return;
        _busy = true;
        var enabled = _actionButtons.Where(b => b.IsEnabled).ToList();
        foreach (var b in enabled) b.IsEnabled = false;
        try
        {
            using var reader = new MemoryReader();
            var before = reader.Read();
            _result.Text = MemoryActions.Name(action) + " is running...";
            var outcome = await Task.Run(() => MemoryActions.Run(action, _isAdmin));
            await Task.Delay(1000);   // let the counters settle
            var after = reader.Read();
            var text = $"{MemoryActions.Name(action)}: {outcome.Message}";
            if (before != null && after != null) text += "\n" + Compare(before, after);
            _result.Text = text;
            ActivityLog.Post("Memory", MemoryActions.Name(action), outcome.Ok, null, text);
        }
        catch (Exception ex) { _result.Text = "The action failed: " + ex.Message; }
        finally
        {
            foreach (var b in enabled) b.IsEnabled = true;
            _busy = false;
        }
    }

    public static string Compare(MemorySnapshot before, MemorySnapshot after)
    {
        static string Change(long a, long b) => $"{CleanupEngine.Size(a)} → {CleanupEngine.Size(b)} " +
            $"({(b >= a ? "+" : "−")}{CleanupEngine.Size(Math.Abs(b - a))})";
        return $"Free:      {Change(before.Free, after.Free)}\n" +
               $"Available: {Change(before.Available, after.Available)}  (free + reusable standby)\n" +
               "Other programs also change memory use during the measurement.";
    }

    private Border SmartCard()
    {
        var memory = _services.Settings.Current.Memory;
        var enabled = new CheckBox { Content = "Turn on Smart Standby Cleaner", IsChecked = memory.SmartCleanerEnabled };
        var threshold = new TextBox
        {
            Text = memory.FreeThresholdMb.ToString(), Width = 90, Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(8, 0, 8, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(threshold, "Free memory threshold in MB");
        var apply = new Button { Content = "Save" };
        var note = Ui.Text("", 11, "MutedBrush");
        apply.Click += async (_, _) =>
        {
            if (!int.TryParse(threshold.Text.Trim(), out var mb) || mb is < 128 or > 65536)
            { note.Text = "Enter a whole number from 128 to 65536."; return; }
            _services.Settings.Current.Memory.FreeThresholdMb = mb;
            _services.Settings.Current.Memory.SmartCleanerEnabled = enabled.IsChecked == true;
            note.Text = await SaveAsync() ?? "Saved.";
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new TextBlock { Text = "Purge when free memory is below (MB)", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(threshold);
        row.Children.Add(apply);
        return Card(Ui.Text("Smart Standby Cleaner", 16, weight: FontWeights.SemiBold), enabled, row,
            Ui.Text($"Runs while the app is open or in the tray. At most once every " +
                    $"{SmartCleanerRules.Cooldown.TotalSeconds:N0} seconds, and only if standby is at least " +
                    $"{CleanupEngine.Size(SmartCleanerRules.MinimumStandby)}. Needs administrator rights.", 11, "MutedBrush"),
            note, _smartStatus);
    }

    private Border LimiterCard()
    {
        var refresh = new Button { Content = "Refresh list" };
        var apply = new Button { Content = "Apply limits" };
        var clear = new Button { Content = "Remove all limits" };
        refresh.Role(ButtonRole.Refresh);
        refresh.Click += async (_, _) => await RefreshAppsAsync();
        apply.Click += (_, _) => ApplyChoices(_apps.Where(a => a.Check.IsChecked == true).Select(a => a.Path).ToList(), true);
        clear.Click += (_, _) => ApplyChoices(new List<string>(), false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        foreach (var b in new[] { refresh, apply, clear }) buttons.Children.Add(b);
        var scroll = Ui.Scroll(_appList);
        scroll.MaxHeight = 260;
        return Card(Ui.Text("Background App Limiter", 16, weight: FontWeights.SemiBold),
            Ui.Text("Gives ticked apps low priority and Efficiency mode, so other work goes first. This does not free " +
                    "memory, and limited apps may run slowly. Avoid games, calls, audio, recording and security tools. " +
                    "Choices are saved and applied to new processes while this app runs. Exiting restores them; if " +
                    "the app crashes, restart the limited apps.", 11, "MutedBrush"),
            buttons, _limitStatus, scroll);
    }

    private async void ApplyChoices(List<string> paths, bool confirm)
    {
        if (confirm && paths.Count > 0 && MessageBox.Show(
                $"Limit these apps?\n\n{string.Join("\n", paths.Select(Path.GetFileName))}", "App Limiter",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _services.Settings.Current.Memory.LimitedApps = paths;
        var error = await SaveAsync();
        _service.ApplyLimits();
        if (paths.Count == 0) foreach (var a in _apps) a.Check.IsChecked = false;
        Refresh();
        if (error != null) _limitStatus.Text = error;
    }

    private async Task<string?> SaveAsync()
    {
        try { await _services.Settings.SaveAsync(); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return "Could not save: " + ex.Message; }
    }

    private int _appsRequest;

    private async Task RefreshAppsAsync()
    {
        var request = ++_appsRequest;
        var running = await Task.Run(() => AppLimiter.ListApps().ToList());
        if (request != _appsRequest) return;   // a newer refresh is on its way
        var saved = new HashSet<string>(_services.Settings.Current.Memory.LimitedApps, StringComparer.OrdinalIgnoreCase);
        // Keep ticks that have not been applied yet.
        var ticked = _apps.Count == 0 ? saved
            : _apps.Where(a => a.Check.IsChecked == true).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Saved apps that are closed stay listed, so they can be unticked.
        running.AddRange(saved.Where(p => !running.Any(a => a.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new RunningApp(p, Path.GetFileNameWithoutExtension(p), 0, 0)));
        _apps.Clear();
        _appList.Children.Clear();
        foreach (var app in running)
        {
            var state = app.Processes == 0 ? "not running"
                : $"{app.Processes} process(es), about {CleanupEngine.Size(app.WorkingSet)}";
            var check = new CheckBox
            {
                Content = new TextBlock { Text = $"{app.Name}   ({state})", Foreground = Ui.Brush("TextBrush") },
                IsChecked = ticked.Contains(app.Path), ToolTip = app.Path, Margin = new Thickness(0, 3, 0, 3)
            };
            _apps.Add(new AppRow(app.Path, check));
            _appList.Children.Add(check);
        }
    }

    private void RestartElevated()
    {
        try
        {
            PCOptimizer.Core.SingleInstance.StartElevatedCopy();
            Application.Current.MainWindow?.Close();
        }
        catch (Win32Exception) { _result.Text = "Administrator restart was cancelled."; }
    }
}
