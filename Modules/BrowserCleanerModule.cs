using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;

namespace PCOptimizer.Modules;

public sealed class BrowserCleanerModule : IModule
{
    public string Id => "browser-cleaner";
    public string Title => "Browser Cleaner";
    public string Icon => "";
    public string Description => "Clear browser data and activity traces";
    public int Order => 20;
    public FrameworkElement CreateView(AppContextServices services) => new BrowserCleanerView();
}

public sealed class BrowserCleanerView : UserControl, ISectionHost
{
    public void ShowSection(string section) => SetEraserMode(section == "eraser");

    public const string CookieWarning =
        "Clearing cookies will sign you out of most websites in the selected browsers.\n\n" +
        "Make sure you know your passwords and have access to your two-step sign-in method. " +
        "Sites may also forget preferences.\n\nContinue with clearing cookies?";

    private readonly List<(Browser Browser, BrowserData Type, ToggleButton Switch)> _browserSwitches = new();
    private readonly List<(TraceKind Kind, ToggleButton Switch, TextBlock Count)> _traceSwitches = new();
    private readonly WindowsTraces _traces = new();
    private readonly RadioButton _browserTab, _eraserTab;
    private readonly StackPanel _tracePanel;
    private readonly Button _clear = new() { IsEnabled = false };
    private readonly TextBlock _status = Ui.Text("", 12, "MutedBrush");
    private readonly TextBlock _cookieNote = Ui.Text(
        "⚠ Cookies are selected: you will be signed out of websites.", 13, "AccentBrush", FontWeights.SemiBold);
    private readonly TextBlock _report = Ui.Text("", 12);
    private readonly Border _reportCard;
    private bool _busy;

    public int BrowserSwitchCount => _browserSwitches.Count;
    public int SelectedCount => _browserSwitches.Count(s => s.Switch.IsChecked == true) +
                                _traceSwitches.Count(s => s.Switch.IsChecked == true);
    public bool EraserMode => _eraserTab.IsChecked == true;

    public BrowserCleanerView()
    {
        _browserTab = ModeTab("Browser data");
        _eraserTab = ModeTab("Tracks Eraser");
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        modes.Children.Add(_browserTab);
        modes.Children.Add(_eraserTab);

        var grid = new UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Top };
        _ = LoadBrowsersAsync(grid);

