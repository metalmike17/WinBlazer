using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.RegistryTools;
using PCOptimizer.Services.Tweaks;

namespace PCOptimizer.Modules;

public sealed class SystemTweaksModule : IQuickActionProvider
{
    public IEnumerable<QuickAction> QuickActions(AppContextServices services)
    {
        yield return new QuickAction("optimize", 2, () => "Optimize (Safe tweaks)...", confirm =>
            QuickRunners.OptimizeAsync(OptimizationScore.CreateTweakEngine(services.Settings),
                                       CleanupEngine.IsAdministrator(), confirm));
    }

    public string Id => "system-tweaks";
    public string Title => "System Tweaks";
    public string Icon => "";
    public string Description => "Reversible speed, network, power, visual and privacy tweaks";
    public int Order => 8;
    public FrameworkElement CreateView(AppContextServices services) => new SystemTweaksView(services);
}

public sealed class SystemTweaksView : UserControl, ISectionHost
{
    private string? _focus;
    public string? FocusedTweak { get; private set; }

    /// <summary>Scrolls to one tweak, once the list has been read.</summary>
    public void ShowSection(string section)
    {
        _focus = section;
        // Clear any category filter first, so the item is certain to be in the list.
        if (_filters.Children.OfType<RadioButton>().FirstOrDefault() is { } all) all.IsChecked = true;
        Show((TweakCategory?)null);
    }

    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly TweakEngine _engine;
    private readonly ListBox _list = new();
    private readonly WrapPanel _filters = new();
    private readonly StackPanel _repairs = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _oneClick = new() { Content = "One-Click Optimize..." };
    private readonly Button _reset = new() { Content = "Reset to Defaults..." };
    private readonly Button _refresh = new() { Content = "Refresh" };
    private readonly TextBlock _status = Ui.Text("Reading current settings...", 12);
    private Dictionary<string, TweakStatus> _states = new();
    private HashSet<string> _recorded = new();
    private TweakCategory? _category;
    private bool _busy, _loaded;

    public int LoadedCount => _states.Count;
    public int ShownCount => _list.Items.Count;
    public string StatusText => _status.Text;

