using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.DriverTools;

namespace PCOptimizer.Modules;

/// <summary>Remembers whether a driver is being installed, so the app is not closed midway.</summary>
public sealed class DriverWork
{
    public string? Installing { get; set; }
}

public sealed class DriverManagerModule : IBackgroundModule
{
    private readonly DriverWork _work = new();

    public string Id => "driver-manager";
    public string Title => "Driver Manager";
    public string Icon => "";
    public string Description => "View, check, update and back up drivers";
    public int Order => 13;

    public IDisposable StartBackground(AppContextServices services)
    {
        void Block(object? sender, CancelEventArgs e)
        {
            if (_work.Installing == null) return;
            e.Cancel = true;
            MessageBox.Show($"{_work.Installing} Wait for it to finish before closing WinBlazer.",
                            "Driver work in progress", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Window? window = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            window = Application.Current.MainWindow;
            if (window != null) window.Closing += Block;
        });
        return new Stopper(() => { if (window != null) window.Closing -= Block; });
    }

    public FrameworkElement CreateView(AppContextServices services) => new DriverManagerView(services, _work);

    private sealed class Stopper(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}

public sealed class DriverManagerView : UserControl
{
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly DriverService _drivers = new();
    private readonly WindowsDriverUpdater _updater = new();
    private readonly DriverWork _work;
    private readonly string _safetyFolder;
    private readonly ListBox _list = new();
    private readonly ComboBox _filter = new() { MinWidth = 170, Margin = new Thickness(0, 0, 8, 8) };
    private readonly TextBox _search = new() { Width = 180, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 8, 8) };
    private readonly Button _refresh = Make("Reload drivers"), _check = Make("Check for updates"),
        _backup = Make("Back up drivers...");
    private readonly TextBlock _status = Ui.Text("Reading installed drivers...", 12);
    private readonly TextBlock _sources = Ui.Text("Updates have not been checked yet.", 11, "MutedBrush");
    private List<DeviceDriver> _items = new();
    private bool _busy, _loaded, _checked;

    public bool HasLoaded { get; private set; }
    public int DriverCount => _items.Count;
    public int ShownCount => _list.Items.Count;
    public IReadOnlyList<DeviceDriver> Items => _items;

    private static Button Make(string text) => new() { Content = text, Margin = new Thickness(0, 0, 8, 8) };

    public DriverManagerView(AppContextServices services, DriverWork work)
    {
        _work = work;
        _safetyFolder = Path.Combine(Path.GetDirectoryName(services.Settings.FilePath)!, "DriverSafetyCopies");

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

        foreach (var text in new[] { "All drivers", "Needs attention", "Outdated", "Missing", "Error", "Third-party only" })
            _filter.Items.Add(new ComboBoxItem { Content = text });
        _filter.SelectedIndex = 0;
        Ui.AlignWithButtons(_filter, _search);
        _filter.SelectionChanged += (_, _) => Render();
        _search.TextChanged += (_, _) => Render();
        System.Windows.Automation.AutomationProperties.SetName(_filter, "Driver filter");
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search drivers");

        _refresh.Role(ButtonRole.Refresh);
        _refresh.Click += async (_, _) => await LoadAsync();
        _check.Role(ButtonRole.Main);
        _check.Click += async (_, _) => await CheckAsync();
        _backup.Click += async (_, _) => await BackupAsync();
        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var control in new UIElement[] { _refresh, _check, _backup, _filter, _search })
            buttons.Children.Add(control);
        if (!_isAdmin)
        {
            var elevate = Make("Restart as administrator");
            elevate.Click += (_, _) => RestartElevated();
            buttons.Children.Add(elevate);
        }