        _tracePanel = BuildTracePanel();
        _reportCard = Ui.Card(_report);
        _reportCard.Visibility = Visibility.Collapsed;
        _reportCard.Margin = new Thickness(0, 0, 12, 10);
        _cookieNote.Visibility = Visibility.Collapsed;
        _clear.Role(ButtonRole.Main);
        _clear.Click += async (_, _) => await ConfirmAndClearAsync();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        buttons.Children.Add(_clear);
        var top = Ui.Stack(Ui.Text("Browser Data Cleaner", 26, weight: FontWeights.SemiBold), modes, buttons,
                           _status, _cookieNote);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(Ui.Scroll(Ui.Stack(_reportCard, _tracePanel, grid,
            Ui.Text("Passwords, bookmarks, extensions and autofill are not removed. Sync may bring cleared data back. " +
                    "This is ordinary deletion, not forensic erasure.", 11, "MutedBrush"))));
        Content = dock;
        _browserTab.IsChecked = true;
    }

    private async Task LoadBrowsersAsync(Panel grid)
    {
        try
        {
            foreach (var browser in await Task.Run(() => BrowserCleaner.Detect().ToList()))
                grid.Children.Add(BrowserCard(browser));
        }
        catch (Exception ex) { _status.Text = "Could not look for browsers: " + ex.Message; }
    }

    private RadioButton ModeTab(string title)
    {
        var tab = new RadioButton
        {
            Content = title, GroupName = "browser-modes", MinWidth = 130, Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)Application.Current.FindResource("PageTab")
        };
        tab.Checked += (_, _) => ModeChanged();
        return tab;
    }

    public void SetEraserMode(bool on) => (on ? _eraserTab : _browserTab).IsChecked = true;

    private void ModeChanged()
    {
        if (_tracePanel == null) return;
        _tracePanel.Visibility = EraserMode ? Visibility.Visible : Visibility.Collapsed;
        // Hidden choices must never be carried out.
        if (!EraserMode) foreach (var t in _traceSwitches) t.Switch.IsChecked = false;
        else RefreshCounts();
        _clear.Content = EraserMode ? "Erase selected..." : "Clear selected...";
        _status.Text = EraserMode
            ? "Tracks Eraser: Windows activity traces first, then browser data. Scroll for more. Everything starts off."
            : "Turn on what you want to remove. Close those browsers first. Everything starts off.";
        UpdateState();
    }

    private ToggleButton Switch(string label, Panel parent, bool enabled)
    {
        var toggle = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("ToggleSwitch"), IsEnabled = enabled
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, label);
        toggle.Checked += (_, _) => UpdateState();
        toggle.Unchecked += (_, _) => UpdateState();
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        row.Children.Add(Ui.Text(label.Split(": ")[^1]));
        parent.Children.Add(row);
        return toggle;
    }

    private Border BrowserCard(Browser browser)
    {
        var panel = Ui.Stack(Ui.Text(browser.Name, 16, weight: FontWeights.SemiBold),
            Ui.Text(browser.Found ? $"{browser.Profiles.Count} profile(s). Applies to all of them." : "Not found",
                    11, "MutedBrush"));
        foreach (var type in Enum.GetValues<BrowserData>())
            _browserSwitches.Add((browser, type,
                Switch($"{browser.Name}: {BrowserCleaner.Label(type)}", panel, browser.Found)));
        var card = Ui.Card(panel);
        card.Margin = new Thickness(0, 0, 12, 12);
        return card;
    }

    private StackPanel BuildTracePanel()
    {
        var rows = Ui.Stack(Ui.Text("Windows activity traces (your account)", 16, weight: FontWeights.SemiBold));
        foreach (var info in WindowsTraces.All)
        {
            var toggle = Switch(info.Name, rows, true);
            var count = Ui.Text("", 11, "AccentBrush");
            rows.Children.Add(Ui.Text(info.Note, 11, "MutedBrush"));
            rows.Children.Add(count);
            _traceSwitches.Add((info.Kind, toggle, count));
        }

        // Windows has no supported way for this app to clear these, so it does not pretend to.
        rows.Children.Add(Ui.Text("Location history (manual)", 13, weight: FontWeights.SemiBold));
        rows.Children.Add(Ui.Text("This app cannot clear it. Open Windows Location settings and use Clear " +
            "next to Location history, if your Windows version shows it.", 11, "MutedBrush"));
        rows.Children.Add(SettingsButton("Open Location settings", "ms-settings:privacy-location"));
        rows.Children.Add(Ui.Text("Windows Activity history is also managed in Settings and is not cleared " +
            "by the app usage switch.", 11, "MutedBrush"));
        rows.Children.Add(SettingsButton("Open Activity history settings", "ms-settings:privacy-activityhistory"));

        var card = Ui.Card(rows);
        card.Margin = new Thickness(0, 0, 12, 12);
        return Ui.Stack(card);
    }

    private Button SettingsButton(string text, string uri)
    {
        var button = new Button
        {
            Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10)
        };
        button.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (Win32Exception ex) { _status.Text = "Could not open Settings: " + ex.Message; }
        };
        return button;
    }

    private async void RefreshCounts()
    {
        foreach (var (kind, _, label) in _traceSwitches.ToList())
        {
            try
            {
                var count = await Task.Run(() => _traces.Count(kind));
                label.Text = count is { } n ? $"{n:N0} item(s) found" : "Contents are not read";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            { label.Text = "Could not count items"; }
        }
    }

    private void UpdateState()
    {
        var cookies = _browserSwitches.Any(s => s.Type == BrowserData.Cookies && s.Switch.IsChecked == true);
        _cookieNote.Visibility = cookies ? Visibility.Visible : Visibility.Collapsed;
        _clear.IsEnabled = !_busy && SelectedCount > 0;
    }

    private static bool Ask(string text, string title) => MessageBox.Show(text, title, MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task ConfirmAndClearAsync()
    {
        if (_busy) return;
        var browserPicks = _browserSwitches.Where(s => s.Switch.IsChecked == true).ToList();
        var tracePicks = EraserMode
            ? _traceSwitches.Where(s => s.Switch.IsChecked == true).Select(s => s.Kind).ToList()
            : new List<TraceKind>();
        if (browserPicks.Count + tracePicks.Count == 0) return;

        if (browserPicks.Any(s => s.Type == BrowserData.Cookies) && !Ask(CookieWarning, "Cookie warning")) return;

        var lines = browserPicks.Select(s => $"• {s.Browser.Name}: {BrowserCleaner.Label(s.Type)}")
            .Concat(tracePicks.Select(k => "• " + WindowsTraces.All.First(t => t.Kind == k).Name));
        var text = $"Permanently remove:\n\n{string.Join("\n", lines)}\n\nThis cannot be undone.";
        if (browserPicks.Any(s => s.Type == BrowserData.Sessions))
            text += "\nClearing sessions means open and pinned tabs may not come back.";
        if (!Ask(text, "Confirm")) return;

        _busy = true;
        UpdateState();
        foreach (var s in _browserSwitches) s.Switch.IsEnabled = false;
        foreach (var s in _traceSwitches) s.Switch.IsEnabled = false;
        _status.Text = "Working...";
        var results = new List<EraseResult>();
        try
        {
            foreach (var group in browserPicks.GroupBy(s => s.Browser))
            {
                var browser = group.Key;
                var types = group.Select(s => s.Type).ToList();
                results.AddRange(await Task.Run(() => BrowserCleaner.Clear(browser, types)));
            }
            // Only the clipboard has to run on the window's thread.
            foreach (var kind in tracePicks)
                results.Add(kind == TraceKind.Clipboard ? _traces.Erase(kind) : await Task.Run(() => _traces.Erase(kind)));
        }
        catch (Exception ex) { results.Add(new EraseResult("Cleanup", false, "Stopped: " + ex.Message)); }
        finally
        {
            _busy = false;
            foreach (var s in _browserSwitches) { s.Switch.IsChecked = false; s.Switch.IsEnabled = s.Browser.Found; }
            foreach (var s in _traceSwitches) { s.Switch.IsChecked = false; s.Switch.IsEnabled = true; }
            if (EraserMode) RefreshCounts();
            UpdateState();
        }
        ActivityLog.Post(ActivityLog.Cleanup, "Browser and privacy cleanup", results.All(r => r.Ok), null,
            string.Join("; ", results.Select(r => $"{r.Item}: {r.Message}")));
        _report.Text = "REPORT\n\n" + string.Join("\n",
            results.Select(r => $"{(r.Ok ? "✓" : "✗")} {r.Item}\n    {r.Message}"));
        _reportCard.Visibility = Visibility.Visible;
        _status.Text = results.All(r => r.Ok) ? "Finished." : "Finished with problems. See the report.";
    }
}
