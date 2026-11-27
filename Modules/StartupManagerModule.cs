using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.StartupTools;

namespace PCOptimizer.Modules;

public sealed class StartupManagerModule : IModule
{
    public string Id => "startup-manager";
    public string Title => "Startup Manager";
    public string Icon => "";
    public string Description => "Control what starts with Windows";
    public int Order => 18;
    public FrameworkElement CreateView(AppContextServices services) => new StartupManagerView();
}

public sealed class StartupManagerView : UserControl
{
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly StartupCatalog _catalog;
    private readonly ScheduledTaskSource _tasks = new();
    private readonly StartupChanges _changes;
    private readonly ListBox _list = new();
    private readonly TextBox _search = new() { Width = 220, Padding = new Thickness(6, 4, 6, 4) };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly Button _disableAll = new() { Content = "Disable All..." };
    private readonly Button _undo = new() { Content = "Undo last change..." };
    private readonly TextBlock _status = Ui.Text("Loading...", 12);
    private readonly TextBlock _impactSource = Ui.Text("", 11, "MutedBrush");
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private List<StartupEntry> _entries = new();
    private bool _busy, _loaded;

    public int EntryCount => _entries.Count;
    public int ShownCount => _list.Items.Count;
    public IReadOnlyList<StartupEntry> Entries => _entries;
    public string ImpactSource => _impactSource.Text;

    public StartupManagerView()
    {
        _catalog = new StartupCatalog(tasks: _tasks);
        _changes = new StartupChanges(_catalog, protectedFolder: PCOptimizer.Core.ProtectedFolder.ForThisPc());

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Foreground = Ui.Brush("TextBrush");
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        // Rows are containers only; the switches inside them are what the user operates.
        var rowStyle = new Style(typeof(ListBoxItem));
        var template = new ControlTemplate(typeof(ListBoxItem))
        {
            VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
        };
        rowStyle.Setters.Add(new Setter(Control.TemplateProperty, template));
        rowStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        _list.ItemContainerStyle = rowStyle;

        _refresh.Role(ButtonRole.Refresh);
        _refresh.Click += async (_, _) => await LoadAsync();
        _disableAll.Click += async (_, _) => await DisableAllAsync();
        _undo.Click += async (_, _) => await UndoAsync();
        _search.TextChanged += (_, _) => Render();
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search startup entries");

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var b in new[] { _refresh, _disableAll, _undo }) buttons.Children.Add(b);
        buttons.Children.Add(new TextBlock
        { Text = "Search ", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Brush("MutedBrush") });
        Ui.AlignWithButtons(_search);
        buttons.Children.Add(_search);

