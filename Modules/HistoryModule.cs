using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;

namespace PCOptimizer.Modules;

public sealed class HistoryModule : IModule
{
    public string Id => "history";
    public string Title => "History";
    public string Icon => "";
    public string Description => "Past actions and space freed";
    public int Order => 900;
    public FrameworkElement CreateView(AppContextServices services) => new HistoryView();
}

public sealed class HistoryView : UserControl
{
    private readonly ListBox _list = new();
    private readonly ComboBox _filter = new() { MinWidth = 150, Margin = new Thickness(0, 0, 8, 6) };
    private readonly TextBlock _summary = Ui.Text("", 15, weight: FontWeights.SemiBold);
    private readonly TextBlock _status = Ui.Text("", 12, "AccentBrush");
    private List<Activity> _entries = new();
    private bool _filling;

    public int ShownCount => _list.Items.Count;
    public string SummaryText => _summary.Text;

    public HistoryView()
    {
        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Foreground = Ui.Brush("TextBrush");
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(_filter, "History category");
        _filter.SelectionChanged += (_, _) => { if (!_filling) Render(); };
        var clear = new Button { Content = "Clear history...", Margin = new Thickness(0, 0, 8, 6) };
        clear.Click += async (_, _) =>
        {
            if (MessageBox.Show("Delete the whole history list? This does not undo any action. Backups are not affected.",
                    "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                != MessageBoxResult.Yes) return;
            try { await Task.Run(ActivityLog.Clear); _status.Text = "History cleared."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = ex.Message; }
            await ReloadAsync();
        };
        var row = new WrapPanel();
        Ui.AlignWithButtons(_filter);
        row.Children.Add(_filter);
        row.Children.Add(clear);
        var top = Ui.Stack(Ui.Text("History", 26, weight: FontWeights.SemiBold),
            Ui.Text("Actions are recorded from the time this feature was added. The latest entries are kept (older " +
                    "ones are trimmed). \"Space freed\" counts only measured file cleanup.", 12, "MutedBrush"),
            _summary, row, _status);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_list);
        Content = dock;

        void OnAdded(Activity _) => Dispatcher.BeginInvoke(() => { var reload = ReloadAsync(); });
        Loaded += (_, _) => { ActivityLog.Added -= OnAdded; ActivityLog.Added += OnAdded; var reload = ReloadAsync(); };
        Unloaded += (_, _) => ActivityLog.Added -= OnAdded;
    }

    public async Task ReloadAsync()
    {
        try { _entries = await Task.Run(ActivityLog.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = ex.Message; }
        var selected = (_filter.SelectedItem as string) ?? "All actions";
        _filling = true;
        _filter.Items.Clear();
        _filter.Items.Add("All actions");
        foreach (var category in _entries.Select(e => e.Category).Distinct().Order()) _filter.Items.Add(category);
        _filter.SelectedItem = _filter.Items.Contains(selected) ? selected : "All actions";
        _filling = false;
        var month = DateTime.Now.AddDays(-30);
        var recent = _entries.Where(e => e.Time >= month).Sum(e => e.BytesFreed ?? 0);
        var actions = _entries.Count;
        Motion.Count(_summary, _entries.Sum(e => e.BytesFreed ?? 0),
            total => $"Space freed in listed history: {CleanupEngine.Size((long)total)}" +
                     $"     Last 30 days: {CleanupEngine.Size(recent)}     Actions: {actions:N0}");        Render();
    }

    private void Render()
    {
        _list.Items.Clear();
        var category = _filter.SelectedItem as string;
        var shown = _entries.Where(e => category is null or "All actions" || e.Category == category).Take(500).ToList();
        if (shown.Count == 0) _list.Items.Add(Ui.Text("No actions recorded yet.", 13, "MutedBrush"));
        foreach (var e in shown)
        {
            var content = Ui.Stack(
                Ui.Text((e.Success ? "✓ " : "⚠ ") + e.Action, 13, weight: FontWeights.SemiBold),
                Ui.Text($"{e.Time:g}     {e.Category}" +
                        (e.BytesFreed is { } bytes ? $"     Space freed: {CleanupEngine.Size(bytes)}" : ""), 11, "AccentBrush"));
            if (e.Details.Length > 0) content.Children.Add(Ui.Text(e.Details, 11, "MutedBrush"));
            var card = Ui.Card(content);
            card.Padding = new Thickness(12, 6, 12, 6);
            card.Margin = new Thickness(0, 0, 8, 5);
            _list.Items.Add(card);
        }
    }
}
