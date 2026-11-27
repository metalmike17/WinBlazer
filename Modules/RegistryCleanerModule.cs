using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer.Modules;

public sealed class RegistryCleanerModule : IScannableModule
{
    public string Id => "registry-cleaner";
    public string Title => "Registry Cleaner";
    public string Icon => "";
    public string Description => "Review broken registry entries, with backup and restore";
    public int Order => 30;
    public FrameworkElement CreateView(AppContextServices services) => new RegistryCleanerView();

    public Task<string> ScanAsync(CleanupPreferences preferences, CancellationToken token) => Task.Run(() =>
        $"{new RegistryScanner(new RegistryStore()).Scan(token).Count} registry entries to review. Nothing was changed.",
        token);
}

public sealed class RegistryCleanerView : UserControl
{
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly RegistryCleanerService _service;
    private readonly RegistryScanner _scanner;
    private readonly BackupFolders _folders;
    private readonly TreeView _tree = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly List<(RegistryIssue Issue, CheckBox Check)> _items = new();
    private readonly Button _scan = new() { Content = "Scan" };
    private readonly Button _tickSafe = new() { Content = "Tick Safe items", IsEnabled = false };
    private readonly Button _clean = new() { Content = "Back up and delete...", IsEnabled = false };
    private readonly Button _restore = new() { Content = "Restore" };
    private readonly Button _openFolder = new() { Content = "Open backups" };
    private readonly TextBlock _status = Ui.Text("Press Scan. Scanning changes nothing.", 13);
    private readonly TextBlock _details = Ui.Text("Select an entry to see why it was flagged.", 12, "MutedBrush");
    private bool _busy;

    public int ItemCount => _items.Count;
    public int TickedCount => _items.Count(i => i.Check.IsChecked == true);
    public int GroupCount => _tree.Items.Count;

    public RegistryCleanerView()
    {
        var store = new RegistryStore();
        _scanner = new RegistryScanner(store);
        _folders = new BackupFolders(_isAdmin);
        _service = new RegistryCleanerService(store, _scanner, _folders);

        _scan.Role(ButtonRole.Refresh);
        _scan.Click += async (_, _) => await ScanAsync();
        _tickSafe.Click += (_, _) =>
        {
            foreach (var (issue, check) in _items)
                if (issue.Risk == Risk.Safe && check.IsEnabled) check.IsChecked = true;
            UpdateButtons();
        };
        _clean.Role(ButtonRole.Main);
        _clean.Click += async (_, _) => await CleanAsync();
        _restore.Click += async (_, _) => await RestoreAsync();
        _openFolder.Click += (_, _) => OpenBackups();

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var b in new[] { _scan, _tickSafe, _clean, _restore, _openFolder })
        {
            b.Margin = new Thickness(0, 0, 8, 4);
            buttons.Children.Add(b);
        }
        var top = Ui.Stack(Ui.Text("Registry Cleaner", 26, weight: FontWeights.SemiBold),
            Ui.Text("Cleaning the registry rarely makes a PC faster, and removing the wrong entry can break software. " +
                    "Only remove entries you recognize." +
                    (_isAdmin ? "" : " Machine-wide (HKLM) entries are view-only without administrator rights."),
                    12, "MutedBrush"), buttons, _status);

        var detailCard = Ui.Card(_details);
        detailCard.Padding = new Thickness(12);
        detailCard.Margin = new Thickness(0, 8, 12, 0);
        detailCard.MinHeight = 90;
        var treeCard = Ui.Card(_tree);
        treeCard.Padding = new Thickness(8);
        treeCard.Margin = new Thickness(0, 0, 12, 0);

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(detailCard, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(detailCard);
        dock.Children.Add(treeCard);
        Content = dock;

        _tree.SelectedItemChanged += (_, e) =>
        {
            if ((e.NewValue as TreeViewItem)?.Tag is RegistryIssue i)
                _details.Text = $"{i.Risk}: {i.Reason}\n{i.Location}\n" +
                                (i.ValueName == null ? "Would remove this key and its subkeys." : "Would remove this one value.");
        };
    }

    private static Brush RiskColor(Risk risk) => Ui.Brush(risk switch
    {
        Risk.Safe => "GoodBrush",
        Risk.Moderate => "WarnBrush",
        _ => "BadBrush"
    });