        var top = Ui.Stack(Ui.Text("Startup Manager", 26, weight: FontWeights.SemiBold),
            Ui.Text("Run keys, Startup folders, and tasks that run at boot or sign-in. Services and Store app " +
                    "startup items are not included." +
                    (_isAdmin ? "" : " Without administrator rights some tasks may be missing and machine-wide " +
                                     "entries cannot be changed."), 12, "MutedBrush"),
            buttons, _status, _impactSource);
        if (!_isAdmin)
        {
            var elevate = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Left };
            elevate.Click += (_, _) => RestartElevated();
            top.Children.Add(elevate);
        }

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_list);
        Content = dock;
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await LoadAsync(); } };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _refresh.IsEnabled = _disableAll.IsEnabled = _undo.IsEnabled = _list.IsEnabled = !busy;
    }

    public async Task LoadAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _status.Text = "Reading startup entries...";
        try
        {
            var (entries, boot) = await Task.Run(() =>
            {
                var found = _catalog.Scan();
                var data = BootImpact.Read();
                return (found.Select(e => e with { Impact = BootImpact.For(e, data) }).ToList(), data);
            });
            _entries = entries;
            _impactSource.Text = "Boot impact: " + boot.Source;
            Render();
            var warnings = _catalog.Warnings.ToList();
            if (_tasks.Unreadable > 0) warnings.Add($"{_tasks.Unreadable} task locations could not be read.");
            _status.Text = $"{_entries.Count} entries, {_entries.Count(e => e.Enabled)} enabled, " +
                           $"{_entries.Count(e => e.Source == StartupSource.ScheduledTask)} scheduled tasks. " +
                           string.Join(" ", warnings);
        }
        catch (Exception ex) { _status.Text = "Could not read startup entries: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private void Render()
    {
        var query = _search.Text.Trim();
        _list.Items.Clear();
        foreach (var entry in _entries.Where(e => query == "" || new[] { e.Name, e.Publisher, e.Command, e.Location }
                     .Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))))
            _list.Items.Add(Row(entry));
    }

    private ImageSource? IconFor(string? path)
    {
        if (path == null) return null;
        if (_icons.TryGetValue(path, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                                                                BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or System.Runtime.InteropServices.ExternalException) { }
        return _icons[path] = image;
    }

    private FrameworkElement Row(StartupEntry entry)
    {
        var grid = new Grid();
        foreach (var width in new[] { new GridLength(44), new GridLength(1, GridUnitType.Star), GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        FrameworkElement icon = IconFor(entry.ProgramPath) is { } source
            ? new Image { Source = source, Width = 30, Height = 30 }
            : new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 26,
                              Foreground = Ui.Brush("MutedBrush") };
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);

        var badges = new List<string>();
        if (entry.Hidden) badges.Add("hidden task");
        if (entry.NeedsAdmin) badges.Add("all users");
        if (entry.BulkSkipReason != null) badges.Add(entry.BulkSkipReason);
        if (entry.LockReason != null) badges.Add(entry.LockReason);
        var command = Ui.Text(entry.Command, 11, "MutedBrush");
        command.FontFamily = new FontFamily("Consolas");
        var details = Ui.Stack(
            Ui.Text(entry.Name + (badges.Count > 0 ? $"   [{string.Join(" | ", badges)}]" : ""), 13,
                    weight: FontWeights.SemiBold),
            Ui.Text($"Publisher: {entry.Publisher}     Boot impact: {entry.Impact}", 11, "AccentBrush"),
            command, Ui.Text(entry.Location, 10, "MutedBrush"));
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        var toggle = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("ToggleSwitch"), IsChecked = entry.Enabled,
            IsEnabled = entry.CanChange(_isAdmin), Margin = new Thickness(12, 0, 4, 0),
            ToolTip = entry.LockReason ?? (entry.CanChange(_isAdmin) ? "On means it starts automatically"
                                                                     : "Needs administrator rights")
        };
        ToolTipService.SetShowOnDisabled(toggle, true);
        System.Windows.Automation.AutomationProperties.SetName(toggle, "Startup enabled: " + entry.Name);
        toggle.Click += async (_, _) => await ToggleAsync(entry, toggle);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        var card = Ui.Card(grid);
        card.Padding = new Thickness(12, 10, 12, 4);
        card.Margin = new Thickness(0, 0, 8, 8);
        return card;
    }

    private static bool Ask(string text, string title) => MessageBox.Show(text, title, MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task ToggleAsync(StartupEntry entry, ToggleButton toggle)
    {
        if (_busy) return;
        var enable = toggle.IsChecked == true;
        if (!enable && entry.BulkSkipReason != null &&
            !Ask($"{entry.Name} is a {entry.BulkSkipReason.ToLowerInvariant()}. Disable it anyway?", "Security item"))
        {
            toggle.IsChecked = true;
            return;
        }
        await ChangeAsync(() => _changes.Apply(new[] { entry }, enable, _isAdmin),
                          enable ? "Enabled" : "Disabled");
    }

    private async Task DisableAllAsync()
    {
        if (_busy) return;
        // The search box does not limit "All".
        var targets = StartupChanges.BulkTargets(_entries, _isAdmin);
        if (targets.Count == 0) { _status.Text = "There is nothing Disable All can switch off."; return; }
        var skipped = _entries.Count(e => e.Enabled) - targets.Count;
        var names = string.Join("\n", targets.Take(20).Select(e => "• " + e.Name));
        if (targets.Count > 20) names += $"\n...and {targets.Count - 20} more";
        if (!Ask($"Disable ALL {targets.Count} of these startup entries (not just search results)?\n\n{names}\n\n" +
                 $"{skipped} enabled entries will be left on (Windows tasks, security items, view-only, or needing " +
                 "rights).\n\nThe list may include antivirus, VPN, audio, driver, backup or sync tools. " +
                 "You can use Undo last change afterward.", "Disable All")) return;
        await ChangeAsync(() => _changes.Apply(targets, false, _isAdmin), "Disabled");
    }

    private async Task UndoAsync()
    {
        if (_busy) return;
        UndoBatch? last;
        try { last = await Task.Run(() => _changes.Load(_isAdmin).LastOrDefault()); }
        catch (Exception ex) { _status.Text = "The undo record could not be read: " + ex.Message; return; }
        if (_busy) return;
        if (last == null) { _status.Text = _changes.NothingToUndo(_isAdmin); return; }
        var lines = string.Join("\n", last.Items.Take(20)
            .Select(i => $"• {i.Name} → {(i.WasEnabled ? "On" : "Off")}"));
        if (last.Items.Count > 20) lines += $"\n...and {last.Items.Count - 20} more";
        if (!Ask($"Undo the change made {last.Time:g}?\n\n{lines}", "Undo")) return;
        await ChangeAsync(() => _changes.UndoLast(_isAdmin), "Restored");
    }

    private async Task ChangeAsync(Func<ChangeReport> change, string verb)
    {
        SetBusy(true);
        string message;
        try
        {
            var report = await Task.Run(change);
            message = $"{verb} {report.Changed} entr{(report.Changed == 1 ? "y" : "ies")}.";
            if (report.Problems.Count > 0) message += " " + string.Join(" ", report.Problems);
        }
        catch (Exception ex) { message = "Change stopped: " + ex.Message; }
        finally { SetBusy(false); }
        ActivityLog.Post("Startup", $"{verb} startup entries", !message.StartsWith("Change stopped"), null, message);
        await LoadAsync();   // show the real state, including after a failure
        _status.Text = message;
    }

    private void RestartElevated()
    {
        try
        {
            PCOptimizer.Core.SingleInstance.StartElevatedCopy();
            Application.Current.MainWindow?.Close();
        }
        catch (Win32Exception) { _status.Text = "Administrator restart was cancelled."; }
    }
}
