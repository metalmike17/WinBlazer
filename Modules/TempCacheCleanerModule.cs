using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;

namespace PCOptimizer.Modules;

public sealed class TempCacheCleanerModule : IQuickActionProvider
{
    public IEnumerable<QuickAction> QuickActions(AppContextServices services)
    {
        yield return new QuickAction("clean", 1, () => "Clean (Quick Clean)...", confirm => QuickRunners.CleanAsync(
            CleanRunner.Categories(CleanMode.Quick), services.Settings.Current.Cleanup.MinimumAgeDays,
            CleanupEngine.IsAdministrator(), confirm));
    }

    public string Id => "temp-cache-cleaner";
    public string Title => "Temp & Cache";
    public string Icon => "";
    public string Description => "Quick Clean, Deep Clean and automatic cleaning";
    public int Order => 15;
    public FrameworkElement CreateView(AppContextServices services) => new TempCacheCleanerView(services);
}

public sealed class TempCacheCleanerView : UserControl
{
    private readonly AppContextServices _services;
    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly TaskSchedulerService _tasks = new();

    private readonly Button _quick = new() { Content = "Quick Clean" };
    private readonly Button _deep = new() { Content = "Deep Clean" };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly ProgressBar _bar = new() { Height = 12, Maximum = 100, Margin = new Thickness(0, 6, 0, 8) };
    private readonly TextBlock _freed = Ui.Text("0 bytes freed", 26, "AccentBrush", FontWeights.SemiBold);
    private readonly TextBlock _current = Ui.Text("Ready.", 12, "MutedBrush");
    private readonly TextBlock _report = Ui.Text("", 12);
    private readonly Border _reportCard;

    private readonly ComboBox _frequency = Combo(Enum.GetValues<AutoFrequency>(), AutoFrequency.Weekly);
    private readonly ComboBox _weekDay = Combo(Enum.GetValues<DayOfWeek>(), DayOfWeek.Sunday);
    private readonly ComboBox _monthDay = Combo(Enumerable.Range(1, 28).ToArray(), 1);
    private readonly ComboBox _mode = Combo(Enum.GetValues<CleanMode>(), CleanMode.Quick);
    private readonly TextBox _time = new() { Text = "12:00", Width = 70, Padding = new Thickness(6, 3, 6, 3) };
    private readonly CheckBox _recycle = new() { Content = "Also empty the Recycle Bin automatically" };
    private readonly TextBlock _scheduleStatus = Ui.Text("Checking Task Scheduler...", 12, "AccentBrush");
    private readonly Button _save = new() { Content = "Save schedule..." };
    private readonly Button _remove = new() { Content = "Remove schedule", IsEnabled = false };
    private CancellationTokenSource? _cancellation;

    public bool Busy => _cancellation != null;

    private static ComboBox Combo<T>(T[] items, T selected) => new()
    {
        ItemsSource = items, SelectedItem = selected, MinWidth = 110, Margin = new Thickness(0, 0, 10, 6)
    };

    public TempCacheCleanerView(AppContextServices services)
    {
        _services = services;
        _bar.Foreground = Ui.Brush("AccentBrush");
        _bar.Background = Ui.Brush("GlassBrush");
        _bar.BorderBrush = Ui.Brush("EdgeBrush");

        var choices = new Grid();
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        choices.ColumnDefinitions.Add(new ColumnDefinition());
        Border Choice(Button button, string text, int column)
        {
            button.HorizontalAlignment = HorizontalAlignment.Left;
            var content = new DockPanel();
            button.VerticalAlignment = VerticalAlignment.Bottom;
            DockPanel.SetDock(button, Dock.Bottom);   // both buttons line up, however long the text is
            content.Children.Add(button);
            content.Children.Add(Ui.Text(text, 12, "MutedBrush"));
            var card = Ui.Card(content);
            card.Margin = new Thickness(0, 0, 12, 10);
            Grid.SetColumn(card, column);
            choices.Children.Add(card);
            return card;
        }
        Choice(_quick, "Old user temp files and the Recycle Bin.", 0);
        Choice(_deep, "Temp files, browser and GPU caches, update downloads, Delivery Optimization, memory dumps, " +
                      "error reports, IIS logs and Windows logs. Does not empty the Recycle Bin." +
                      (_isAdmin ? "" : " Some items need administrator rights and will be skipped."), 1);

        _cancel.HorizontalAlignment = HorizontalAlignment.Left;
        var progress = Ui.Card(Ui.Stack(_freed, _bar, _current, _cancel));
        progress.Margin = new Thickness(0, 0, 12, 10);

        _report.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _reportCard = Ui.Card(_report);
        _reportCard.Margin = new Thickness(0, 0, 12, 10);
        _reportCard.Visibility = Visibility.Collapsed;

        var page = Ui.Page("Temp File & Cache Cleaner",
            "Each button checks first, shows what it will delete, and asks once. Deletion is permanent.");
        page.Children.Add(choices);
        page.Children.Add(progress);
        page.Children.Add(_reportCard);
        page.Children.Add(ScheduleCard());
        Content = Ui.Scroll(page);

        _quick.Role(ButtonRole.Main);
        _quick.Click += async (_, _) => await CleanAsync(CleanMode.Quick);
        _deep.Click += async (_, _) => await CleanAsync(CleanMode.Deep);
        _cancel.Role(ButtonRole.Cancel);
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _save.Click += async (_, _) => await SaveScheduleAsync();
        _remove.Click += async (_, _) => await RemoveScheduleAsync();
        Loaded += async (_, _) => await RefreshScheduleAsync();
    }

