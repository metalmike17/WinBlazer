using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.RepairTools;

namespace PCOptimizer.Modules;

public sealed class DiskRepairModule : IBackgroundModule
{
    private RepairRunner? _runner;

    public string Id => "disk-repair";
    public string Title => "Disk Repair";
    public string Icon => "";
    public string Description => "Windows repair tools, drive optimization and drive health";
    public int Order => 12;

    public IDisposable StartBackground(AppContextServices services)
    {
        var runner = _runner = new RepairRunner(Path.Combine(
            Path.GetDirectoryName(services.Settings.FilePath)!, "RepairLogs"));
        // Closing during a repair would cut off the running tool, so it is blocked.
        void Block(object? sender, CancelEventArgs e)
        {
            if (!runner.IsRunning) return;
            e.Cancel = true;
            MessageBox.Show($"{runner.Current?.Title} is still running. Wait for it to finish before closing " +
                            "WinBlazer. You can minimize the window.", "Repair in progress",
                            MessageBoxButton.OK, MessageBoxImage.Information);
        }
        // The window is assigned after background modules start.
        var window = (Window?)null;
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            window = Application.Current.MainWindow;
            if (window != null) window.Closing += Block;
        });
        return new Stopper(() => { if (window != null) window.Closing -= Block; });
    }

    public FrameworkElement CreateView(AppContextServices services) =>
        new DiskRepairView(_runner ?? throw new InvalidOperationException("Disk Repair is not running."));

    private sealed class Stopper(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}

/// <summary>The module page with its two sub-pages.</summary>
public sealed class DiskRepairView : UserControl, ISectionHost
{
    public void ShowSection(string section) => (section == "health" ? _healthTab : _toolsTab).IsChecked = true;

    private readonly ContentControl _host = new() { Focusable = false };
    private readonly RadioButton _toolsTab, _healthTab;

    public RepairToolsView Tools { get; }
    public DriveHealthView Health { get; } = new();

    public DiskRepairView(RepairRunner runner)
    {
        Tools = new RepairToolsView(runner);
        _toolsTab = Tab("Repair Tools", Tools);
        _healthTab = Tab("Drive Health", Health);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        tabs.Children.Add(_toolsTab);
        tabs.Children.Add(_healthTab);
        var top = Ui.Stack(Ui.Text("Disk Repair", 26, weight: FontWeights.SemiBold), tabs);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_host);
        Content = dock;
        _toolsTab.IsChecked = true;
    }

    public void ShowHealth() => _healthTab.IsChecked = true;

    private RadioButton Tab(string title, UIElement page)
    {
        var tab = new RadioButton
        {
            Content = title, GroupName = "repair-pages", Margin = new Thickness(0, 0, 8, 0), MinWidth = 120,
            Style = (Style)Application.Current.FindResource("PageTab")
        };
        tab.Checked += (_, _) => _host.Content = page;
        return tab;
    }
}

