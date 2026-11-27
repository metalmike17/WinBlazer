using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;

namespace PCOptimizer.Modules;

public sealed class CleanerView : UserControl
{
    private sealed class Card
    {
        public required CleanCategory Category { get; init; }
        public required CheckBox Check { get; init; }
        public required TextBlock Size { get; init; }
        public required TextBlock State { get; init; }
        public CategoryScan? Scan { get; set; }
    }

    private readonly AppContextServices _services;
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly List<Card> _cards = new();
    private readonly Button _scanButton = new() { Content = "Scan" };
    private readonly Button _cleanButton = new() { Content = "Clean selected...", IsEnabled = false };
    private readonly TextBlock _status = Ui.Text("Press Scan to measure each category. Scanning changes nothing.", 13);
    private readonly TextBlock _report = Ui.Text("", 12);
    private readonly Border _reportCard;
    private int _scannedAge;
    private bool _busy;

    public int CardCount => _cards.Count;
    public int ScannedCount => _cards.Count(c => c.Scan != null);
    public int TickedCount => _cards.Count(c => c.Check.IsChecked == true);

    public CleanerView(AppContextServices services)
    {
        _services = services;
        _report.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _reportCard = Ui.Card(_report);
        _reportCard.Visibility = Visibility.Collapsed;
        _reportCard.Margin = new Thickness(0, 0, 12, 10);

        var grid = new UniformGrid { Columns = 3, VerticalAlignment = VerticalAlignment.Top };
        foreach (var category in CleanupCatalog.Create()) grid.Children.Add(BuildCard(category));

        _scanButton.Role(ButtonRole.Refresh);
        _scanButton.Click += async (_, _) => await ScanAsync();
        _cleanButton.Role(ButtonRole.Main);
        _cleanButton.Click += async (_, _) => await CleanAsync();
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        buttons.Children.Add(_scanButton);
        buttons.Children.Add(_cleanButton);
        if (!_isAdmin)
        {
            var elevate = new Button { Content = "Restart as administrator" };
            elevate.Click += (_, _) => RestartElevated();
            buttons.Children.Add(elevate);
        }

        var top = Ui.Stack(buttons, _status);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(Ui.Scroll(Ui.Stack(_reportCard, grid)));
        Content = dock;
    }

