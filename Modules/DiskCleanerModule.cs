using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;

namespace PCOptimizer.Modules;

public sealed class DiskCleanerModule : IScannableModule
{
    public string Id => "disk-cleaner";   // unchanged so saved settings still apply
    public string Title => "Disk Space Cleaner";
    public string Icon => "";
    public string Description => "Free up space and see what is using your drives";
    public int Order => 10;

    public FrameworkElement CreateView(AppContextServices services) => new DiskCleanerView(services);

    /// <summary>Read-only scheduled scan.</summary>
    public Task<string> ScanAsync(CleanupPreferences preferences, CancellationToken token)
    {
        var age = preferences.MinimumAgeDays;
        return Task.Run(() =>
        {
            var scans = CleanupCatalog.Create().Where(c => c.Kind == CategoryKind.Files)
                .Select(c => CleanupEngine.Scan(c, age, token)).ToList();
            return $"About {CleanupEngine.Size(scans.Sum(s => s.Bytes))} found in cleanup folders " +
                   "(excludes Recycle Bin and Windows.old). Nothing was deleted.";
        }, token);
    }
}

/// <summary>The module page with its two sub-pages.</summary>
public sealed class DiskCleanerView : UserControl, ISectionHost
{
    public void ShowSection(string section) => (section == "analyzer" ? _analyzerTab : _cleanerTab).IsChecked = true;

    private readonly ContentControl _host = new() { Focusable = false };
    private readonly RadioButton _cleanerTab, _analyzerTab;

    public CleanerView Cleaner { get; }
    public DiskAnalyzerView Analyzer { get; } = new();

    public DiskCleanerView(AppContextServices services)
    {
        Cleaner = new CleanerView(services);
        _cleanerTab = Tab("Cleaner", Cleaner);
        _analyzerTab = Tab("Disk Analyzer", Analyzer);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        tabs.Children.Add(_cleanerTab);
        tabs.Children.Add(_analyzerTab);

        var top = Ui.Stack(Ui.Text("Disk Space Cleaner", 26, weight: FontWeights.SemiBold), tabs);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_host);
        Content = dock;
        _cleanerTab.IsChecked = true;
    }

    public void ShowAnalyzer() => _analyzerTab.IsChecked = true;

    private RadioButton Tab(string title, UIElement page)
    {
        var tab = new RadioButton
        {
            Content = title, GroupName = "disk-pages", Margin = new Thickness(0, 0, 8, 0), MinWidth = 120,
            Style = (Style)Application.Current.FindResource("PageTab")
        };
        tab.Checked += (_, _) => _host.Content = page;
        return tab;
    }
}
