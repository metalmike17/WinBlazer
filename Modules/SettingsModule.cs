using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Controls;
using PCOptimizer.Core;

namespace PCOptimizer.Modules;

public sealed class SettingsModule : IModule
{
    public string Id => "settings";
    public string Title => "Settings";
    public string Icon => "";
    public string Description => "Modules, schedules and cleanup preferences";
    public int Order => 1000;
    public bool CanHide => false;

    public FrameworkElement CreateView(AppContextServices services)
    {
        var settings = services.Settings;
        var page = Ui.Page(Title, "Changes are saved immediately");
        var status = Ui.Text(settings.LoadWarning ?? "", 12, "AccentBrush");
        page.Children.Add(status);

        async void Save()
        {
            try { await settings.SaveAsync(); status.Text = "Saved."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { status.Text = "Could not save settings: " + ex.Message; }
        }

        CheckBox Toggle(string label, bool value, Action<bool> apply)
        {
            var box = new CheckBox { Content = label, IsChecked = value };
            box.Click += (_, _) => { apply(box.IsChecked == true); Save(); };
            return box;
        }

        void Section(string title, params UIElement[] rows)
        {
            var panel = Ui.Stack(Ui.Text(title, 15, weight: FontWeights.SemiBold));
            foreach (var row in rows) panel.Children.Add(row);
            var card = Ui.Card(panel);
            card.Margin = new Thickness(0, 6, 12, 8);
            page.Children.Add(card);
        }

        var light = Toggle("Light theme (Ctrl+T)", settings.Current.LightTheme, v => settings.Current.LightTheme = v);
        void ShowTheme() => light.IsChecked = Themes.ThemeManager.IsLight;
        light.Loaded += (_, _) => { Themes.ThemeManager.Changed -= ShowTheme; Themes.ThemeManager.Changed += ShowTheme; ShowTheme(); };
        light.Unloaded += (_, _) => Themes.ThemeManager.Changed -= ShowTheme;
        Section("Appearance", light, Ui.Text("Press F1 to see all keyboard shortcuts. Animations follow the Windows " +
                "\"Animation effects\" setting.", 12, "MutedBrush"));

        Section("Modules shown in the sidebar", services.Modules.All.Where(m => m.CanHide).Select(m =>
            (UIElement)Toggle(m.Title, !settings.Current.HiddenModules.Contains(m.Id), shown =>
            {
                if (shown) settings.Current.HiddenModules.Remove(m.Id);
                else settings.Current.HiddenModules.Add(m.Id);
            })).ToArray());

        var schedules = new List<UIElement>
        {
            Ui.Text("Scans run only while the app is open or in the tray. They never delete anything. " +
                    "Hiding a module does not turn off its schedule.", 12, "MutedBrush")
        };
        foreach (var module in services.Modules.All.OfType<IScannableModule>())
        {
            settings.Current.Schedules.TryGetValue(module.Id, out var saved);
            var choice = new ComboBox
            {
                ItemsSource = Enum.GetValues<ScanFrequency>(), Width = 130,
                SelectedItem = saved?.Frequency ?? ScanFrequency.Off
            };
            choice.SelectionChanged += (_, _) =>
            {
                if (!settings.Current.Schedules.TryGetValue(module.Id, out var s))
                    settings.Current.Schedules[module.Id] = s = new ModuleSchedule();
                s.Frequency = (ScanFrequency)choice.SelectedItem;
                Save();
            };
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(choice, Dock.Right);
            row.Children.Add(choice);
            row.Children.Add(Ui.Text(module.Title));
            schedules.Add(row);
        }
        Section("Automatic scans", schedules.ToArray());

        var age = new ComboBox { Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
        var ages = new SortedSet<int> { 1, 3, 7, 14, 30, 90, settings.Current.Cleanup.MinimumAgeDays };
        age.ItemsSource = ages;
        age.SelectedItem = settings.Current.Cleanup.MinimumAgeDays;
        age.SelectionChanged += (_, _) => { settings.Current.Cleanup.MinimumAgeDays = (int)age.SelectedItem; Save(); };
        Section("Cleanup preferences", Ui.Text("Only include temp files older than this many days:"), age,
                Ui.Text("Deleting always asks for confirmation.", 12, "MutedBrush"));

        Section("App behavior",
            Toggle("Minimize to the system tray", settings.Current.MinimizeToTray,
                   v => settings.Current.MinimizeToTray = v),
            Toggle("Notify me after a scheduled scan", settings.Current.NotifyAfterScheduledScan,
                   v => settings.Current.NotifyAfterScheduledScan = v),
            Toggle("Show a notification when cleanup finishes", settings.Current.NotifyAfterCleanup,
                   v => settings.Current.NotifyAfterCleanup = v));

        page.Children.Add(Ui.Text("Settings file: " + settings.FilePath, 11, "MutedBrush"));
        return Ui.Scroll(page);
    }
}