        var top = Ui.Stack(Ui.Text("Driver Manager", 26, weight: FontWeights.SemiBold),
            Ui.Text("An older date alone does not mean a driver is outdated. Update only to fix a problem or get " +
                    "something you need. Check for updates contacts Microsoft, and NVIDIA (graphics card model and " +
                    "Windows version) if you have an NVIDIA card." +
                    (_isAdmin ? "" : " Updating and backup need administrator rights."), 12, "MutedBrush"),
            buttons, _status, _sources);
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
        _refresh.IsEnabled = _check.IsEnabled = _backup.IsEnabled = _list.IsEnabled = !busy;
    }

    public async Task LoadAsync(string? message = null)
    {
        if (_busy) return;
        SetBusy(true);
        _status.Text = "Reading installed drivers...";
        try
        {
            _items = await Task.Run(_drivers.ReadInstalled);
            _checked = false;
            _sources.Text = "Updates have not been checked since the list was loaded.";
            _status.Text = (message == null ? "" : message + "\n") + Summary();
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { SetBusy(false); HasLoaded = true; Render(); }
    }

    private string Summary()
    {
        int Count(DriverHealth health) => _items.Where(d => d.Health == health).Sum(d => d.Count);
        return $"{_items.Sum(d => d.Count)} devices ({_items.Count} rows). Missing: {Count(DriverHealth.Missing)}, " +
               $"error: {Count(DriverHealth.Error)}, disabled: {Count(DriverHealth.Disabled)}, " +
               (_checked ? $"outdated: {Count(DriverHealth.Outdated)}." : "outdated: not checked.");
    }

    private async Task CheckAsync()
    {
        if (_busy || _items.Count == 0) return;
        SetBusy(true);
        _status.Text = "Checking Windows Update (this can take a minute or more)...";
        var notes = new List<string>();
        try
        {
            try
            {
                var offers = await Task.Run(_updater.Search);
                var unmatched = DriverInventory.Match(_items, offers);
                notes.Add($"Windows Update offered {offers.Count} driver(s).");
                if (unmatched.Count > 0)
                    notes.Add("Not matched to a listed device (see Windows Update): " +
                              string.Join("; ", unmatched.Select(u => u.Title)));
                _checked = true;
            }
            catch (Exception ex) { notes.Add("Windows Update check FAILED: " + ex.Message); }

            foreach (var gpu in _items.Where(NvidiaLookup.IsNvidiaDisplay))
            {
                try
                {
                    var latest = await NvidiaLookup.LatestAsync(gpu);
                    var installed = NvidiaLookup.MarketingVersion(gpu.Version);
                    gpu.Vendor = latest;
                    gpu.VendorIsNewer = installed == null ? null : NvidiaLookup.IsNewer(latest.Version, installed);
                    notes.Add($"NVIDIA: latest Game Ready is {latest.Version}; installed is {installed ?? "unknown"}.");
                }
                catch (Exception ex) { notes.Add("NVIDIA check FAILED: " + ex.Message); }
            }
            notes.Add("Other manufacturers have no automatic vendor check; use their official pages.");
            _sources.Text = $"Checked {DateTime.Now:t}. " + string.Join("\n", notes);
            _items = _items.OrderBy(d => d.Health).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            _status.Text = Summary();
        }
        finally { SetBusy(false); Render(); }
    }

    private void Render()
    {
        _list.Items.Clear();
        var query = _search.Text.Trim();
        var filter = _filter.SelectedIndex;
        foreach (var d in _items.Where(d => filter switch
                 {
                     1 => d.Health is DriverHealth.Missing or DriverHealth.Error or DriverHealth.Outdated,
                     2 => d.Health == DriverHealth.Outdated,
                     3 => d.Health == DriverHealth.Missing,
                     4 => d.Health == DriverHealth.Error,
                     5 => d.IsThirdParty,
                     _ => true
                 }).Where(d => query == "" || new[] { d.Name, d.Provider, d.Manufacturer, d.Class }
                     .Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))))
            _list.Items.Add(Row(d));
    }

    private FrameworkElement Row(DeviceDriver d)
    {
        var (label, color) = d.Health switch
        {
            DriverHealth.Missing => ("MISSING", "BadBrush"),
            DriverHealth.Error => ("ERROR", "BadBrush"),
            DriverHealth.Outdated => ("OUTDATED", "WarnBrush"),
            DriverHealth.Disabled => ("DISABLED", "MutedBrush"),
            _ => ("WORKING", "GoodBrush")
        };
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, FontWeight = FontWeights.SemiBold };
        title.Inlines.Add(new System.Windows.Documents.Run(label + "  ") { Foreground = Ui.Brush(color), FontSize = 11 });
        title.Inlines.Add(new System.Windows.Documents.Run(d.Name + (d.Count > 1 ? $"  (×{d.Count})" : ""))
        { Foreground = Ui.Brush("TextBrush") });

        var content = Ui.Stack(title, Ui.Text(
            $"Version: {(d.Version == "" ? "none" : d.Version)}     Date: {d.Date?.ToString("yyyy-MM-dd") ?? "none"}" +
            $"     Provider: {(d.Provider == "" ? "unknown" : d.Provider)}     {d.Class}", 11, "MutedBrush"));
        if (d.ProblemCode != 0) content.Children.Add(Ui.Text(DeviceDriver.ProblemText(d.ProblemCode), 11, "AccentBrush"));
        if (d.ProblemCode == 28 && d.HardwareIds.Count == 0)
            content.Children.Add(Ui.Text("This entry reports no hardware ID, so no driver can be matched to it.", 11, "MutedBrush"));
        if (d.WindowsUpdate is { } offer)
            content.Children.Add(Ui.Text($"Windows Update offers: {offer.Title}" +
                (offer.Date is { } date ? $" ({date:yyyy-MM-dd})" : "") +
                (d.OfferIsNotNewer ? ". Not newer by date than yours, so not flagged." :
                 d.Version.Length > 0 && offer.Title.Contains($"({d.Version})")
                     ? ". It has the SAME version number as yours; only the date is newer, so you may notice no change."
                     : ""), 11, "AccentBrush"));
        if (d.Vendor is { } vendor)
            content.Children.Add(Ui.Text($"NVIDIA latest: {vendor.Version}" +
                (vendor.Released is { } released ? $" ({released:yyyy-MM-dd})" : "") +
                (d.VendorIsNewer == true ? ". Newer than yours." : d.VendorIsNewer == false
                    ? ". Yours is the same or newer." : ". Could not compare."), 11, "AccentBrush"));

        var actions = new WrapPanel();
        if (d.WindowsUpdate != null && !d.OfferIsNotNewer)
        {
            var update = Make(d.Health == DriverHealth.Missing ? "Install" : "Update");
            update.IsEnabled = _isAdmin;
            update.ToolTip = _isAdmin ? "Install from Windows Update" : "Needs administrator rights";
            ToolTipService.SetShowOnDisabled(update, true);
            update.Click += async (_, _) => await UpdateAsync(d);
            actions.Children.Add(update);
        }
        var page = d.Vendor?.PageUrl ?? VendorPages.For(d);
        if (page != null && (d.Health != DriverHealth.Working || d.Vendor != null))
        {
            var open = Make(d.VendorIsNewer == true ? "Get it from NVIDIA" : "Vendor page");
            open.ToolTip = page;
            open.Click += (_, _) => Open(page);
            actions.Children.Add(open);
        }
        if (actions.Children.Count > 0) content.Children.Add(actions);

        var card = Ui.Card(content);
        card.Padding = new Thickness(14, 8, 14, 2);
        card.Margin = new Thickness(0, 0, 8, 6);
        return card;
    }

    private void Open(string url)
    {
        if (!VendorPages.IsOfficial(url)) return;
        try
        {
            // When elevated, the page is opened through Explorer so the browser does not run as administrator.
            Process.Start(_isAdmin
                ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                                    "explorer.exe"), $"\"{url}\"")
                : new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception ex) { _status.Text = ex.Message; }
    }

    private static bool Ask(string text, string title) => MessageBox.Show(text, title, MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task UpdateAsync(DeviceDriver d)
    {
        if (_busy || d.WindowsUpdate is not { } offer) return;
        if (!Ask($"Install this driver from Windows Update?\n\n{offer.Title}\nfor {d.Name}\n\n" +
                 (d.IsThirdParty ? "The current driver is saved first." :
                     "The current driver is part of Windows or missing, so no copy is made.") +
                 " Save your work. The screen, network, sound or input may drop briefly. A bad driver can cause " +
                 "problems; create a restore point if you can. To go back, use Device Manager > Roll Back Driver. " +
                 "Windows Update installs may not be cancellable.", "Update driver")) return;
        SetBusy(true);
        try
        {
            var accept = false;
            if (!offer.EulaAccepted)
            {
                _status.Text = "Reading the license terms...";
                var license = await Task.Run(() => _updater.License(offer.UpdateId));
                if (license != null)
                {
                    if (!ShowLicense(license)) { _status.Text = "License not accepted. Nothing was installed."; return; }
                    accept = true;
                }
            }
            _work.Installing = $"A driver is being installed for {d.Name}.";
            var progress = new Progress<string>(text => _status.Text = text);
            var report = (IProgress<string>)progress;
            var result = await Task.Run(() => _drivers.Update(d, _updater, _safetyFolder, _isAdmin, accept, report.Report));
            _work.Installing = null;
            SetBusy(false);
            ActivityLog.Post("Drivers", $"Update driver: {d.Name}", result.Ok, null, $"{offer.Title}. {result.Message}");
            await LoadAsync($"{d.Name}: {result.Message}");
        }
        catch (Exception ex) { _status.Text = "The update did not complete: " + ex.Message; }
        finally { _work.Installing = null; SetBusy(false); }
    }

    private static bool ShowLicense(string text)
    {
        var window = new Window
        {
            Title = "Driver license terms", Width = 620, Height = 480, Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = SystemColors.WindowBrush
        };
        var accept = new Button { Content = "I accept", Margin = new Thickness(0, 0, 8, 0) };
        var decline = new Button { Content = "Decline", IsCancel = true };
        accept.Click += (_, _) => window.DialogResult = true;
        var row = new StackPanel
        { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        row.Children.Add(accept);
        row.Children.Add(decline);
        var dock = new DockPanel();
        DockPanel.SetDock(row, Dock.Bottom);
        dock.Children.Add(row);
        dock.Children.Add(new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Foreground = Brushes.Black, Background = Brushes.White
        });
        window.Content = dock;
        return window.ShowDialog() == true;
    }

    private async Task BackupAsync()
    {
        if (_busy || _items.Count == 0) return;
        if (!_isAdmin) { _status.Text = "Backing up drivers needs administrator rights. Use Restart as administrator."; return; }
        var dialog = new OpenFolderDialog { Title = "Choose where to create the driver backup folder" };
        if (dialog.ShowDialog() != true) return;
        SetBusy(true);
        _status.Text = "Exporting third-party drivers. This can take several minutes...";
        _work.Installing = "Drivers are being backed up.";
        try
        {
            var result = await Task.Run(() => _drivers.Backup(dialog.FolderName, _items, _isAdmin));
            ActivityLog.Post("Drivers", "Driver backup", result.Ok, null, $"{result.Message} {result.Folder}");
            _status.Text = (result.Ok ? "Backup complete. " : "BACKUP INCOMPLETE. ") + result.Message +
                           (result.Folder == "" ? "" : "\n" + result.Folder);
        }
        catch (Exception ex) { _status.Text = "Backup failed: " + ex.Message; }
        finally { _work.Installing = null; SetBusy(false); }
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

    /// <summary>Used by the self-test to run the same read-only check as the button.</summary>
    public async Task CheckForTestAsync() => await CheckAsync();
    public string SourcesText => _sources.Text;
}