    public async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _status.Text = "Scanning...";
        try
        {
            var issues = await Task.Run(() => _scanner.Scan());
            Populate(issues);
            _status.Text = $"{issues.Count} entries to review. Nothing is ticked." +
                           (_scanner.Unreadable > 0 ? $" {_scanner.Unreadable} locations could not be read." : "");
        }
        catch (Exception ex) { Populate(new List<RegistryIssue>()); _status.Text = "Scan failed: " + ex.Message; }
        finally { SetBusy(false); }
    }

    public void Populate(IReadOnlyList<RegistryIssue> issues)
    {
        _tree.Items.Clear();
        _items.Clear();
        _details.Text = "Select an entry to see why it was flagged.";
        foreach (var kind in Enum.GetValues<IssueKind>())
        {
            var group = issues.Where(i => i.Kind == kind).OrderBy(i => i.Risk).ThenBy(i => i.Title).ToList();
            var node = new TreeViewItem
            {
                Header = Ui.Text($"{RegistryScanner.CategoryName(kind)} ({group.Count})", 13, weight: FontWeights.SemiBold),
                IsExpanded = group.Count is > 0 and <= 25
            };
            foreach (var issue in group)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = issue.Risk.ToString(), Foreground = RiskColor(issue.Risk), Width = 72,
                    FontWeight = FontWeights.SemiBold
                });
                row.Children.Add(new TextBlock
                {
                    Text = $"{issue.Title}   [{(issue.Hive == Hive.CurrentUser ? "HKCU" : "HKLM")}]",
                    Foreground = Ui.Brush("TextBrush")
                });
                var check = new CheckBox { Content = row, IsEnabled = _service.CanDelete(issue), Margin = new Thickness(0, 2, 0, 2) };
                System.Windows.Automation.AutomationProperties.SetName(check, $"{issue.Risk} {issue.Title}");
                check.Click += (_, _) => UpdateButtons();
                var child = new TreeViewItem { Header = check, Tag = issue };
                check.GotFocus += (_, _) => child.IsSelected = true;
                check.PreviewMouseDown += (_, _) => child.IsSelected = true;
                node.Items.Add(child);
                _items.Add((issue, check));
            }
            _tree.Items.Add(node);
        }
        UpdateButtons();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _scan.IsEnabled = _restore.IsEnabled = !busy;
        _tree.IsEnabled = !busy;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _clean.IsEnabled = !_busy && TickedCount > 0;
        _tickSafe.IsEnabled = !_busy && _items.Any(i => i.Issue.Risk == Risk.Safe && i.Check.IsEnabled);
    }

    private static bool Ask(string text, string title) => MessageBox.Show(text, title, MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task CleanAsync()
    {
        if (_busy) return;
        var picked = _items.Where(i => i.Check.IsChecked == true).Select(i => i.Issue).ToList();
        if (picked.Count == 0) return;
        var risky = picked.Count(i => i.Risk == Risk.Risky);
        if (risky > 0 && !Ask($"{risky} ticked entries are rated Risky. Removing them could stop software or " +
                              "file types from working.\n\nContinue?", "Risky entries")) return;
        var names = string.Join("\n", picked.Take(12).Select(i => $"• [{i.Risk}] {i.Title}"));
        if (picked.Count > 12) names += $"\n...and {picked.Count - 12} more";
        if (!Ask($"A .reg backup will be saved first, then these entries deleted:\n\n{names}", "Confirm")) return;

        SetBusy(true);
        _status.Text = "Backing up...";
        string message;
        try
        {
            var report = await Task.Run(() => _service.Clean(picked));
            message = $"Deleted {report.Deleted} of {picked.Count}." +
                      (report.BackupPath == null ? " No backup was needed." : " Backup: " + report.BackupPath);
            if (report.Problems.Count > 0) message += "\n" + string.Join("\n", report.Problems);
            ActivityLog.Post(ActivityLog.Cleanup, "Registry cleanup", report.Problems.Count == 0, null, message);
        }
        catch (Exception ex)
        {
            message = "Cleanup stopped: " + ex.Message;
            ActivityLog.Post(ActivityLog.Cleanup, "Registry cleanup", false, null, message);
        }
        finally { SetBusy(false); }
        await ScanAsync();
        _details.Text = message;
        _status.Text = "Cleanup finished. See details below.";
    }

    private async Task RestoreAsync()
    {
        if (_busy) return;
        var latest = await Task.Run(() => _folders.Latest());
        if (_busy) return;
        if (latest == null)
        {
            _status.Text = "No backup found." + (_isAdmin ? "" : " Backups made as administrator need administrator rights.");
            return;
        }
        var when = await Task.Run(() => File.GetLastWriteTime(latest.Value.Path));
        if (!Ask($"Re-apply the latest backup from {when:g}?\n\n{latest.Value.Path}\n\n" +
                 "Missing entries are put back. Values that exist now are not overwritten.", "Restore")) return;
        SetBusy(true);
        string message;
        try
        {
            var r = await Task.Run(() => _service.RestoreLatest());
            message = $"Restored {r.Restored} value(s). {r.AlreadyPresent} already present, " +
                      $"{r.KeptNewer} newer value(s) kept.\nFrom: {r.BackupPath}";
            if (r.Problems.Count > 0) message += "\nProblems:\n" + string.Join("\n", r.Problems);
        }
        catch (Exception ex) { message = "Restore failed: " + ex.Message; }
        finally { SetBusy(false); }
        ActivityLog.Post("Registry", "Registry backup restore", !message.StartsWith("Restore failed"), null, message);
        await ScanAsync();
        _details.Text = message;
        _status.Text = "Restore finished. See details below.";
    }

    private void OpenBackups()
    {
        // Explorer cannot usually open the administrators-only folder, so show the latest folder in use.
        var folder = _folders.Latest() is { } latest ? Path.GetDirectoryName(latest.Path)! : _folders.User;
        if (!Directory.Exists(folder)) { _status.Text = "No backups yet."; return; }
        try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{folder}\"") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { _status.Text = ex.Message; }
    }
}