public sealed class RepairToolsView : UserControl
{
    private readonly RepairRunner _runner;
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly ComboBox _drives = new() { MinWidth = 420, Margin = new Thickness(0, 4, 0, 6) };
    private readonly Button _sfc = Make("Run SFC /scannow"), _dism = Make("Run DISM RestoreHealth"),
        _check = Make("Scan drive (read-only)"), _fix = Make("Fix drive errors..."),
        _analyze = Make("Analyze"), _optimize = Make("Optimize");
    private readonly Button _cancel = Make("Cancel");
    private readonly ProgressBar _bar = new() { Height = 10, Maximum = 100, Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBlock _state = Ui.Text("No tool is running.", 13, weight: FontWeights.SemiBold);
    private readonly TextBlock _live = Ui.Text("", 11, "MutedBrush");
    private readonly TextBlock _history = Ui.Text("", 12);
    private readonly TextBox _log = new()
    {
        IsReadOnly = true, Height = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 11,
        Background = Ui.Brush("ConsoleBrush"), Padding = new Thickness(8)
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _dirty = true;
    private string _shownLog = "";

    public int DriveCount => _drives.Items.Count;
    public int ToolsEnabled => new[] { _sfc, _dism, _check, _fix, _analyze, _optimize }.Count(b => b.IsEnabled);

    private static Button Make(string text) => new() { Content = text, Margin = new Thickness(0, 0, 8, 8) };

    private VolumeInfo? Selected => (_drives.SelectedItem as ComboBoxItem)?.Tag as VolumeInfo;

    public RepairToolsView(RepairRunner runner)
    {
        _runner = runner;
        _log.Foreground = Ui.Brush("TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(_drives, "Drive");
        System.Windows.Automation.AutomationProperties.SetName(_log, "Tool output");

        _sfc.Click += async (_, _) => await StartAsync(RepairJobs.Sfc,
            "SFC checks protected Windows files and repairs damaged ones. It can take a while and cannot be cancelled.");
        _dism.Click += async (_, _) => await StartAsync(RepairJobs.Dism,
            "DISM repairs the Windows component store. It may download files from Windows Update, can take a long " +
            "time (and appear stuck), and cannot be cancelled.");
        _check.Click += async (_, _) => await StartAsync(() => RepairJobs.CheckDisk(Selected!), null);
        _fix.Click += async (_, _) => await StartAsync(() => RepairJobs.FixDisk(Selected!),
            $"chkdsk /f on {Selected?.Letter}:\n\nBack up important files first, especially if the drive may be failing. " +
            (Selected?.IsSystem == true
                ? "This is the Windows drive, so the check will be SCHEDULED for the next restart. It can take a long time then."
                : "The drive will not be force-dismounted. If it is in use, the check is scheduled for restart where " +
                  "Windows offers that; otherwise it does not run.") + "\n\nIt cannot be cancelled from this app.");
        _analyze.Click += async (_, _) => await StartAsync(() => RepairJobs.Analyze(Selected!), null);
        _optimize.Click += async (_, _) => await StartAsync(() => RepairJobs.Optimize(Selected!),
            Selected?.Media == MediaKind.Hdd
                ? "Defragment this hard disk? It can take hours and cannot be cancelled from this app."
                : "Optimize this drive? SSDs receive TRIM only. It cannot be cancelled from this app.");
        _cancel.Role(ButtonRole.Cancel);
        _cancel.Click += (_, _) => _runner.Cancel();
        _drives.SelectionChanged += (_, _) => UpdateButtons();

        var page = new StackPanel();
        if (!_isAdmin)
        {
            var elevate = Make("Restart as administrator");
            elevate.HorizontalAlignment = HorizontalAlignment.Left;
            elevate.Click += (_, _) => RestartElevated();
            page.Children.Add(Card(Ui.Text("These Windows tools all need administrator rights.", 13), elevate));
        }
        page.Children.Add(Card(Ui.Text("Windows system files", 16, weight: FontWeights.SemiBold),
            Ui.Text("If SFC cannot fix everything, run DISM, restart, then run SFC again.", 11, "MutedBrush"),
            Row(_sfc, _dism)));
        var refresh = Make("Refresh drives");
        refresh.Role(ButtonRole.Refresh);
        refresh.Click += (_, _) => LoadDrives();
        page.Children.Add(Card(Ui.Text("Drive checks and optimization", 16, weight: FontWeights.SemiBold), _drives,
            Row(_check, _fix, _analyze, _optimize, refresh),
            Ui.Text("The read-only scan needs NTFS. SSDs get TRIM and are never defragmented; hard disks are " +
                    "defragmented; for unknown types Windows chooses. Windows already optimizes drives on a schedule.",
                    11, "MutedBrush")));
        page.Children.Add(Card(_state, _bar, _live, _cancel, _log, LogButtons()));
        page.Children.Add(Card(Ui.Text("Results since the app started", 16, weight: FontWeights.SemiBold), _history));
        Content = Ui.Scroll(page);

        // Output can arrive very quickly, so the screen is refreshed on a timer.
        _runner.Changed += () => _dirty = true;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { if (_drives.Items.Count == 0) LoadDrives(); _dirty = true; Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private static Border Card(params UIElement[] children)
    {
        var card = Ui.Card(Ui.Stack(children));
        card.Margin = new Thickness(0, 0, 12, 10);
        return card;
    }

    private static WrapPanel Row(params UIElement[] children)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private WrapPanel LogButtons()
    {
        var open = Make("Open logs folder");
        open.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(_runner.LogFolder);
                // Through Explorer, so nothing inherits administrator rights.
                Process.Start(new ProcessStartInfo(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    $"\"{_runner.LogFolder}\""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            { _live.Text = ex.Message; }
        };
        return Row(open);
    }

    private void LoadDrives()
    {
        var letter = Selected?.Letter;
        _drives.Items.Clear();
        foreach (var volume in DriveCatalog.Volumes())
        {
            var item = new ComboBoxItem { Content = volume.Describe(), Tag = volume };
            _drives.Items.Add(item);
            if (volume.Letter == letter || (letter == null && volume.IsSystem)) _drives.SelectedItem = item;
        }
        if (_drives.SelectedItem == null && _drives.Items.Count > 0) _drives.SelectedIndex = 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var free = _isAdmin && !_runner.IsRunning;
        var volume = Selected;
        _sfc.IsEnabled = _dism.IsEnabled = free;
        _check.IsEnabled = free && volume != null && RepairJobs.SupportsOnlineCheck(volume);
        _fix.IsEnabled = free && volume != null && RepairJobs.SupportsFix(volume);
        _analyze.IsEnabled = _optimize.IsEnabled = free && volume != null;
        _drives.IsEnabled = !_runner.IsRunning;
        _optimize.Content = volume?.Media == MediaKind.Ssd ? "Run TRIM"
            : volume?.Media == MediaKind.Hdd ? "Defragment" : "Optimize";
        _cancel.Visibility = _runner.Current?.CanCancel == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh()
    {
        if (_runner.IsRunning)
        {
            var elapsed = DateTime.Now - _runner.Started;
            _state.Text = $"{_runner.Current!.Title}  •  {(_runner.Percent is { } p ? $"{p:N0}%" : "working")}  " +
                          $"•  {elapsed:hh\\:mm\\:ss}";
        }
        if (!_dirty) return;
        _dirty = false;
        _bar.IsIndeterminate = _runner.IsRunning && _runner.Percent == null;
        _bar.Value = _runner.Percent ?? 0;
        _live.Text = _runner.IsRunning ? _runner.LastLine : _runner.LogPath != null ? "Log: " + _runner.LogPath : "";
        var text = _runner.LogText;
        if (text != _shownLog) { _log.Text = _shownLog = text; _log.ScrollToEnd(); }
        _history.Text = _runner.Results.Count == 0 ? "Nothing has been run yet."
            : string.Join("\n", _runner.Results.OrderByDescending(r => r.Value.When).Select(r =>
                $"{r.Value.When:t}  {r.Key}  —  {r.Value.Result.Verdict.ToString().ToUpperInvariant()}: " +
                r.Value.Result.Summary));
        UpdateButtons();
    }

    private async Task StartAsync(Func<RepairJob> create, string? question)
    {
        if (_runner.IsRunning) return;
        if (question != null && MessageBox.Show(question + "\n\nStart now?", "Disk Repair", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            var job = create();
            var run = _runner.RunAsync(job);
            _dirty = true;
            Refresh();
            var result = await run;
            _state.Text = $"{job.Title}: {result.Verdict.ToString().ToUpperInvariant()}. {result.Summary}";
            ActivityLog.Post("Repair", job.Title, result.Verdict.ToString() is not ("Failed" or "Cancelled"), null, _state.Text);
        }
        catch (Exception ex) { _state.Text = "Could not run the tool: " + ex.Message; }
        _dirty = true;
        Refresh();
    }

    private void RestartElevated()
    {
        try
        {
            PCOptimizer.Core.SingleInstance.StartElevatedCopy();
            Application.Current.MainWindow?.Close();
        }
        catch (Win32Exception) { _state.Text = "Administrator restart was cancelled."; }
    }
}

public sealed class DriveHealthView : UserControl
{
    private readonly StackPanel _cards = new();
    private readonly Button _refresh = new() { Content = "Read drives again", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = Ui.Text("", 12);
    private bool _loaded, _busy;

    public int DriveCount { get; private set; }
    public bool HasLoaded { get; private set; }

    public DriveHealthView()
    {
        _refresh.Role(ButtonRole.Refresh);
        _refresh.Click += async (_, _) => await LoadAsync();
        var note = "The score summarizes warning signs the drive reports. It is not a prediction: a drive can fail " +
                   "with a good score, so keep backups." + (CleanupEngine.IsAdministrator() ? ""
                       : " Without administrator rights some drives show limited or no SMART data.");
        var dock = new DockPanel();
        var top = Ui.Stack(Ui.Text(note, 12, "MutedBrush"), _refresh, _status);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(Ui.Scroll(_cards));
        Content = dock;
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await LoadAsync(); } };
    }

    public async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        _refresh.IsEnabled = false;
        _status.Text = "Reading drive health...";
        try
        {
            var (drives, volumes) = await Task.Run(() => (SmartReader.ReadAll(), DriveCatalog.Volumes()));
            _cards.Children.Clear();
            foreach (var drive in drives)
                _cards.Children.Add(Card(drive, volumes.Where(v => v.DiskNumber == drive.Number)));
            DriveCount = drives.Count;
            _status.Text = drives.Count == 0 ? "No physical drives could be queried."
                : $"{drives.Count} drive(s), read at {DateTime.Now:T}.";
        }
        catch (Exception ex) { _status.Text = "Could not read drives: " + ex.Message; }
        finally { _busy = false; _refresh.IsEnabled = true; HasLoaded = true; }
    }

    private static FrameworkElement Card(DriveHealth drive, IEnumerable<VolumeInfo> volumes)
    {
        var health = SmartParser.Score(drive);
        var letters = string.Join(", ", volumes.Select(v => v.Letter + ":"));
        var facts = new List<string>
        {
            "Temperature: " + (drive.Temperature is { } t ? $"{t:N0} °C" : "not reported"),
            "Reallocated sectors: " + (drive.ReallocatedSectors is { } r ? r.ToString("N0")
                : drive.Nvme != null ? "not an NVMe measure" : "not read"),
            "Drive's failure warning: " + (drive.PredictsFailure switch
            { true => "YES — back up now", false => "none", _ => "not read" })
        };
        var summary = Ui.Stack(
            Ui.Text($"Disk {drive.Number}: {drive.Model}", 15, weight: FontWeights.SemiBold),
            Ui.Text($"{drive.Bus}{(letters == "" ? "" : "  •  " + letters)}  •  {health.Label}", 12, "AccentBrush"),
            Ui.Text(string.Join("\n", facts), 12),
            Ui.Text(string.Join("\n", health.Reasons), 11, "MutedBrush"));
        var gauge = new HealthGauge(health.Score) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0) };
        var header = new DockPanel();
        DockPanel.SetDock(gauge, Dock.Left);
        header.Children.Add(gauge);
        header.Children.Add(summary);

        var content = Ui.Stack(header);
        if (drive.Nvme is { } n)
            content.Children.Add(Details("NVMe health log", new[]
            {
                ("Critical warning", n.CriticalWarning == 0 ? "none" : $"0x{n.CriticalWarning:X2}"),
                ("Spare capacity", $"{n.Spare}% (threshold {n.SpareThreshold}%)"),
                ("Rated endurance used", $"{n.PercentUsed}%"),
                ("Media errors", n.MediaErrors.ToString("N0")),
                ("Error log entries", n.ErrorLogEntries.ToString("N0")),
                ("Data read", CleanupEngine.Size((long)Math.Min(n.BytesRead, long.MaxValue))),
                ("Data written", CleanupEngine.Size((long)Math.Min(n.BytesWritten, long.MaxValue))),
                ("Power-on hours", n.PowerOnHours.ToString("N0")),
                ("Power cycles", n.PowerCycles.ToString("N0")),
                ("Unsafe shutdowns", n.UnsafeShutdowns.ToString("N0"))
            }.Select(p => $"{p.Item1,-24}{p.Item2}")));
        if (drive.Attributes.Count > 0)
            content.Children.Add(Details("S.M.A.R.T. attributes", drive.Attributes.Select(a =>
                $"{a.Id,3}  {a.Name,-32} value {a.Current,3}  worst {a.Worst,3}  " +
                $"limit {a.Threshold?.ToString() ?? "-",3}  raw {a.Raw}" + (a.BelowThreshold ? "  AT LIMIT" : ""))
                .Prepend("Raw values are vendor specific.")));
        var card = Ui.Card(content);
        card.Margin = new Thickness(0, 0, 12, 10);
        return card;
    }

    private static Expander Details(string title, IEnumerable<string> lines)
    {
        var text = Ui.Text(string.Join("\n", lines), 11);
        text.FontFamily = new FontFamily("Consolas");
        return new Expander { Header = title, Content = text, Foreground = Ui.Brush("TextBrush") };
    }
}
