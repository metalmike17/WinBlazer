using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.ShortcutTools;

namespace PCOptimizer.Modules;

public sealed class ShortcutFixerModule : IScannableModule
{
    public string Id => "shortcut-fixer";
    public string Title => "Shortcut Fixer";
    public string Icon => "";
    public string Description => "Find and repair broken desktop and Start menu shortcuts";
    public int Order => 28;

    public FrameworkElement CreateView(AppContextServices services) => new ShortcutFixerView(services);

    public Task<string> ScanAsync(CleanupPreferences preferences, CancellationToken token) => Task.Run(() =>
    {
        var scan = ShortcutScanner.Scan(ShortcutScanOptions.ForThisPc(), token);
        return $"{scan.Findings.Count} shortcut(s) with problems out of {scan.Checked} checked. Nothing was changed.";
    }, token);
}

public sealed class ShortcutFixerView : UserControl
{
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly ShortcutRepair _repair;
    private readonly ListBox _list = new();
    private readonly ComboBox _filter = new() { MinWidth = 190, Margin = new Thickness(0, 0, 8, 8) };
    private readonly Button _scanButton = Make("Scan shortcuts"), _repairAll = Make("Repair all clear matches..."),
        _removeGone = Make("Remove possibly uninstalled..."), _undo = Make("Undo last change...");
    private readonly TextBlock _status = Ui.Text("Not scanned yet. Scanning changes nothing.", 12);
    private readonly Expander _unverified = new() { Visibility = Visibility.Collapsed };
    private ShortcutScan? _scan;
    private bool _busy, _loaded;

    public bool HasScanned => _scan != null;
    public ShortcutScan? LastScan => _scan;
    public int ShownCount => _list.Items.Count;

    private static Button Make(string text) => new() { Content = text, Margin = new Thickness(0, 0, 8, 8) };

