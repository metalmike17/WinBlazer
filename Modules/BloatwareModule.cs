using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.Bloatware;

namespace PCOptimizer.Modules;

public sealed class BloatWork
{
    public bool Running { get; set; }
}

public sealed class BloatwareModule : IBackgroundModule
{
    private readonly BloatWork _work = new();

    public string Id => "bloatware-remover";
    public string Title => "Bloatware Remover";
    public string Icon => "";
    public string Description => "Remove preinstalled apps you don't use";
    public int Order => 22;

    public IDisposable StartBackground(AppContextServices services)
    {
        void Block(object? sender, CancelEventArgs e)
        {
            if (!_work.Running) return;
            e.Cancel = true;
            MessageBox.Show("Apps are being changed. Wait for this to finish before closing WinBlazer.",
                            "Bloatware Remover", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Window? window = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            window = Application.Current.MainWindow;
            if (window != null) window.Closing += Block;
        });
        return new Stopper(() => { if (window != null) window.Closing -= Block; });
    }

    public FrameworkElement CreateView(AppContextServices services) => new BloatwareView(services, _work);

    private sealed class Stopper(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}

public sealed class BloatwareView : UserControl
{
    private readonly AppxService _apps;
    private readonly BloatWork _work;
    private readonly ListBox _list = new();
    private readonly CheckBox _showAbsent = new()
    {
        Content = "Show apps that aren't installed", VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 8), Foreground = Ui.Brush("TextBrush")
    };
    private readonly Button _preset = Make("Debloat Preset"), _reload = Make("Reload");
    private readonly TextBlock _status = Ui.Text("Reading installed apps...", 12);
    private readonly TextBlock _space = Ui.Text("", 12, "AccentBrush");
    private AppSnapshot? _snapshot;
    private bool _busy, _loaded;

    public bool HasLoaded { get; private set; }
    public IReadOnlyList<BloatEntry> Entries => _snapshot?.Entries ?? new List<BloatEntry>();
    public int ShownCount => _list.Items.Count;
    public string StatusText => _status.Text + " | " + _space.Text;

    private static Button Make(string text) => new() { Content = text, Margin = new Thickness(0, 0, 8, 8) };

    public BloatwareView(AppContextServices services, BloatWork work)
    {
        _work = work;
        _apps = new AppxService(Path.Combine(
            Path.GetDirectoryName(services.Settings.FilePath)!, "removed-apps.json"));

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

        _preset.Role(ButtonRole.Main);
        _preset.Click += async (_, _) => await RunPresetAsync();
        _reload.Role(ButtonRole.Refresh);
        _reload.Click += async (_, _) => await LoadAsync();
        _showAbsent.Click += (_, _) => Render();
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        buttons.Children.Add(_preset);
        buttons.Children.Add(_reload);
        buttons.Children.Add(_showAbsent);

        var top = Ui.Stack(Ui.Text("Bloatware Remover", 26, weight: FontWeights.SemiBold),
            Ui.Text("Only apps on a known list are shown; Windows components and unrecognized apps never are. " +
                    "Switch an app off to uninstall it for your account. Its local app data is deleted and " +
                    "reinstalling does not bring that data back.", 12, "MutedBrush"),
            buttons, _status, _space);
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
        _preset.IsEnabled = _reload.IsEnabled = _showAbsent.IsEnabled = _list.IsEnabled = !busy;
        if (!busy) _preset.IsEnabled = PresetTargets().Count > 0;
    }

    private List<BloatEntry> PresetTargets() => Entries.Where(e => e.Installed && e.App.InPreset).ToList();

    private static long Bytes(IEnumerable<BloatEntry> entries) => entries.Sum(e => (e.AppBytes ?? 0) + e.DataBytes);

    public async Task LoadAsync(string? message = null)
    {
        if (_busy) return;
        SetBusy(true);
        _status.Text = "Reading installed apps and measuring their size...";
        try
        {
            _snapshot = await Task.Run(() =>
            {
                var snapshot = _apps.Read();
                foreach (var entry in snapshot.Entries) _apps.Measure(entry);
                return snapshot;
            });
            var installed = Entries.Where(e => e.Installed).ToList();
            var targets = PresetTargets();
            _preset.Content = $"Debloat Preset ({targets.Count})";
            _status.Text = (message == null ? "" : message + "\n") +
                (installed.Count == 0 ? "None of the listed removable apps are installed. There is nothing to remove."
                    : $"{installed.Count} removable app(s) installed.") +
                (_snapshot.StoreInstalled ? "" :
                    "\nThe Microsoft Store is not installed, so apps that must be downloaded cannot be reinstalled " +
                    "directly from here.");
            _space.Text = installed.Count == 0 ? "" :
                $"Up to {CleanupEngine.Size(Bytes(installed))} in listed apps; preset: up to " +
                $"{CleanupEngine.Size(Bytes(targets))}. Windows may keep app files, so the space actually freed can be less.";
        }
        catch (Exception ex) { _snapshot = null; _status.Text = ex.Message; }
        finally { SetBusy(false); HasLoaded = true; Render(); }
    }

    private void Render()
    {
        _list.Items.Clear();
        var shown = Entries.Where(e => e.Installed || e.Removed != null || _showAbsent.IsChecked == true)
            .OrderBy(e => e.Installed ? 0 : e.Removed != null ? 1 : 2)
            .ThenBy(e => e.App.Category).ThenBy(e => e.App.Title);
        foreach (var entry in shown) _list.Items.Add(Row(entry));
    }

