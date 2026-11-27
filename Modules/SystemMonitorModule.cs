using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.Monitor;

namespace PCOptimizer.Modules;

public sealed class SystemMonitorModule : IModule
{
    public string Id => "system-monitor";
    public string Title => "System Monitor";
    public string Icon => "";
    public string Description => "Live performance and process manager";
    public int Order => 2;

    public FrameworkElement CreateView(AppContextServices services) => new SystemMonitorView();
}

public sealed class SystemMonitorView : UserControl, ISectionHost
{
    public void ShowSection(string section) => (section == "processes" ? _processTab : _performanceTab).IsChecked = true;

    private static readonly Color Cyan = Color.FromRgb(0x00, 0xD4, 0xFF), Purple = Color.FromRgb(0xA7, 0x8B, 0xFA),
        Green = Color.FromRgb(0x34, 0xD3, 0x99), Amber = Color.FromRgb(0xFB, 0xBF, 0x24);

    private readonly ContentControl _page = new() { Focusable = false };
    private FrameworkElement _performance = null!, _processes = null!;
    private RadioButton _performanceTab = null!, _processTab = null!;
    private readonly TextBlock _status = Ui.Text("", 12, "AccentBrush");

    // Performance
    private readonly TextBlock _cpuText = Ui.Text("CPU", 14, weight: FontWeights.SemiBold);
    private readonly UniformGrid _coreGrid = new() { Columns = 4 };
    private readonly List<(TextBlock Label, LiveChart Chart)> _coreViews = new();
    private readonly TextBlock _gpuText = Ui.Text("GPU", 14, weight: FontWeights.SemiBold);
    private readonly TextBlock _vramText = Ui.Text("VRAM", 14, weight: FontWeights.SemiBold);
    private readonly LiveChart _gpu = Percent(Green), _vram = Percent(Purple);
    private readonly TextBlock _memoryText = Ui.Text("Memory", 14, weight: FontWeights.SemiBold);
    private readonly LiveChart _memory = Percent(Purple);
    private readonly StackPanel _memoryApps = new();
    private readonly TextBlock _diskText = Ui.Text("Disk", 14, weight: FontWeights.SemiBold);
    private readonly TextBlock _diskDetail = Ui.Text("", 11, "MutedBrush");
    private readonly LiveChart _disk = Rates(Cyan, Amber);
    private readonly TextBlock _netText = Ui.Text("Network", 14, weight: FontWeights.SemiBold);
    private readonly LiveChart _network = Rates(Cyan, Amber);

