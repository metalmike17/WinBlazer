using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.GameTools;
using PCOptimizer.Services.MemoryTools;

namespace PCOptimizer.Modules;

public sealed class GameModeModule : IBackgroundModule, IQuickActionProvider
{
    public IEnumerable<QuickAction> QuickActions(AppContextServices services)
    {
        bool On() => _service is { } s && (s.IsActive || s.Engine.HasSession);
        yield return new QuickAction("game-mode", 3, () => On() ? "Game Mode: turn off" : "Game Mode: turn on",
            async _ =>
            {
                if (_service == null) return "Game Mode is not running.";
                if (_service.IsBusy) return "Game Mode is busy. Try again in a moment.";
                if (On()) await _service.RestoreAsync(true);
                else await _service.ActivateAsync(true);
                return string.Join(" ", _service.LastReport.Take(4));
            });
    }

    private GameModeService? _service;

    public string Id => "game-mode";
    public string Title => "Game Mode";
    public string Icon => "";
    public string Description => "One-click gaming setup that restores itself";
    public int Order => 5;

    public IDisposable StartBackground(AppContextServices services)
    {
        var service = _service = new GameModeService(services);
        return service;
    }

    public FrameworkElement CreateView(AppContextServices services) =>
        new GameModeView(services, _service ?? throw new InvalidOperationException("Game Mode is not running."));
}