    private Border BuildCard(CleanCategory category)
    {
        var check = new CheckBox
        {
            Content = new TextBlock { Text = category.Name, FontWeight = FontWeights.SemiBold, FontSize = 13 },
            IsEnabled = false
        };
        System.Windows.Automation.AutomationProperties.SetName(check, category.Name);
        var size = Ui.Text("--", 22, weight: FontWeights.SemiBold);
        var state = Ui.Text("Not scanned", 11, "AccentBrush");
        var panel = Ui.Stack(check, size, Ui.Text(category.Note, 11, "MutedBrush"), state);
        if (category.Kind == CategoryKind.ReportOnly)
        {
            var open = new Button { Content = "Open Storage settings", HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true }); }
                catch (Win32Exception ex) { _status.Text = "Could not open Settings: " + ex.Message; }
            };
            panel.Children.Add(open);
        }
        check.Click += (_, _) => UpdateCleanButton();
        _cards.Add(new Card { Category = category, Check = check, Size = size, State = state });
        var border = Ui.Card(panel);
        border.Padding = new Thickness(14);
        border.Margin = new Thickness(0, 0, 12, 12);
        return border;
    }

    private void UpdateCleanButton() => _cleanButton.IsEnabled = !_busy && TickedCount > 0;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _scanButton.IsEnabled = !busy;
        foreach (var card in _cards) if (busy) card.Check.IsEnabled = false;
        UpdateCleanButton();
    }

    private void Show(Card card, CategoryScan scan)
    {
        card.Scan = scan;
        var unknown = scan.Error != null || (scan.Incomplete && scan.Bytes == 0);
        card.Size.Text = unknown ? "Unknown" : (scan.Incomplete ? "≥ " : "") + CleanupEngine.Size(scan.Bytes);
        var blocked = CleanupEngine.WhyBlocked(scan.Category, _isAdmin);
        var empty = scan.Bytes == 0 && scan.Items == 0;
        card.State.Text = scan.Error ?? blocked ??
            (empty ? "Nothing found" : $"{scan.Items:N0} item(s)" + (scan.Incomplete ? ", some unreadable" : ""));
        // A running browser can be closed after the scan, so that block is checked again when cleaning.
        var possible = scan.Category.Kind != CategoryKind.ReportOnly && !empty && scan.Error == null &&
                       (!scan.Category.RequiresAdmin || _isAdmin);
        card.Check.IsEnabled = possible;
        if (!possible) card.Check.IsChecked = false;
    }

    public async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _reportCard.Visibility = Visibility.Collapsed;
        var age = _scannedAge = _services.Settings.Current.Cleanup.MinimumAgeDays;
        foreach (var card in _cards)
        {
            card.Scan = null;
            card.Check.IsChecked = false;   // nothing is ever pre-ticked
            card.Size.Text = "--";
            card.State.Text = "Waiting...";
        }
        try
        {
            foreach (var card in _cards)
            {
                _status.Text = $"Scanning {card.Category.Name}...";
                card.State.Text = "Scanning...";
                try
                {
                    var category = card.Category;
                    Show(card, await Task.Run(() => CleanupEngine.Scan(category, age)));
                }
                catch (Exception ex) { card.State.Text = "Scan failed: " + ex.Message; }
            }
            _status.Text = $"Scan complete. Temp files include only items older than {age} days. " +
                           "Tick what you want to remove.";
        }
        finally
        {
            SetBusy(false);
            foreach (var card in _cards) if (card.Scan != null) Show(card, card.Scan);
        }
    }

    private async Task CleanAsync()
    {
        var picked = _cards.Where(c => c.Check.IsChecked == true && c.Scan != null).ToList();
        if (_busy || picked.Count == 0) return;
        var age = _services.Settings.Current.Cleanup.MinimumAgeDays;
        if (age != _scannedAge) { _status.Text = "The age setting changed. Scan again first."; return; }

        var lines = string.Join("\n", picked.Select(c => $"• {c.Category.Name}: {c.Size.Text}"));
        var warning = $"Permanently delete the selected items?\n\n{lines}\n\nThis cannot be undone.";
        if (picked.Any(c => c.Category.Kind == CategoryKind.RecycleBin))
            warning += "\nEverything in the Recycle Bin will be removed, including items added since the scan.";
        if (MessageBox.Show(warning, "Disk Space Cleaner", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                            MessageBoxResult.No) != MessageBoxResult.Yes) return;

        SetBusy(true);
        var results = new List<CleanResult>();
        var failures = new List<string>();
        var freeBefore = await Task.Run(FreeSpace);
        try
        {
            foreach (var card in picked)
            {
                _status.Text = $"Cleaning {card.Category.Name}...";
                try
                {
                    var scan = card.Scan!;
                    results.Add(await Task.Run(() => CleanupEngine.Clean(scan, age, _isAdmin)));
                }
                catch (Exception ex) { failures.Add($"{card.Category.Name}: {ex.Message}"); }
            }
        }
        finally { SetBusy(false); }

        ActivityLog.Post(ActivityLog.Cleanup, "Disk Space Cleaner", failures.Count == 0, results.Sum(r => r.BytesDeleted),
            string.Join("; ", results.Select(r => $"{r.Category.Name}: {CleanupEngine.Size(r.BytesDeleted)}").Concat(failures)));
        var report = BuildReport(results, failures, freeBefore, await Task.Run(FreeSpace));
        await ScanAsync();   // refresh the cards, then show the report
        _report.Text = report;
        _reportCard.Visibility = Visibility.Visible;
        _status.Text = "Cleanup finished. See the report below.";
    }

    public static string BuildReport(IReadOnlyList<CleanResult> results, IReadOnlyList<string> failures,
                                     long freeBefore, long freeAfter)
    {
        var text = new StringBuilder("SPACE FREED REPORT\n\n");
        foreach (var r in results)
        {
            text.AppendLine($"{r.Category.Name}");
            text.AppendLine($"  before {CleanupEngine.Size(r.BytesBefore)}  ->  after " +
                            (r.AfterIncomplete ? "at least " : "") + CleanupEngine.Size(r.BytesAfter));
            text.AppendLine($"  removed {CleanupEngine.Size(r.BytesDeleted)} ({r.Deleted:N0} items), skipped {r.Skipped:N0}");
            if (r.Message != null) text.AppendLine("  " + r.Message);
        }
        foreach (var f in failures) text.AppendLine("Failed - " + f);
        text.AppendLine();
        text.AppendLine($"Total size of removed files: {CleanupEngine.Size(results.Sum(r => r.BytesDeleted))}");
        text.AppendLine($"Free space on all fixed drives: {CleanupEngine.Size(freeBefore)} -> {CleanupEngine.Size(freeAfter)}");
        text.Append("Free space can differ from the total because other programs also use the drives.");
        return text.ToString();
    }

    private static long FreeSpace()
    {
        long total = 0;
        foreach (var drive in DriveInfo.GetDrives())
        {
            try { if (drive.DriveType == DriveType.Fixed && drive.IsReady) total += drive.AvailableFreeSpace; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
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