    public ShortcutFixerView(AppContextServices services)
    {
        _repair = new ShortcutRepair(ShortcutScanOptions.DefaultRoots(),
            Path.Combine(Path.GetDirectoryName(services.Settings.FilePath)!, "ShortcutBackups"),
            PCOptimizer.Core.ProtectedFolder.ForThisPc());

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Foreground = Ui.Brush("TextBrush");
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var rowStyle = new Style(typeof(ListBoxItem));
        rowStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem))
        { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }));
        rowStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        _list.ItemContainerStyle = rowStyle;

        foreach (var text in new[]
                 { "All problems", "Broken target", "Arguments", "Start in folder", "Possibly uninstalled" })
            _filter.Items.Add(new ComboBoxItem { Content = text });
        _filter.SelectedIndex = 0;
        Ui.AlignWithButtons(_filter);
        _filter.SelectionChanged += (_, _) => Render();
        System.Windows.Automation.AutomationProperties.SetName(_filter, "Problem filter");

        _scanButton.Role(ButtonRole.Refresh);
        _scanButton.Click += async (_, _) => await ScanAsync();
        _repairAll.Click += async (_, _) => await RepairAllAsync();
        _removeGone.Click += async (_, _) => await RemoveUninstalledAsync();
        _undo.Click += async (_, _) => await UndoAsync();
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var control in new UIElement[] { _scanButton, _repairAll, _removeGone, _undo, _filter })
            buttons.Children.Add(control);
        if (!_isAdmin)
        {
            var elevate = Make("Restart as administrator");
            elevate.Click += (_, _) => RestartElevated();
            buttons.Children.Add(elevate);
        }
        _unverified.Foreground = Ui.Brush("MutedBrush");

        var top = Ui.Stack(Ui.Text("Shortcut Fixer", 26, weight: FontWeights.SemiBold),
            Ui.Text("Checks .lnk shortcuts on both desktops and Start menus. Each one is backed up before it is " +
                    "repaired or removed." + (_isAdmin ? "" : " Shortcuts shared by all users need administrator rights."),
                    12, "MutedBrush"),
            buttons, _status, _unverified);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_list);
        Content = dock;
        UpdateButtons();
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await ScanAsync(); } };
    }

    private void UpdateButtons()
    {
        _scanButton.IsEnabled = _undo.IsEnabled = _list.IsEnabled = !_busy;
        var clear = _scan?.Findings.Count(f => f.CanAutoRepair && Allowed(f)) ?? 0;
        var gone = _scan?.Findings.Count(f => f.Has(ProblemKind.Uninstalled) && Allowed(f)) ?? 0;
        _repairAll.Content = $"Repair all clear matches ({clear})...";
        _removeGone.Content = $"Remove possibly uninstalled ({gone})...";
        _repairAll.IsEnabled = !_busy && clear > 0;
        _removeGone.IsEnabled = !_busy && gone > 0;
    }

    private bool Allowed(ShortcutFinding finding) => _isAdmin || !finding.Root.NeedsAdmin;

    public async Task ScanAsync(string? message = null)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        _status.Text = "Scanning shortcuts and looking for moved files...";
        try
        {
            _scan = await Task.Run(() => ShortcutScanner.Scan(ShortcutScanOptions.ForThisPc()));
            _status.Text = (message == null ? "" : message + "\n") +
                $"{_scan.Findings.Count} shortcut(s) with problems out of {_scan.Checked} checked.";
            _unverified.Visibility = _scan.Unverified.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _unverified.Header = $"Could not check {_scan.Unverified.Count} item(s) (not treated as broken)";
            var details = Ui.Text(string.Join("\n", _scan.Unverified.Take(100)), 11, "MutedBrush");
            _unverified.Content = new ScrollViewer { Content = details, MaxHeight = 120 };
        }
        catch (Exception ex) { _status.Text = "The scan failed: " + ex.Message; }
        finally { _busy = false; Render(); UpdateButtons(); }
    }

    private void Render()
    {
        _list.Items.Clear();
        if (_scan == null) return;
        var index = _filter.SelectedIndex;
        foreach (var finding in _scan.Findings.Where(f => index switch
                 {
                     1 => f.Has(ProblemKind.MissingTarget),
                     2 => f.Has(ProblemKind.MissingArguments) || f.Has(ProblemKind.MissingArgumentFile),
                     3 => f.Has(ProblemKind.BadWorkingDirectory),
                     4 => f.Has(ProblemKind.Uninstalled),
                     _ => true
                 }))
            _list.Items.Add(Row(finding));
        if (_list.Items.Count == 0) _list.Items.Add(Ui.Text("No shortcuts to show.", 13, "MutedBrush"));
    }

    private FrameworkElement Row(ShortcutFinding finding)
    {
        var content = Ui.Stack(
            Ui.Text(finding.Name, 14, weight: FontWeights.SemiBold),
            Ui.Text($"{finding.Root.Label}: {finding.Path}", 10, "MutedBrush"));
        foreach (var problem in finding.Problems)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
            void Cell(string text, int row, int column, string brush)
            {
                var block = Ui.Text(text, 11, brush);
                block.Margin = new Thickness(0);
                Grid.SetRow(block, row);
                Grid.SetColumn(block, column);
                grid.Children.Add(block);
            }
            Cell(problem.Kind == ProblemKind.MissingTarget ? "Expected target" : "Expected", 0, 0, "MutedBrush");
            Cell(problem.Expected, 0, 1, "TextBrush");
            Cell("Actual state", 1, 0, "MutedBrush");
            Cell(problem.Actual, 1, 1, "AccentBrush");
            content.Children.Add(grid);
        }
        if (finding.Link.Arguments.Length > 0)
            content.Children.Add(Ui.Text("Current arguments: " + finding.Link.Arguments, 10, "MutedBrush"));

        var actions = new WrapPanel();
        var allowed = Allowed(finding);
        ComboBox? choices = null;
        if (finding.Has(ProblemKind.MissingTarget) && finding.Candidates.Count > 0)
        {
            content.Children.Add(Ui.Text(finding.ConfidentTarget != null ? "Likely new location:"
                : "Possible matches (same file name; check before choosing):", 11, "MutedBrush"));
            choices = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var candidate in finding.Candidates)
                choices.Items.Add(new ComboBoxItem { Content = candidate.Path, Tag = candidate.Path });
            if (finding.ConfidentTarget != null)
                choices.SelectedIndex = finding.Candidates.FindIndex(c => c.Path == finding.ConfidentTarget);
            System.Windows.Automation.AutomationProperties.SetName(choices, "New target for " + finding.Name);
            content.Children.Add(choices);
        }

        var canRepair = allowed && !finding.Link.Advertised &&
            (choices != null || finding.SuggestedArguments != null ||
             (finding.Has(ProblemKind.BadWorkingDirectory) && !finding.Has(ProblemKind.MissingTarget)));
        if (canRepair)
        {
            var repair = Make("Repair");
            repair.Click += async (_, _) =>
            {
                var picked = (choices?.SelectedItem as ComboBoxItem)?.Tag as string;
                if (choices != null && picked == null) { _status.Text = "Choose the new target first."; return; }
                // A clear match repairs in one click; anything else is confirmed first.
                if (picked != null && picked != finding.ConfidentTarget &&
                    !Ask($"Point \"{finding.Name}\" at this file?\n\n{picked}\n\nOnly the file name is known to match.")) return;
                if (picked == null && finding.SuggestedArguments != null &&
                    !Ask($"Copy these arguments from the other \"{finding.Name}\" shortcut?\n\n{finding.SuggestedArguments}")) return;
                await ApplyAsync(() => _repair.Repair(new[] { (finding, picked, finding.SuggestedArguments) }, _isAdmin), "Repaired");
            };
            actions.Children.Add(repair);
        }
        if (finding.Has(ProblemKind.MissingTarget) && !finding.Link.Advertised && allowed)
        {
            var browse = Make("Choose file...");
            browse.Click += async (_, _) =>
            {
                var dialog = new OpenFileDialog { Title = "New target for " + finding.Name, Filter = "All files|*.*", DereferenceLinks = false };
                if (dialog.ShowDialog() != true || !Ask($"Point \"{finding.Name}\" at:\n\n{dialog.FileName}")) return;
                await ApplyAsync(() => _repair.Repair(new[] { (finding, (string?)dialog.FileName, (string?)null) }, _isAdmin), "Repaired");
            };
            actions.Children.Add(browse);
        }
        var remove = Make("Remove...");
        remove.IsEnabled = allowed;
        remove.Click += async (_, _) =>
        {
            if (!Ask($"Remove the shortcut \"{finding.Name}\"?\n\n{finding.Path}\n\nOnly the shortcut is removed. " +
                     "A backup is kept for Undo.")) return;
            await ApplyAsync(() => _repair.Remove(new[] { finding }, _isAdmin), "Removed");
        };
        actions.Children.Add(remove);
        if (!allowed) actions.Children.Add(Ui.Text("Needs administrator rights.", 11, "MutedBrush"));
        content.Children.Add(actions);

        var card = Ui.Card(content);
        card.Padding = new Thickness(14, 10, 14, 4);
        card.Margin = new Thickness(0, 0, 8, 8);
        return card;
    }

    private static bool Ask(string text) => MessageBox.Show(text, "Shortcut Fixer", MessageBoxButton.YesNo,
        MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private static string List(IEnumerable<string> lines, int count) =>
        string.Join("\n", lines.Take(15)) + (count > 15 ? $"\n...and {count - 15} more" : "");

    private async Task RepairAllAsync()
    {
        var targets = _scan!.Findings.Where(f => f.CanAutoRepair && Allowed(f)).ToList();
        var lines = targets.Select(f => $"• {f.Name} → {f.ConfidentTarget ?? "fix the Start in folder"}");
        if (!Ask($"Repair these {targets.Count} shortcuts (all of them, regardless of the filter)?\n\n" +
                 List(lines, targets.Count))) return;
        await ApplyAsync(() => _repair.Repair(
            targets.Select(f => (f, f.ConfidentTarget, (string?)null)).ToList(), _isAdmin), "Repaired");
    }

    private async Task RemoveUninstalledAsync()
    {
        var targets = _scan!.Findings.Where(f => f.Has(ProblemKind.Uninstalled) && Allowed(f)).ToList();
        if (!Ask($"Remove these {targets.Count} shortcuts? \"Possibly uninstalled\" is a judgment, so check the names." +
                 $"\n\n{List(targets.Select(f => "• " + f.Name), targets.Count)}\n\nBackups are kept for Undo.")) return;
        await ApplyAsync(() => _repair.Remove(targets, _isAdmin), "Removed");
    }

    private async Task UndoAsync()
    {
        ShortcutBatch? last;
        try { last = await Task.Run(() => _repair.History(_isAdmin).LastOrDefault()); }
        catch (Exception ex) { _status.Text = "The backup history could not be read: " + ex.Message; return; }
        if (_busy) return;
        if (last == null)
        {
            // Explains where the history is, if it is one this window may not use. Nothing is changed.
            try { _status.Text = (await Task.Run(() => _repair.UndoLast(_isAdmin))).Problems.FirstOrDefault() ?? ""; }
            catch (Exception ex) { _status.Text = "The backup history could not be read: " + ex.Message; }
            return;
        }
        // Full paths are shown so you can see exactly what will be written.
        if (!Ask($"Undo \"{last.Action}\" from {last.Time:g}?\n\n{List(last.Items.Select(i => i.Path), last.Items.Count)}")) return;
        await ApplyAsync(() => _repair.UndoLast(_isAdmin), "Restored");
    }

    private async Task ApplyAsync(Func<ShortcutReport> change, string verb)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        string message;
        try
        {
            var report = await Task.Run(change);
            message = $"{verb} {report.Changed} shortcut(s).";
            if (report.Problems.Count > 0) message += " " + string.Join(" ", report.Problems.Take(5));
        }
        catch (Exception ex) { message = "Change stopped: " + ex.Message; }
        finally { _busy = false; }
        ActivityLog.Post("Shortcuts", $"{verb} shortcuts", !message.StartsWith("Change stopped"), null, message);
        await ScanAsync(message);
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
