using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Themes;

namespace PCOptimizer;

/// <summary>
/// First-run setup: administrator rights, then which modules to show. Closing the window changes
/// nothing, and the wizard appears again next time.
/// </summary>
public sealed class FirstRunWindow : Window
{
    private readonly SettingsService _settings;
    private readonly List<(string Id, CheckBox Box)> _modules = new();
    private readonly ContentControl _page = new() { Focusable = false };
    private readonly Button _back = new() { Content = "Back" }, _next = new() { Content = "Next", IsDefault = true };
    private readonly TextBlock _error = Ui.Text("", 12, "BadBrush");
    private readonly FrameworkElement _adminPage, _modulePage;
    private bool _saving;

    public bool Completed { get; private set; }
    /// <summary>True when an administrator copy was started and this one should exit.</summary>
    public bool Relaunched { get; private set; }
    public int Step { get; private set; }
    public int ModuleCount => _modules.Count;

    public FirstRunWindow(SettingsService settings, ModuleRegistry modules, bool isAdmin)
    {
        _settings = settings;
        Title = "Welcome to WinBlazer";
        Width = 600;
        Height = 620;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("NavyBrush");
        FontFamily = new FontFamily("Segoe UI");
        Icon = AppLogo.Icon();
        SourceInitialized += (_, _) => ThemeManager.StyleTitleBar(this);

        _adminPage = AdminPage(isAdmin);
        _modulePage = ModulePage(modules);

        _back.Click += (_, _) => ShowStep(0);
        _next.Click += async (_, _) => { if (Step == 0) ShowStep(1); else await FinishAsync(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new AppLogo { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) });
        header.Children.Add(Ui.Text("Welcome to WinBlazer", 22, weight: FontWeights.SemiBold));
        var bottom = Ui.Stack(_error, buttons);
        var dock = new DockPanel { Margin = new Thickness(22) };
        var frame = new Grid();
        frame.Children.Add(dock);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(bottom);
        dock.Children.Add(_page);
        Content = frame;
        ShowStep(0);
    }

    private FrameworkElement AdminPage(bool isAdmin)
    {
        var panel = Ui.Stack(Ui.Text("Step 1 of 2: Administrator rights", 15, weight: FontWeights.SemiBold),
            Ui.Text("Some features change Windows itself and need administrator rights, for example disk repair, " +
                    "installing driver updates, network resets and some cleanup and tweaks. Everything else works " +
                    "without them.", 13),
            Ui.Text("Windows will show its own permission prompt. This applies to this session only: the app does not " +
                    "set itself to always run as administrator. Each page also has its own restart button.",
                    12, "MutedBrush"));
        if (isAdmin) panel.Children.Add(Ui.Text("✓ The app is running as administrator.", 14, "GoodBrush", FontWeights.SemiBold));
        else
        {
            var elevate = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 8) };
            elevate.Click += (_, _) => Elevate();
            panel.Children.Add(elevate);
            panel.Children.Add(Ui.Text("Or choose Next to continue without administrator rights.", 12, "MutedBrush"));
        }
        return panel;
    }

    private FrameworkElement ModulePage(ModuleRegistry modules)
    {
        var list = new StackPanel();
        foreach (var module in modules.All.Where(m => m.CanHide))
        {
            var box = new CheckBox
            {
                Content = Ui.Stack(Ui.Text(module.Title, 13, weight: FontWeights.SemiBold),
                                   Ui.Text(module.Description, 11, "MutedBrush")),
                IsChecked = !_settings.Current.HiddenModules.Contains(module.Id), Margin = new Thickness(0, 2, 8, 2)
            };
            System.Windows.Automation.AutomationProperties.SetName(box, module.Title);
            _modules.Add((module.Id, box));
            list.Children.Add(box);
        }
        var all = new Button { Content = "Select all" };
        all.Click += (_, _) => _modules.ForEach(m => m.Box.IsChecked = true);
        var none = new Button { Content = "Select none" };
        none.Click += (_, _) => _modules.ForEach(m => m.Box.IsChecked = false);
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(all);
        row.Children.Add(none);
        var top = Ui.Stack(Ui.Text("Step 2 of 2: Choose your modules", 15, weight: FontWeights.SemiBold),
            Ui.Text("Unticked modules are left out of the sidebar, search and tray menu. Dashboard and Settings are " +
                    "always on, and you can change this later in Settings. Enabling a module does not run anything.",
                    12, "MutedBrush"), row);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(Ui.Scroll(list));
        return dock;
    }

    public void ShowStep(int step)
    {
        Step = step;
        _page.Content = step == 0 ? _adminPage : _modulePage;
        _back.IsEnabled = step > 0;
        _next.Content = step == 0 ? "Next" : "Finish";
        Motion.Enter(_page, 1);
    }

    public void SetEnabled(string moduleId, bool enabled) =>
        _modules.First(m => m.Id == moduleId).Box.IsChecked = enabled;

    /// <summary>Saves the choices.</summary>
    public async Task<bool> SaveChoicesAsync()
    {
        foreach (var (id, box) in _modules)
        {
            if (box.IsChecked == true) _settings.Current.HiddenModules.Remove(id);
            else _settings.Current.HiddenModules.Add(id);
        }
        _settings.Current.FirstRunCompleted = true;
        try { await _settings.SaveAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings.Current.FirstRunCompleted = false;
            _error.Text = "Could not save your choices: " + ex.Message;
            return false;
        }
        return Completed = true;
    }

    private async Task FinishAsync()
    {
        if (_saving) return;
        _saving = true;
        _next.IsEnabled = false;
        if (await SaveChoicesAsync()) Close();
        _next.IsEnabled = true;
        _saving = false;
    }

    private void Elevate()
    {
        if (CleanupEngine.IsAdministrator()) return;
        try
        {
            // The new copy shows this wizard again, now with administrator rights.
            PCOptimizer.Core.SingleInstance.StartElevatedCopy();
            Relaunched = true;
            Close();
        }
        catch (Win32Exception) { _error.Text = "WinBlazer did not restart as administrator. You can continue without administrator rights."; }
    }
}