    private Border ScheduleCard()
    {
        WrapPanel Row(params (string Label, FrameworkElement Control)[] fields)
        {
            var row = new WrapPanel();
            foreach (var (label, control) in fields)
            {
                var text = Ui.Text(label, 12, "MutedBrush");
                text.Margin = new Thickness(0, 4, 6, 6);
                System.Windows.Automation.AutomationProperties.SetName(control, label);
                row.Children.Add(text);
                row.Children.Add(control);
            }
            return row;
        }
        _time.Margin = new Thickness(0, 0, 10, 6);
        var weekly = Row(("Day of week", _weekDay));
        var monthly = Row(("Day of month", _monthDay));
        void Update()
        {
            weekly.Visibility = (AutoFrequency)_frequency.SelectedItem == AutoFrequency.Weekly
                ? Visibility.Visible : Visibility.Collapsed;
            monthly.Visibility = (AutoFrequency)_frequency.SelectedItem == AutoFrequency.Monthly
                ? Visibility.Visible : Visibility.Collapsed;
            var quick = (CleanMode)_mode.SelectedItem == CleanMode.Quick;
            _recycle.IsEnabled = quick;
            if (!quick) _recycle.IsChecked = false;
        }
        _frequency.SelectionChanged += (_, _) => Update();
        _mode.SelectionChanged += (_, _) => Update();
        Update();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(_save);
        buttons.Children.Add(_remove);
        var card = Ui.Card(Ui.Stack(
            Ui.Text("Automatic cleaning (Windows Task Scheduler)", 16, weight: FontWeights.SemiBold), _scheduleStatus,
            Row(("How often", _frequency), ("Time (24-hour)", _time), ("Cleaning type", _mode)), weekly, monthly, _recycle,
            Ui.Text("Runs while you are signed in, even if this app is closed, and runs later if a time was missed. " +
                    "It never uses administrator rights, so protected items are skipped. Running browsers are skipped. " +
                    "If you move the app, save the schedule again. Remove the schedule before deleting the app.",
                    11, "MutedBrush"), buttons));
        card.Margin = new Thickness(0, 0, 12, 10);
        return card;
    }