    public SystemTweaksView(AppContextServices services)
    {
        var journal = Path.Combine(Path.GetDirectoryName(services.Settings.FilePath)!, "tweaks-journal.json");
        _engine = new TweakEngine(TweakCatalog.Create(),
            new TweakContext(new RegistryStore(), new WindowsTweakSystem()), journal,
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

        _oneClick.Role(ButtonRole.Main);
        _oneClick.Click += async (_, _) => await OneClickAsync();
        _reset.Click += async (_, _) => await ResetAsync();
        _refresh.Role(ButtonRole.Refresh);
        _refresh.Click += async (_, _) => await LoadAsync();
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var b in new[] { _oneClick, _reset, _refresh }) buttons.Children.Add(b);
        if (!_isAdmin)
        {
            var elevate = new Button { Content = "Restart as administrator" };
            elevate.Click += (_, _) => RestartElevated();
            buttons.Children.Add(elevate);
        }

        AddFilter("All", null, true);
        foreach (var category in Enum.GetValues<TweakCategory>())
            AddFilter(category == TweakCategory.Visual ? "Visual Effects" : category.ToString(), category, false);
        BuildRepairs();

        var top = Ui.Stack(Ui.Text("System Tweaks & Speed Optimizer", 24, weight: FontWeights.SemiBold),
            Ui.Text($"{_engine.Tweaks.Count} reversible tweaks. Many make little or no measurable speed difference. " +
                    "Change a few at a time." + (_isAdmin ? "" : " Some need administrator rights."), 12, "MutedBrush"),
            buttons, _filters, _status, _repairs);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_list);
        Content = dock;
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await LoadAsync(); } };
    }

    private void AddFilter(string text, TweakCategory? category, bool selected)
    {
        var radio = new RadioButton
        {
            Content = text, IsChecked = selected, GroupName = "tweak-category",
            Foreground = Ui.Brush("TextBrush"), Margin = new Thickness(0, 4, 16, 6)
        };
        radio.Checked += (_, _) => Show(category);
        _filters.Children.Add(radio);
    }

    public void Show(TweakCategory? category)
    {
        _category = category;
        _repairs.Visibility = category == TweakCategory.Network ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void BuildRepairs()
    {
        var flush = new Button { Content = "Flush DNS cache" };
        flush.Click += async (_, _) => await RepairAsync("DNS cache flushed.", "ipconfig.exe", "/flushdns");
        var winsock = new Button { Content = "Winsock reset...", IsEnabled = _isAdmin };
        winsock.Click += async (_, _) =>
        {
            if (Ask("Winsock reset CANNOT BE UNDONE by this app. It can break VPN, firewall, proxy or security " +
                    "software, which may need to be repaired or reinstalled. Restart is required.\n\nUse it only to " +
                    "fix broken networking. It does not make a working connection faster. Continue?", "Winsock reset"))
                await RepairAsync("Winsock was reset. Restart the PC.", "netsh.exe", "winsock", "reset");
        };
        var row = new WrapPanel();
        row.Children.Add(flush);
        row.Children.Add(winsock);
        _repairs.Children.Add(Ui.Text("One-time repairs (not tweaks; not part of One-Click or Reset). " +
                                      "Winsock reset needs administrator rights and cannot be undone.", 11, "MutedBrush"));
        _repairs.Children.Add(row);
        _repairs.Visibility = Visibility.Collapsed;
    }

    private async Task RepairAsync(string success, string program, params string[] arguments)
    {
        if (_busy) return;
        SetBusy(true);
        try { await Task.Run(() => WindowsTweakSystem.Run(program, arguments)); _status.Text = success; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _oneClick.IsEnabled = _reset.IsEnabled = _refresh.IsEnabled = _list.IsEnabled = _repairs.IsEnabled = !busy;
    }

    public async Task LoadAsync(string? message = null)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            _states = await Task.Run(() => _engine.AllStatuses());
            var journalProblem = "";
            try { _recorded = await Task.Run(() => _engine.Records().Select(r => r.Id).ToHashSet()); }
            catch (Exception ex) { journalProblem = " The saved-originals file could not be read: " + ex.Message; }
            Render();
            _status.Text = (message ?? $"{_states.Values.Count(s => s.State == TweakState.Applied)} of " +
                $"{_states.Count} are currently applied; {_recorded.Count} changed by this app.") + journalProblem;
        }
        catch (Exception ex) { _status.Text = "Could not read settings: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private void Render()
    {
        _list.Items.Clear();
        if (_states.Count == 0) return;
        foreach (var group in _engine.Tweaks.Where(t => _category == null || t.Category == _category)
                     .GroupBy(t => t.Category))
            foreach (var tweak in group)
            {
                var row = Row(tweak);
                _list.Items.Add(row);
                if (tweak.Id != _focus) continue;
                _focus = null;
                FocusedTweak = tweak.Id;
                if (row is Border border) (border.BorderBrush, border.BorderThickness) = (Ui.Brush("AccentBrush"), new Thickness(2));
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _list.ScrollIntoView(row));
            }
    }

    private FrameworkElement Row(Tweak tweak)
    {
        var status = _states[tweak.Id];
        var ours = _recorded.Contains(tweak.Id);
        var stateText = status.State switch
        {
            TweakState.Applied => ours ? "ON (changed by this app)" : "ALREADY SET (not by this app)",
            TweakState.Partial => "PARTLY SET",
            TweakState.Off => ours ? "OFF (changed back since this app set it)" : "OFF",
            _ => "NOT AVAILABLE"
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, FontWeight = FontWeights.SemiBold };
        var color = tweak.Risk == TweakRisk.Safe ? "GoodBrush"
            : tweak.Risk == TweakRisk.Moderate ? "WarnBrush" : "BadBrush";
        title.Inlines.Add(new System.Windows.Documents.Run(tweak.Name + "   ") { Foreground = Ui.Brush("TextBrush") });
        title.Inlines.Add(new System.Windows.Documents.Run(tweak.Risk.ToString().ToUpperInvariant())
        { Foreground = Ui.Brush(color), FontSize = 11 });

        var notes = new List<string> { tweak.Category == TweakCategory.Visual ? "Visual Effects" : tweak.Category.ToString() };
        if (tweak.NeedsAdmin) notes.Add("administrator");
        if (tweak.Effect != null) notes.Add("takes effect " + tweak.Effect);
        var details = Ui.Stack(title, Ui.Text(tweak.Description, 11, "MutedBrush"),
            Ui.Text($"{stateText}  •  {status.Detail}", 11, "AccentBrush"),
            Ui.Text(string.Join("  •  ", notes), 10, "MutedBrush"));
        grid.Children.Add(details);

        var on = status.State == TweakState.Applied;
        // A setting made elsewhere has no saved original, so the app does not guess one.
        var allowed = status.State != TweakState.Unavailable && (_isAdmin || !tweak.NeedsAdmin) && (!on || ours);
        var toggle = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("ToggleSwitch"), IsChecked = on, IsEnabled = allowed,
            Margin = new Thickness(12, 0, 4, 0),
            ToolTip = allowed ? "Turn the tweak on or off"
                : on && !ours ? "Already set before this app changed it, so there is no original to restore"
                : status.State == TweakState.Unavailable ? "Not available on this PC" : "Needs administrator rights"
        };
        ToolTipService.SetShowOnDisabled(toggle, true);
        System.Windows.Automation.AutomationProperties.SetName(toggle, tweak.Name);
        toggle.Click += async (_, _) => await ToggleAsync(tweak, toggle);
        var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (ours && !on)
        {
            var restore = new Button
            { Content = "Restore", FontSize = 11, IsEnabled = _isAdmin || !tweak.NeedsAdmin };
            restore.Click += async (_, _) =>
            {
                if (ConfirmDnsRestore(tweak))
                    await ChangeAsync(() => _engine.Revert(new[] { tweak.Id }, _isAdmin), "Restored");
            };
            side.Children.Add(restore);
        }
        side.Children.Add(toggle);
        Grid.SetColumn(side, 1);
        grid.Children.Add(side);

        var card = Ui.Card(grid);
        card.Padding = new Thickness(14, 10, 12, 4);
        card.Margin = new Thickness(0, 0, 8, 8);
        return card;
    }

    private static bool Ask(string text, string title) => MessageBox.Show(text, title, MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>True to go ahead. Only the DNS tweak asks, showing the addresses it would restore.</summary>
    private bool ConfirmDnsRestore(Tweak tweak)
    {
        if (tweak is not DnsTweak) return true;
        try
        {
            return _engine.DescribeSavedDns(new[] { tweak.Id }) is not { } saved ||
                   Ask($"Restore these DNS addresses?\n\n{saved}\n\nCheck that you recognize them.", "Restore DNS");
        }
        catch (Exception ex) { _status.Text = "The saved originals could not be read: " + ex.Message; return false; }
    }

    private async Task ToggleAsync(Tweak tweak, ToggleButton toggle)
    {
        var turnOn = toggle.IsChecked == true;
        if (_busy || (turnOn && tweak.Risk == TweakRisk.Risky &&
                      !Ask($"RISKY: {tweak.Name}\n\n{tweak.Description}\n\nApply it?", "Risky tweak")) ||
            (!turnOn && !ConfirmDnsRestore(tweak)))
        {
            toggle.IsChecked = !turnOn;
            return;
        }
        await ChangeAsync(() => turnOn ? _engine.Apply(new[] { tweak }, _isAdmin)
            : _engine.Revert(new[] { tweak.Id }, _isAdmin), turnOn ? "Applied" : "Restored");
    }

    private async Task OneClickAsync()
    {
        if (_busy) return;
        SetBusy(true);
        List<Tweak> targets;
        try { targets = await Task.Run(() => _engine.OneClickTargets(_isAdmin)); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        finally { SetBusy(false); }
        if (targets.Count == 0) { _status.Text = "All available Safe tweaks are already applied."; return; }
        if (!Ask($"Apply these {targets.Count} Safe tweaks? Some change how Windows looks or what it suggests. " +
                 "All can be restored with Reset to Defaults.\n\n" +
                 string.Join("\n", targets.Select(t => "• " + t.Name)), "One-Click Optimize")) return;
        await ChangeAsync(() => _engine.Apply(targets, _isAdmin), "Applied");
    }

    private async Task ResetAsync()
    {
        if (_busy) return;
        List<TweakRecord> records;
        try { records = await Task.Run(() => _engine.Records()); }
        catch (Exception ex) { _status.Text = "The saved-originals file could not be read: " + ex.Message; return; }
        if (_busy) return;
        if (records.Count == 0) { _status.Text = "This app has no tweak changes to restore."; return; }
        var lines = records.Select(r =>
        {
            var tweak = _engine.Tweaks.FirstOrDefault(t => t.Id == r.Id);
            // DNS addresses are shown so you can check what will be restored.
            return "• " + (tweak?.Name ?? r.Id) + (tweak is DnsTweak ? "\n" + DnsTweak.Describe(r.Original) : "");
        }).ToList();
        var shown = string.Join("\n", lines.Take(25)) + (lines.Count > 25 ? $"\n...and {lines.Count - 25} more" : "");
        if (!Ask($"Restore {records.Count} change(s) to what they were before this app changed them?\n\n{shown}\n\n" +
                 "This is not a factory reset. Settings you changed since are left alone.", "Reset to Defaults")) return;
        await ChangeAsync(() => _engine.Revert(null, _isAdmin), "Restored");
    }

    private async Task ChangeAsync(Func<TweakReport> change, string verb)
    {
        if (_busy) return;
        SetBusy(true);
        string message;
        try
        {
            var report = await Task.Run(change);
            message = $"{verb} {report.Changed} tweak(s). Some need a sign-out or restart; see each card.";
            if (report.Problems.Count > 0) message += "\n" + string.Join("\n", report.Problems.Take(6)) +
                (report.Problems.Count > 6 ? $"\n...and {report.Problems.Count - 6} more problems." : "");
        }
        catch (Exception ex) { message = "Change stopped: " + ex.Message; }
        finally { SetBusy(false); }
        ActivityLog.Post("Tweaks", $"{verb} tweaks", !message.StartsWith("Change stopped"), null, message);
        await LoadAsync(message);   // show the real state, including after a failure
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