public sealed class GameModeView : UserControl
{
    private readonly AppContextServices _services;
    private readonly GameModeService _service;
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly Button _main = new() { FontSize = 18, Padding = new Thickness(28, 12, 28, 12), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _state = Ui.Text("", 14, weight: FontWeights.SemiBold);
    private readonly TextBlock _report = Ui.Text("", 12, "MutedBrush");
    private readonly TextBlock _saveNote = Ui.Text("", 11, "AccentBrush");
    private readonly TextBlock _cpuText = Ui.Text("CPU", 13), _gpuText = Ui.Text("GPU", 13), _ramText = Ui.Text("Game RAM", 13);
    private readonly Sparkline _cpu = new() { Height = 60 }, _gpu = new() { Height = 60 }, _ram = new() { Height = 60, Maximum = 0 };
    private readonly Border _liveCard;
    private readonly StackPanel _appList = new(), _gameList = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private MetricsService? _metrics;
    private bool _sampling;
    private string _shownGames = "?";

    public int ListedGames => _gameList.Children.Count;
    public string StateText => _state.Text;

    public GameModeView(AppContextServices services, GameModeService service)
    {
        _services = services;
        _service = service;

        _main.Role(ButtonRole.Main);
        _main.Click += async (_, _) =>
        {
            if (_service.IsActive || _service.Engine.HasSession) await _service.RestoreAsync(true);
            else await _service.ActivateAsync(true);
        };
        _statsTimer.Tick += async (_, _) => await SampleAsync();

        var page = Ui.Page("Game Mode", "Applies your choices below, then puts things back when the game closes. " +
                           "Performance gains vary and may be too small to notice.");
        page.Children.Add(Card(_main, _state, _report));
        var live = new UniformGrid { Columns = 3 };
        foreach (var (text, chart) in new[] { (_cpuText, _cpu), (_gpuText, _gpu), (_ramText, _ram) })
        {
            var cell = Ui.Stack(text, chart);
            cell.Margin = new Thickness(0, 0, 12, 0);
            live.Children.Add(cell);
        }
        _liveCard = Card(Ui.Text("Live while active", 16, weight: FontWeights.SemiBold), live);
        page.Children.Add(_liveCard);
        page.Children.Add(OptionsCard());
        page.Children.Add(GamesCard());
        page.Children.Add(AppsCard());
        Content = Ui.Scroll(page);

        Loaded += (_, _) =>
        {
            _service.Updated -= Refresh;
            _service.Updated += Refresh;
            _statsTimer.Start();
            Refresh();
            _ = RefreshAppsAsync();
        };
        Unloaded += (_, _) =>
        {
            _service.Updated -= Refresh;
            _statsTimer.Stop();
            if (!_sampling) { _metrics?.Dispose(); _metrics = null; }
        };
    }

    private static Border Card(params UIElement[] children)
    {
        var card = Ui.Card(Ui.Stack(children));
        card.Margin = new Thickness(0, 0, 12, 10);
        return card;
    }

    private GameModeSettings Options => _services.Settings.Current.GameMode;

    private async void Save()
    {
        try { await _services.Settings.SaveAsync(); _saveNote.Text = "Saved. Changes apply the next time Game Mode starts."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _saveNote.Text = "Could not save: " + ex.Message; }
    }

    private CheckBox Option(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 3, 0, 3) };
        box.Click += (_, _) => { set(box.IsChecked == true); Save(); };
        return box;
    }

    private void Refresh()
    {
        var pending = !_service.IsActive && !_service.IsBusy && _service.Engine.HasSession;
        _main.Content = _service.IsBusy ? "Working..." : _service.IsActive ? "Turn off and restore"
            : pending ? "Restore previous settings" : "Activate Game Mode";
        _main.IsEnabled = !_service.IsBusy;
        var game = _service.Current;
        _state.Text = (_service.IsActive ? "GAME MODE IS ON" : pending ? "A session still needs restoring" : "Game Mode is off")
            + (game != null ? $"  •  Running: {game.Game.Name} ({game.Game.Store})" : "");
        _report.Text = string.Join("\n", _service.LastReport);
        _liveCard.Visibility = _service.IsActive ? Visibility.Visible : Visibility.Collapsed;
        var key = string.Join("|", _service.Games.Select(g => g.Id));
        if (key != _shownGames) { _shownGames = key; RenderGames(); }
    }

    private async Task SampleAsync()
    {
        if (_sampling || !_service.IsActive || !IsVisible) return;
        _sampling = true;
        try
        {
            _metrics ??= new MetricsService();
            var metrics = _metrics;
            var ids = _service.Current?.ProcessIds ?? Array.Empty<int>();
            var (sample, memory) = await Task.Run(() =>
            {
                long total = 0;
                foreach (var id in ids)
                {
                    try { using var p = Process.GetProcessById(id); total += p.WorkingSet64; }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
                }
                return (metrics.Sample(), total);
            });
            _cpu.Add(sample.Cpu);
            _gpu.Add(sample.Gpu);
            _ram.Add(memory > 0 ? memory : null);
            _cpuText.Text = "CPU " + (sample.Cpu is { } c ? $"{c:N0}%" : "measuring...");
            _gpuText.Text = "GPU " + (sample.Gpu is { } g ? $"{g:N0}%  {sample.GpuDetail}" : sample.GpuDetail);
            _ramText.Text = "Game RAM " + (memory > 0 ? CleanupEngine.Size(memory)
                : ids.Count == 0 ? "no game detected" : "not readable");
        }
        finally { _sampling = false; }
    }

    private Border OptionsCard()
    {
        var o = Options;
        var parts = new List<UIElement>
        {
            Ui.Text("What Game Mode does", 16, weight: FontWeights.SemiBold),
            Option("Switch to the Ultimate Performance power plan (more power and heat)", o.UltimatePower, v => Options.UltimatePower = v),
            Option("Turn off Game DVR background capture and the controller's Game Bar button", o.DisableGameDvr, v => Options.DisableGameDvr = v),
            Option("Turn off notification pop-ups (calls and alerts may be missed)", o.SilenceNotifications, v => Options.SilenceNotifications = v),
            Option("NVIDIA power mode: prefer maximum performance (global setting)", o.GpuMaxPerformance, v => Options.GpuMaxPerformance = v),
            Option("Purge standby memory (administrator only; rarely helps)", o.PurgeStandby, v => Options.PurgeStandby = v),
            Option("Reopen closed apps afterward", o.RelaunchClosedApps, v => Options.RelaunchClosedApps = v),
            Ui.Text("Win+G can still open Game Bar. A game's own NVIDIA profile overrides the global GPU setting.", 11, "MutedBrush")
        };
        if (!_isAdmin)
        {
            parts.Add(Ui.Text("Not running as administrator: the memory purge is skipped.", 11, "MutedBrush"));
            var elevate = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Left };
            elevate.Click += (_, _) => RestartElevated();
            parts.Add(elevate);
        }
        parts.Add(_saveNote);
        return Card(parts.ToArray());
    }

    private Border GamesCard()
    {
        var auto = Option("Turn Game Mode on automatically when a ticked game starts", Options.AutoActivate,
                          v => Options.AutoActivate = v);
        var addProgram = new Button { Content = "Add game program..." };
        var addFolder = new Button { Content = "Add game folder..." };
        var rescan = new Button { Content = "Rescan" };
        addProgram.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
            if (dialog.ShowDialog() == true) AddCustom(dialog.FileName);
        };
        addFolder.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog() == true) AddCustom(dialog.FolderName);
        };
        rescan.Role(ButtonRole.Refresh);
        rescan.Click += async (_, _) => { _service.ReloadLibrary(); await _service.PollAsync(); };
        var buttons = new WrapPanel();
        foreach (var b in new[] { addProgram, addFolder, rescan }) buttons.Children.Add(b);
        var scroll = Ui.Scroll(_gameList);
        scroll.MaxHeight = 220;
        return Card(Ui.Text("Games", 16, weight: FontWeights.SemiBold), auto,
            Ui.Text("Found from Steam, Epic, GOG, Xbox game folders and your own paths. Works while the app is open or " +
                    "in the tray. Whether on automatically or by button, it turns off about 12 seconds after the " +
                    "detected game closes.", 11, "MutedBrush"),
            buttons, scroll);
    }

    private void AddCustom(string path)
    {
        if (!GameLibrary.Custom(new[] { path }).Any())
        { _saveNote.Text = _state.Text = "That location is too broad or unsupported. Choose the game's own folder or program."; return; }
        if (!Options.CustomPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) Options.CustomPaths.Add(path);
        Save();
        _service.ReloadLibrary();
        _ = _service.PollAsync();
    }

    private void RenderGames()
    {
        _gameList.Children.Clear();
        foreach (var game in _service.Games)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            if (game.Store == "Custom")
            {
                var remove = new Button { Content = "Remove", FontSize = 11, Padding = new Thickness(8, 2, 8, 2) };
                remove.Click += (_, _) =>
                {
                    Options.CustomPaths.RemoveAll(p => Path.TrimEndingDirectorySeparator(p)
                        .Equals(game.Folder, StringComparison.OrdinalIgnoreCase));
                    Save();
                    _service.ReloadLibrary();
                    _ = _service.PollAsync();
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
            }
            var box = new CheckBox
            {
                Content = new TextBlock { Text = $"{game.Name}   ({game.Store})", Foreground = Ui.Brush("TextBrush") },
                IsChecked = !Options.IgnoredGames.Contains(game.Id), ToolTip = game.Folder
            };
            box.Click += (_, _) =>
            {
                Options.IgnoredGames.Remove(game.Id);
                if (box.IsChecked != true) Options.IgnoredGames.Add(game.Id);
                Save();
            };
            row.Children.Add(box);
            _gameList.Children.Add(row);
        }
        if (_service.Games.Count == 0)
            _gameList.Children.Add(Ui.Text("No games found yet. Add a program or folder.", 12, "MutedBrush"));
    }

    private Border AppsCard()
    {
        var refresh = new Button { Content = "Refresh list" };
        refresh.Click += async (_, _) => await RefreshAppsAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(refresh);
        var scroll = Ui.Scroll(_appList);
        scroll.MaxHeight = 220;
        return Card(Ui.Text("Background apps to close", 16, weight: FontWeights.SemiBold),
            Ui.Text("Ticked apps are asked to close, like clicking X. Nothing is force-closed, so tray apps may stay " +
                    "open. Save your work first. Avoid game launchers, voice chat, audio, VPN and security tools. " +
                    "Reopened apps may not restore their tabs or windows.", 11, "MutedBrush"),
            buttons, scroll);
    }

    private static readonly string[] Launchers =
        { "steam", "steamwebhelper", "EpicGamesLauncher", "GalaxyClient", "XboxPcApp", "PCOptimizer" };

    private int _appsRequest;

    private async Task RefreshAppsAsync()
    {
        var request = ++_appsRequest;
        var running = await Task.Run(() => AppLimiter.ListApps().ToList());
        if (request != _appsRequest) return;   // a newer refresh is on its way
        var chosen = new HashSet<string>(Options.AppsToClose, StringComparer.OrdinalIgnoreCase);
        var apps = running
            .Where(a => !Launchers.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        apps.AddRange(chosen.Where(p => !apps.Any(a => a.Path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new RunningApp(p, Path.GetFileNameWithoutExtension(p), 0, 0)));
        _appList.Children.Clear();
        foreach (var app in apps)
        {
            var box = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = app.Name + (app.Processes == 0 ? "   (not running)" : $"   (about {CleanupEngine.Size(app.WorkingSet)})"),
                    Foreground = Ui.Brush("TextBrush")
                },
                IsChecked = chosen.Contains(app.Path), ToolTip = app.Path, Margin = new Thickness(0, 2, 0, 2)
            };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true && MessageBox.Show(
                        $"Ask {app.Name} to close every time Game Mode starts, including automatically?", "Game Mode",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                { box.IsChecked = false; return; }
                Options.AppsToClose.RemoveAll(p => p.Equals(app.Path, StringComparison.OrdinalIgnoreCase));
                if (box.IsChecked == true) Options.AppsToClose.Add(app.Path);
                Save();
            };
            _appList.Children.Add(box);
        }
    }

    private void RestartElevated()
    {
        try
        {
            PCOptimizer.Core.SingleInstance.StartElevatedCopy();
            Application.Current.MainWindow?.Close();
        }
        catch (Win32Exception) { _saveNote.Text = "Administrator restart was cancelled."; }
    }
}