    // Process manager
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { Width = 180, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 8, 6) };
    private readonly ComboBox _sort = new() { MinWidth = 140, Margin = new Thickness(0, 0, 8, 6) };
    private readonly CheckBox _pause = new() { Content = "Pause list", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("TextBrush") };
    private readonly Dictionary<string, GroupRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProcessMonitor _monitor = new();
    private List<AppGroup> _groups = new();
    private CancellationTokenSource? _loop;
    private bool _ending;

    public int SampleCount { get; private set; }
    public HardwareSample? Latest { get; private set; }
    public IReadOnlyList<AppGroup> Groups => _groups;
    public int ShownGroups => _list.Items.Count;
    public int CoreCharts => _coreViews.Count;

    private static LiveChart Percent(Color color) => new(color) { Maximum = 100, Height = 90 };
    private static LiveChart Rates(params Color[] colors) => new(colors)
    { Height = 90, MinimumScale = 102400, ScaleText = v => MetricsService.Rate(v) };

    public SystemMonitorView()
    {
        _performance = PerformancePage();
        _processes = ProcessPage();
        _performanceTab = Tab("Performance", () => _performance);
        _processTab = Tab("Process Manager", () => _processes);
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        tabs.Children.Add(_performanceTab);
        tabs.Children.Add(_processTab);
        var top = Ui.Stack(Ui.Text("System Monitor", 26, weight: FontWeights.SemiBold), tabs, _status);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_page);
        Content = dock;
        _performanceTab.IsChecked = true;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => { _loop?.Cancel(); _loop = null; };
    }

    private RadioButton Tab(string name, Func<FrameworkElement> page)
    {
        var tab = new RadioButton
        { Content = name, GroupName = "monitor", Style = (Style)Application.Current.FindResource("PageTab"), Margin = new Thickness(0, 0, 8, 0) };
        tab.Checked += (_, _) => { _page.Content = page(); _status.Text = ""; RenderProcesses(true); };
        return tab;
    }

    public void OpenProcessManager() => _processTab.IsChecked = true;
    /// <summary>Used by the self-test to capture the lower charts.</summary>
    public void ScrollToEnd() => ((ScrollViewer)_performance).ScrollToEnd();

    private async void Start()
    {
        if (_loop != null) return;
        var stop = _loop = new CancellationTokenSource();
        // Each run owns its monitors, so a quick leave-and-return never shares them between threads.
        var hardware = await Task.Run(() => new HardwareMonitor());
        var processes = new ProcessMonitor();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var (sample, groups) = await Task.Run(() =>
                    (hardware.Sample(), processes.Group(processes.Sample())));
                if (stop.IsCancellationRequested) break;
                Latest = sample;
                _groups = groups;
                ShowPerformance(sample);
                RenderProcesses(false);
                SampleCount++;
                await Task.Delay((int)Math.Max(100, 1000 - watch.ElapsedMilliseconds), stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "Monitoring stopped: " + ex.Message; }
        finally
        {
            hardware.Dispose();
            stop.Dispose();
            if (_loop == stop) _loop = null;
        }
    }

    // ---- Performance ----

    private static Border Panel(params UIElement[] children)
    {
        var card = Ui.Card(Ui.Stack(children));
        card.Padding = new Thickness(12, 8, 12, 10);
        card.Margin = new Thickness(0, 0, 8, 8);
        return card;
    }

    private FrameworkElement PerformancePage()
    {
        var gpus = new UniformGrid { Columns = 2 };
        gpus.Children.Add(Panel(_gpuText, _gpu));
        gpus.Children.Add(Panel(_vramText, _vram));
        var io = new UniformGrid { Columns = 2 };
        io.Children.Add(Panel(_diskText, _disk, _diskDetail));
        io.Children.Add(Panel(_netText, _network,
            Ui.Text("Cyan: download. Amber: upload. Adapters with a gateway.", 11, "MutedBrush")));
        var stack = Ui.Stack(Panel(_cpuText, _coreGrid), gpus,
            Panel(_memoryText, _memory, Ui.Text("Apps using the most memory (private working set)", 12), _memoryApps),
            io, Ui.Text("Charts show the last minute, one reading per second, and pause when you leave this page.",
                        11, "MutedBrush"));
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void ShowPerformance(HardwareSample s)
    {
        if (_coreViews.Count != s.Cores.Length)
        {
            _coreViews.Clear();
            _coreGrid.Children.Clear();
            for (var i = 0; i < s.Cores.Length; i++)
            {
                var label = Ui.Text("", 11, "MutedBrush");
                var chart = new LiveChart(Cyan) { Maximum = 100, Height = 44 };
                _coreViews.Add((label, chart));
                _coreGrid.Children.Add(new Border
                {
                    BorderBrush = Ui.Brush("GridBrush"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6), Margin = new Thickness(3), Padding = new Thickness(4, 0, 4, 2),
                    Child = Ui.Stack(label, chart)
                });
            }
        }
        for (var i = 0; i < s.Cores.Length; i++)
        {
            var valid = !double.IsNaN(s.Cores[i]);
            _coreViews[i].Label.Text = $"Logical CPU {i}: {(valid ? $"{s.Cores[i]:0}%" : "n/a")}";
            _coreViews[i].Chart.Add(valid ? s.Cores[i] : null);
        }
        var cores = s.Cores.Length;
        Motion.Count(_cpuText, cores == 0 ? null : s.Cores.Where(c => !double.IsNaN(c)).DefaultIfEmpty().Average(),
            v => $"CPU: {v:0}% across {cores} logical processors", "CPU: per-core readings are unavailable");

        var gpu = s.Gpu;
        _gpuText.Text = gpu == null ? "GPU: unavailable (NVIDIA only)"
            : $"GPU: {(gpu.Load is { } load ? $"{load:0}%" : "n/a")}" +
              (gpu.Temperature is { } t ? $"   {t:0} °C" : "");
        _gpu.ToolTip = gpu?.Name;
        double? vram = gpu is { MemoryUsedMb: { } used, MemoryTotalMb: > 0 } ? 100 * used / gpu.MemoryTotalMb.Value : null;
        _vramText.Text = gpu is { MemoryUsedMb: { } u, MemoryTotalMb: { } total }
            ? $"VRAM: {u / 1024:0.0} of {total / 1024:0.0} GB" : "VRAM: unavailable";
        _gpu.Add(gpu?.Load);
        _vram.Add(vram);

        double? memory = s.MemoryTotal > 0 ? 100.0 * s.MemoryUsed / s.MemoryTotal : null;
        Motion.Count(_memoryText, memory == null ? null : s.MemoryUsed,
            v => $"Memory: {CleanupEngine.Size((long)v)} of {CleanupEngine.Size(s.MemoryTotal)} in use ({100 * v / s.MemoryTotal:0}%)",
            "Memory: unavailable");
        _memory.Add(memory);

        var topApps = _groups.OrderByDescending(g => g.Memory).Take(8).ToList();
        var largest = Math.Max(1, topApps.FirstOrDefault()?.Memory ?? 1);
        while (_memoryApps.Children.Count < topApps.Count)
        {
            var bar = new ProgressBar
            { Height = 6, Maximum = 100, Foreground = new SolidColorBrush(Purple), Background = Ui.Brush("GridBrush"), BorderThickness = new Thickness(0) };
            _memoryApps.Children.Add(Ui.Stack(Ui.Text("", 11), bar));
        }
        for (var i = 0; i < _memoryApps.Children.Count && i < topApps.Count; i++)
        {
            var row = (StackPanel)_memoryApps.Children[i];
            var app = topApps[i];
            ((TextBlock)row.Children[0]).Text =
                $"{app.Title}: {CleanupEngine.Size(app.Memory)}, {app.Processes.Count} process(es)";
            // The bar glides to its new length.
            ((ProgressBar)row.Children[1]).BeginAnimation(RangeBase.ValueProperty,
                new System.Windows.Media.Animation.DoubleAnimation(100.0 * app.Memory / largest, TimeSpan.FromMilliseconds(600)));
        }

        var read = s.Disks.Sum(d => d.Read);
        var write = s.Disks.Sum(d => d.Write);
        var haveDisk = s.Disks.Count > 0;
        _diskText.Text = haveDisk ? $"Disk: read {MetricsService.Rate(read)}, write {MetricsService.Rate(write)}"
            : "Disk: unavailable";
        _disk.Add(haveDisk ? read : null, haveDisk ? write : null);
        var busy = s.Disks.Where(d => d.Read + d.Write >= 1024).Select(d =>
            $"Disk {d.Name}: R {MetricsService.Rate(d.Read)}, W {MetricsService.Rate(d.Write)}").ToList();
        _diskDetail.Text = "Cyan: read. Amber: write." + (busy.Count > 0 ? "\n" + string.Join("\n", busy) : "");

        _netText.Text = s.Download is { } down && s.Upload is { } up
            ? $"Network: ↓ {MetricsService.Rate(down)}  ↑ {MetricsService.Rate(up)}" : "Network: measuring or not connected";
        _network.Add(s.Download, s.Upload);
    }

    // ---- Process manager ----

    private FrameworkElement ProcessPage()
    {
        foreach (var text in new[] { "Sort: Memory", "Sort: CPU", "Sort: I/O", "Sort: Name" })
            _sort.Items.Add(new ComboBoxItem { Content = text });
        _sort.SelectedIndex = 0;
        _sort.SelectionChanged += (_, _) => RenderProcesses(true);
        _search.TextChanged += (_, _) => RenderProcesses(true);
        _pause.Click += (_, _) => RenderProcesses(true);
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search processes");
        System.Windows.Automation.AutomationProperties.SetName(_sort, "Sort processes");

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Foreground = Ui.Brush("TextBrush");
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
        { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }));
        style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        _list.ItemContainerStyle = style;
        // Rows stop moving under the pointer, so the intended one is clicked.
        _list.MouseLeave += (_, _) => RenderProcesses(false);

        var controls = new WrapPanel();
        Ui.AlignWithButtons(_search, _sort);
        controls.Children.Add(_search);
        controls.Children.Add(_sort);
        controls.Children.Add(_pause);
        var header = Columns(Ui.Text("App", 11, "MutedBrush"), Ui.Text("CPU", 11, "MutedBrush"),
            Ui.Text("Memory", 11, "MutedBrush"), Ui.Text("I/O", 11, "MutedBrush"), new TextBlock());
        header.Margin = new Thickness(10, 0, 26, 2);
        var top = Ui.Stack(controls, Ui.Text(
            "Grouped by program file. I/O is all reading and writing by the process, including network and " +
            "devices, not only disk. The order stays still while your pointer is over the list. Windows, security, " +
            "service and unverifiable processes are locked.", 11, "MutedBrush"), header);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_list);
        return dock;
    }

    private static Grid Columns(params UIElement[] cells)
    {
        var grid = new Grid();
        foreach (var width in new[] { 0, 55, 80, 80, 86 })
            grid.ColumnDefinitions.Add(new ColumnDefinition
            { Width = width == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            if (cells[i] is FrameworkElement e) e.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    private static TextBlock Cell(double size = 12) => new()
    { FontSize = size, Foreground = Ui.Brush("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>One line: a group or a single process. Its controls are kept and only their text changes.</summary>
    private sealed class Line
    {
        public readonly TextBlock Name = Cell(), Cpu = Cell(), Memory = Cell(), Io = Cell();
        public readonly Button End = new() { Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Right };
        public readonly Grid Grid;

        public Line(UIElement first) => Grid = Columns(first, Cpu, Memory, Io, End);

        public void Show(double cpu, long memory, double io, string? lockReason)
        {
            Cpu.Text = $"{cpu:0.0}%";
            Memory.Text = CleanupEngine.Size(memory);
            Io.Text = MetricsService.Rate(io);
            End.Content = lockReason == null ? "End Task" : "\U0001F512 Locked";
            End.IsEnabled = lockReason == null;
            End.ToolTip = lockReason ?? "Force this to close";
            ToolTipService.SetShowOnDisabled(End, true);
        }
    }

    private sealed class GroupRow
    {
        public required Border Root;
        public required Line Header;
        public required Button Expander;
        public required StackPanel Children;
        public readonly Dictionary<(int, long), (Line Line, ProcessRow[] Current)> Lines = new();
        public required AppGroup Group;
    }

    private GroupRow CreateRow(AppGroup group)
    {
        var expander = new Button { Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(0, 0, 6, 0), MinWidth = 24 };
        var name = Cell(13);
        var title = new DockPanel();
        DockPanel.SetDock(expander, Dock.Left);
        title.Children.Add(expander);
        title.Children.Add(name);
        name.VerticalAlignment = VerticalAlignment.Center;
        var header = new Line(title);
        var children = new StackPanel { Margin = new Thickness(30, 2, 0, 0) };
        var row = new GroupRow
        {
            Expander = expander, Header = header, Children = children, Group = group,
            Root = new Border
            {
                Background = Ui.Brush("GlassBrush"), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 4),
                Child = Ui.Stack(header.Grid, children)
            }
        };
        header.Name.Tag = name;
        expander.Click += (_, _) =>
        {
            if (!_expanded.Remove(row.Group.Key)) _expanded.Add(row.Group.Key);
            Update(row);
        };
        header.End.Click += async (_, _) => await EndAsync(row.Group.Title, row.Group.Processes.ToList());
        return row;
    }

    private void Update(GroupRow row)
    {
        var g = row.Group;
        var name = (TextBlock)row.Header.Name.Tag;
        name.Text = g.Processes.Count > 1 ? $"{g.Title}  ({g.Processes.Count})" : g.Title;
        name.ToolTip = g.Processes[0].Path.Length > 0 ? g.Processes[0].Path : "Program file not readable";
        row.Header.Show(g.Cpu, g.Memory, g.IoPerSecond, g.LockReason);
        var open = _expanded.Contains(g.Key);
        row.Expander.Content = open ? "▾" : "▸";
        System.Windows.Automation.AutomationProperties.SetName(row.Expander, "Show processes of " + g.Title);
        row.Children.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        var wanted = open ? g.Processes.OrderBy(p => p.Id).ToList() : new List<ProcessRow>();
        var keys = wanted.Select(p => (p.Id, p.Created)).ToHashSet();
        foreach (var gone in row.Lines.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            row.Children.Children.Remove(row.Lines[gone].Line.Grid);
            row.Lines.Remove(gone);
        }
        foreach (var p in wanted)
        {
            if (!row.Lines.TryGetValue((p.Id, p.Created), out var entry))
            {
                var line = new Line(Cell(11));
                var current = new[] { p };
                line.End.Click += async (_, _) => await EndAsync($"{current[0].Name} (PID {current[0].Id})", new() { current[0] });
                row.Lines[(p.Id, p.Created)] = entry = (line, current);
                row.Children.Children.Add(line.Grid);
            }
            entry.Current[0] = p;
            ((TextBlock)entry.Line.Grid.Children[0]).Text = $"{p.Name}   PID {p.Id}";
            entry.Line.Show(p.Cpu, p.Memory, p.IoPerSecond, p.LockReason);
        }
    }

    private void RenderProcesses(bool force)
    {
        if (_processTab.IsChecked != true || (_pause.IsChecked == true && !force)) return;
        var query = _search.Text.Trim();
        var shown = _groups.Where(g => query == "" || g.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        shown = _sort.SelectedIndex switch
        {
            1 => shown.OrderByDescending(g => g.Cpu).ThenByDescending(g => g.Memory),
            2 => shown.OrderByDescending(g => g.IoPerSecond).ThenByDescending(g => g.Memory),
            3 => shown.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => shown.OrderByDescending(g => g.Memory)
        };
        var list = shown.ToList();
        var wanted = new List<Border>();
        foreach (var group in list)
        {
            if (!_rows.TryGetValue(group.Key, out var row)) _rows[group.Key] = row = CreateRow(group);
            row.Group = group;
            Update(row);
            wanted.Add(row.Root);
        }
        var alive = _groups.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _rows.Keys.Where(k => !alive.Contains(k)).ToList())
            if (!_list.IsMouseOver) _rows.Remove(gone);
        if (_list.IsMouseOver && !force)
        {
            // Keep positions; rows for closed apps are greyed until the pointer leaves.
            var live = wanted.ToHashSet();
            foreach (Border item in _list.Items) item.IsEnabled = live.Contains(item);
            return;
        }
        foreach (var border in wanted) border.IsEnabled = true;
        if (_list.Items.Cast<Border>().SequenceEqual(wanted)) return;
        _list.Items.Clear();
        foreach (var border in wanted) _list.Items.Add(border);
    }

    private async Task EndAsync(string title, List<ProcessRow> targets)
    {
        if (_ending || targets.Count == 0 || targets.Any(t => t.LockReason != null)) return;
        // The targets are fixed when the button is clicked, not when the question is answered.
        if (MessageBox.Show($"End {title}?\n\n{targets.Count} process(es) will be forced to close. Unsaved work will " +
                            "be lost, and the app may leave files incomplete. Close it normally if you can.",
                            "End Task", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;
        _ending = true;
        try
        {
            var results = await Task.Run(() => targets.Select(_monitor.End).ToList());
            var failed = results.Where(r => !r.Ok).Select(r => r.Message).Distinct().ToList();
            ActivityLog.Post("Processes", $"End Task: {title}", failed.Count == 0, null,
                $"Ended {results.Count(r => r.Ok)} of {targets.Count}. {string.Join(" ", failed.Take(4))}");
            _status.Text = $"Ended {results.Count(r => r.Ok)} of {targets.Count} process(es) of {title}." +
                           (failed.Count > 0 ? "\n" + string.Join("\n", failed.Take(4)) : "");
        }
        finally { _ending = false; }
    }
}