    private FrameworkElement Row(BloatEntry e)
    {
        var size = !e.Installed ? (e.Removed != null ? $"Removed {e.Removed.RemovedAt:g}" : "Not installed")
            : e.AppBytes == null ? "Size unavailable"
            : $"Up to {CleanupEngine.Size(e.AppBytes.Value + e.DataBytes)} " +
              $"(app {CleanupEngine.Size(e.AppBytes.Value)}, your data {CleanupEngine.Size(e.DataBytes)})";
        var text = Ui.Stack(Ui.Text(e.App.Title + (e.App.InPreset ? "   • preset" : ""), 13, weight: FontWeights.SemiBold),
            Ui.Text($"{e.App.Category}     {size}", 11, "MutedBrush"));
        if (e.App.Note.Length > 0) text.Children.Add(Ui.Text(e.App.Note, 11, "AccentBrush"));

        var toggle = new CheckBox
        {
            IsChecked = e.Installed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("ToggleSwitch"),
            ToolTip = e.Installed ? "On: installed. Switch off to uninstall." : "Off: not installed. Switch on to reinstall."
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, e.App.Title + " installed");
        toggle.Click += async (_, _) =>
        {
            toggle.IsChecked = e.Installed;   // the switch only moves once the change is confirmed
            if (e.Installed) await RemoveAsync(new List<BloatEntry> { e }, $"Uninstall {e.App.Title}?");
            else await ReinstallAsync(e);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(text);
        grid.Children.Add(toggle);
        var card = Ui.Card(grid);
        card.Padding = new Thickness(14, 8, 14, 8);
        card.Margin = new Thickness(0, 0, 8, 6);
        return card;
    }

    private static bool Ask(string text, string title) => MessageBox.Show(text, title, MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private Task RunPresetAsync() => RemoveAsync(PresetTargets(), "Run the Debloat Preset?");

    private async Task RemoveAsync(List<BloatEntry> targets, string question)
    {
        if (_busy || targets.Count == 0) return;
        var names = string.Join("\n", targets.Select(e =>
            "  " + e.App.Title + (e.App.Note.Length > 0 ? " - " + e.App.Note : "")));
        if (!Ask($"{question}\n\n{names}\n\nUp to {CleanupEngine.Size(Bytes(targets))}. Local app data is deleted " +
                 "and cannot be restored by this app." +
                 (_snapshot?.StoreInstalled == false
                     ? " The Microsoft Store is not installed, so reinstalling may not be possible." : ""),
                 "Uninstall apps")) return;
        SetBusy(true);
        _work.Running = true;
        var results = new List<AppAction>();
        try
        {
            foreach (var entry in targets)
            {
                _status.Text = $"Removing {entry.App.Title}...";
                try { results.Add(await Task.Run(() => _apps.Remove(entry))); }
                catch (Exception ex) { results.Add(new(false, $"{entry.App.Title}: {ex.Message}")); break; }
            }
        }
        finally { _work.Running = false; SetBusy(false); }
        var failed = results.Where(r => !r.Ok).Select(r => r.Message).ToList();
        ActivityLog.Post("Apps", $"Uninstall apps ({results.Count(r => r.Ok)} of {targets.Count})", failed.Count == 0, null,
            string.Join("; ", results.Select(r => r.Message)));
        await LoadAsync($"Removed {results.Count(r => r.Ok)} of {targets.Count}." +
                        (failed.Count > 0 ? "\n" + string.Join("\n", failed) : ""));
    }

    private async Task ReinstallAsync(BloatEntry entry)
    {
        if (_busy || _snapshot == null) return;
        var family = entry.Removed?.FamilyName;
        if (family != null)
        {
            SetBusy(true);
            _work.Running = true;
            _status.Text = $"Reinstalling {entry.App.Title}...";
            AppAction result;
            try { result = await Task.Run(() => _apps.Reregister(family, entry.App.Title)); }
            catch (Exception ex) { result = new(false, ex.Message); }
            finally { _work.Running = false; SetBusy(false); }
            ActivityLog.Post("Apps", $"Reinstall {entry.App.Title}", result.Ok, null, result.Message);
            if (result.Ok) { await LoadAsync(result.Message); return; }
            _status.Text = result.Message;
        }
        var link = AppxService.StoreLink(entry.App, family, _snapshot.StoreInstalled);
        var where = _snapshot.StoreInstalled ? "the Microsoft Store"
            : "Microsoft's web store (the Store app is not installed, so installing from that page may not work)";
        if (!Ask($"Open {where} for {entry.App.Title}?\n\nCheck the publisher before installing. Click Reload afterwards.",
                 "Reinstall from the Store")) return;
        try
        {
            // When elevated, open through Explorer so the browser or Store does not run as administrator.
            Process.Start(CleanupEngine.IsAdministrator()
                ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                                    "explorer.exe"), $"\"{link}\"")
                : new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Win32Exception ex) { _status.Text = ex.Message; }
    }

    /// <summary>Used by the self-test to show the full catalog.</summary>
    public void ShowAbsentForTest()
    {
        _showAbsent.IsChecked = true;
        Render();
    }
}