    private static bool Ask(string text) => MessageBox.Show(text, "Temp & Cache Cleaner", MessageBoxButton.YesNo,
        MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void SetBusy(bool busy)
    {
        _quick.IsEnabled = _deep.IsEnabled = !busy;
        _cancel.IsEnabled = busy;
    }

    private async Task CleanAsync(CleanMode mode)
    {
        if (Busy) return;
        var source = _cancellation = new CancellationTokenSource();
        var token = source.Token;
        var age = _services.Settings.Current.Cleanup.MinimumAgeDays;
        var state = new CleanProgressState();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => ShowProgress(state);
        SetBusy(true);
        _reportCard.Visibility = Visibility.Collapsed;
        _freed.Text = "0 bytes freed";
        _bar.Value = 0;
        _bar.IsIndeterminate = true;
        timer.Start();
        try
        {
            var plan = await Task.Run(() =>
                CleanRunner.Plan(CleanRunner.Categories(mode), age, _isAdmin, null, state, token), token);
            timer.Stop();
            var run = plan.Where(p => p.WillRun).ToList();
            var skipped = plan.Where(p => p.Blocked != null).ToList();
            var skippedText = string.Join("\n", skipped.Select(p => $"  {p.Scan.Category.Name}: {p.Blocked}"));
            if (run.Count == 0)
            {
                _current.Text = "Nothing to clean right now.";
                if (skipped.Count > 0) ShowReport("NOT CLEANED\n" + skippedText);
                return;
            }
            var list = string.Join("\n", run.Select(p => $"• {p.Scan.Category.Name}: " +
                (p.Scan.Incomplete && p.Scan.Bytes == 0 ? "size unknown" : CleanupEngine.Size(p.Scan.Bytes))));
            var question = $"{mode} Clean will permanently delete:\n\n{list}\n\nTemp, report and log files must be " +
                           $"older than {age} days.";
            if (run.Any(p => p.Scan.Category.Kind == CategoryKind.RecycleBin))
                question += "\nEVERYTHING in the Recycle Bin will be removed.";
            if (run.Any(p => p.Scan.Category.Id == "crash-dumps"))
                question += "\nMemory dumps are useful if you are troubleshooting crashes.";
            if (skipped.Count > 0) question += "\n\nWill be skipped:\n" + skippedText;
            _bar.IsIndeterminate = false;
            _current.Text = "Waiting for confirmation.";
            if (!Ask(question + "\n\nContinue?")) { _current.Text = "Nothing was deleted."; return; }

            timer.Start();
            var (results, failures) = await Task.Run(() => CleanRunner.Run(run, age, _isAdmin, state, token));
            timer.Stop();
            ShowProgress(state);
            var cancelled = token.IsCancellationRequested;
            ActivityLog.Post(ActivityLog.Cleanup, $"{mode} Clean" + (cancelled ? " (cancelled part way)" : ""),
                failures.Count == 0, state.Freed, string.Join("; ",
                    results.Select(r => $"{r.Category.Name}: {CleanupEngine.Size(r.BytesDeleted)}").Concat(failures)));
            if (!cancelled) _bar.Value = 100;
            _current.Text = cancelled ? "Cancelled. Files already deleted stay deleted." : "Finished.";
            var report = $"REMOVED {CleanupEngine.Size(state.Freed)}\n\n" + string.Join("\n", results.Select(r =>
                $"{r.Category.Name}: {CleanupEngine.Size(r.BytesDeleted)} ({r.Deleted:N0} removed, {r.Skipped:N0} skipped)" +
                (r.Message == null ? "" : "\n    " + r.Message)));
            if (failures.Count > 0) report += "\n" + string.Join("\n", failures.Select(f => "Failed - " + f));
            if (skipped.Count > 0) report += "\n\nNOT CLEANED\n" + skippedText;
            ShowReport(report);
        }
        catch (OperationCanceledException) { _current.Text = "Cancelled. Nothing was deleted."; }
        catch (Exception ex) { _current.Text = "Cleaning stopped: " + ex.Message; }
        finally
        {
            timer.Stop();
            _bar.IsIndeterminate = false;
            _cancellation = null;
            source.Dispose();
            SetBusy(false);
        }
    }

    private void ShowProgress(CleanProgressState state)
    {
        _freed.Text = CleanupEngine.Size(state.Freed) + " freed";
        _current.Text = state.Current;
        if (state.Total > 0)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Min(100, 100.0 * state.Done / state.Total);
        }
    }

    private void ShowReport(string text)
    {
        _report.Text = text;
        _reportCard.Visibility = Visibility.Visible;
    }

    private async Task RefreshScheduleAsync()
    {
        try
        {
            var task = await Task.Run(_tasks.Read);
            _remove.IsEnabled = task != null;
            _scheduleStatus.Text = task == null ? "Automatic cleaning is off."
                : "Current schedule: " + TaskSchedulerService.Describe(task) +
                  (_tasks.PointsElsewhere(task) ? " It starts a different copy of the app; save again to update it." : "");
        }
        catch (Exception ex) { _scheduleStatus.Text = "Could not read Task Scheduler: " + ex.Message; }
    }

    private async Task SaveScheduleAsync()
    {
        if (!TimeOnly.TryParseExact(_time.Text.Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var time))
        {
            _scheduleStatus.Text = "Enter the time in 24-hour form, such as 03:30 or 18:00.";
            return;
        }
        var schedule = new AutoSchedule((AutoFrequency)_frequency.SelectedItem, time, (DayOfWeek)_weekDay.SelectedItem,
            (int)_monthDay.SelectedItem, (CleanMode)_mode.SelectedItem, _recycle.IsChecked == true);
        var what = schedule.Mode == CleanMode.Deep
            ? "old temp files, browser and GPU caches, memory dumps in your folders and old error reports. " +
              "Items needing administrator rights are skipped."
            : "old user temp files" + (schedule.IncludeRecycleBin ? " and EVERYTHING in the Recycle Bin." : ".");
        if (!Ask($"Create a {schedule.Frequency.ToString().ToLowerInvariant()} task at {time:HH\\:mm}?\n\n" +
                 $"Without asking again, each run will permanently delete {what}")) return;
        _save.IsEnabled = false;
        try
        {
            await Task.Run(() => _tasks.Save(schedule));
            await RefreshScheduleAsync();
        }
        catch (Exception ex) { _scheduleStatus.Text = ex.Message; }
        finally { _save.IsEnabled = true; }
    }

    private async Task RemoveScheduleAsync()
    {
        if (!Ask("Turn off automatic cleaning and remove the task?")) return;
        try { await Task.Run(_tasks.Delete); await RefreshScheduleAsync(); }
        catch (Exception ex) { _scheduleStatus.Text = ex.Message; }
    }
}
