using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Themes;

namespace PCOptimizer;

/// <summary>The shell knows nothing about individual modules.</summary>
public partial class MainWindow : Window, IShell
{
    private readonly List<(string ModuleId, QuickAction Action)> _quickActions;
    private bool _quickBusy;
    private readonly List<Shortcut> _shortcuts = new();
    private string _trayModules = "";

    public ShortcutHelp Help { get; } = new();
    public IReadOnlyList<Shortcut> ShortcutList => _shortcuts;

    public SearchOverlay Search { get; }
    public ToastHost Toast { get; } = new();
    public IReadOnlyList<string> TrayLabels => _tray?.MenuLabels ?? new List<string>();

    private readonly AppContextServices _services;
    private readonly Dictionary<string, FrameworkElement> _views = new();
    private readonly ScanScheduler _scheduler;
    private TrayIcon? _tray;

    public string CurrentModuleId { get; private set; } = "";
    public FrameworkElement CurrentView => (FrameworkElement)PageHost.Content;
    public IReadOnlyList<string> SidebarIds =>
        Sidebar.Children.OfType<RadioButton>().Select(b => (string)b.Tag).ToList();

    public MainWindow(AppContextServices services, bool enableBackgroundFeatures)
    {
        InitializeComponent();
        _services = services;
        _scheduler = new ScanScheduler(services);
        services.Shell = this;
        var index = Services.SearchIndex.Build(services.Modules);
        Search = new SearchOverlay(() =>
        {
            var shown = VisibleIds();
            return index.Where(entry => shown.Contains(entry.ModuleId)).ToList();
        }, entry => Navigate(entry.ModuleId, entry.Section));
        Root.Children.Add(Toast);
        Root.Children.Add(Search);
        Root.Children.Add(Help);
        Icon = AppLogo.Icon();
        SourceInitialized += (_, _) => ThemeManager.StyleTitleBar(this);
        SearchButton.Click += (_, _) => Search.Open();
        ThemeButton.Click += async (_, _) => await ToggleThemeAsync();
        BuildShortcuts();
        PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (RunShortcut(key, Keyboard.Modifiers)) e.Handled = true;
        };
        ContextMenus.Attach(Root, PageMenu);
        void ApplyTheme()
        {
            if (ThemeManager.IsLight != services.Settings.Current.LightTheme)
                ThemeManager.Apply(services.Settings.Current.LightTheme);
        }
        ApplyTheme();
        services.Settings.Changed += ApplyTheme;
        Closed += (_, _) => services.Settings.Changed -= ApplyTheme;        void OnActivity(Services.Activity activity) => Dispatcher.BeginInvoke(() => ShowCleanupNotice(activity));
        Services.ActivityLog.Added += OnActivity;
        Closed += (_, _) => Services.ActivityLog.Added -= OnActivity;
        BuildSidebar();
        services.Settings.Changed += BuildSidebar;
        // Started before any page is shown, because pages may depend on their background service.
        var background = new List<IDisposable>();
        foreach (var module in services.Modules.All.OfType<IBackgroundModule>())
            background.Add(module.StartBackground(services));
        Closed += (_, _) => background.ForEach(b => b.Dispose());
        // Collected after background services start, because Game Mode's action needs its service.
        _quickActions = services.Modules.All.OfType<IQuickActionProvider>()
            .SelectMany(m => m.QuickActions(services).Select(a => (m.Id, Action: a)))
            .OrderBy(a => a.Action.Order).ToList();
        Closing += (_, e) =>
        {
            if (!_quickBusy) return;
            e.Cancel = true;
            MessageBox.Show("A quick action is still running. Wait for it to finish.", "WinBlazer");
        };
        Navigate(services.Modules.Visible(services.Settings.Current).First().Id);

        if (enableBackgroundFeatures)
        {
            BuildTray();
            services.Settings.Changed += BuildTray;   // modules turned off leave the tray menu
            Closed += (_, _) => services.Settings.Changed -= BuildTray;            _scheduler.ScanFinished += (title, message) =>
            {
                if (_services.Settings.Current.NotifyAfterScheduledScan) _tray?.Notify(title, message);
            };
            _scheduler.Start();
        }

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _tray != null && _services.Settings.Current.MinimizeToTray)
                Hide();
        };
        Closed += (_, _) => { _scheduler.Stop(); _tray?.Dispose(); _tray = null; };
    }

    public string? FindQuickActionLabel(string actionId) =>
        _quickActions.FirstOrDefault(a => a.Action.Id == actionId).Action?.Label();

    private HashSet<string> VisibleIds() =>
        _services.Modules.Visible(_services.Settings.Current).Select(m => m.Id).ToHashSet();

    private void BuildTray()
    {
        var shown = VisibleIds();
        var actions = _quickActions.Where(a => shown.Contains(a.ModuleId)).Select(a => a.Action).ToList();
        var key = string.Join("|", actions.Select(a => a.Id));
        if (_tray != null && key == _trayModules) return;
        _trayModules = key;
        _tray?.Dispose();
        _tray = new TrayIcon(Restore, Close, actions.Select(a => (a.Label, (Action)(() => RunAndNotify(a.Id)))).ToList());
    }

    private async void RunAndNotify(string actionId)
    {
        var message = await RunQuickActionAsync(actionId);
        if (!(actionId == "clean" && message.StartsWith("Freed ") && _services.Settings.Current.NotifyAfterCleanup))
            Notify("WinBlazer", message);
    }

    public async Task ToggleThemeAsync()
    {
        _services.Settings.Current.LightTheme = !ThemeManager.IsLight;
        ThemeManager.Apply(_services.Settings.Current.LightTheme);
        try { await _services.Settings.SaveAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Toast.Show("Theme not saved", ex.Message); }
    }

    private void BuildShortcuts()
    {
        void Add(Key key, ModifierKeys modifiers, string keys, string description, Func<bool> run) =>
            _shortcuts.Add(new Shortcut(key, modifiers, keys, description, run));
        static bool Done(Action action) { action(); return true; }
        bool Press(ButtonRole role) => PageHost.Content is DependencyObject page && Shortcuts.Press(page, role);
        const ModifierKeys ctrl = ModifierKeys.Control, ctrlShift = ModifierKeys.Control | ModifierKeys.Shift;

        Add(Key.F1, ModifierKeys.None, "F1", "Show this list", () => Done(ShowHelp));
        Add(Key.K, ctrl, "Ctrl+K", "Search", () => Done(() => { Help.Close(); Search.Open(); }));
        Add(Key.F5, ModifierKeys.None, "F5", "Refresh or scan the current page", () => Press(ButtonRole.Refresh));
        Add(Key.Return, ctrl, "Ctrl+Enter", "Run the current page's main action", () => Press(ButtonRole.Main));
        Add(Key.Escape, ModifierKeys.None, "Esc", "Cancel the running scan or test", () => Press(ButtonRole.Cancel));
        for (var n = 1; n <= 9; n++)
        {
            var position = n - 1;
            Add(Key.D0 + n, ctrl, "Ctrl+" + n, $"Open sidebar page {n}", () => GoTo(position));
        }
        Add(Key.Next, ctrl, "Ctrl+Page Down", "Next page", () => Step(1));
        Add(Key.Prior, ctrl, "Ctrl+Page Up", "Previous page", () => Step(-1));
        Add(Key.H, ctrl, "Ctrl+H", "History", () => Done(() => Navigate("history", null)));
        Add(Key.OemComma, ctrl, "Ctrl+,", "Settings", () => Done(() => Navigate("settings", null)));
        Add(Key.T, ctrl, "Ctrl+T", "Light or dark theme", () => Done(() => _ = ToggleThemeAsync()));
        Add(Key.C, ctrlShift, "Ctrl+Shift+C", "Quick Clean (asks first)", () => Done(() => RunAndNotify("clean")));
        Add(Key.O, ctrlShift, "Ctrl+Shift+O", "Optimize (asks first)", () => Done(() => RunAndNotify("optimize")));
        Add(Key.G, ctrlShift, "Ctrl+Shift+G", "Open Game Mode", () => Done(() => Navigate("game-mode", null)));
    }

    /// <summary>Returns false when the key should be left to the focused control.</summary>
    public bool RunShortcut(Key key, ModifierKeys modifiers)
    {
        if (Help.IsOpen && key == Key.Escape) { Help.Close(); return true; }
        var shortcut = _shortcuts.FirstOrDefault(s => s.Key == key && s.Modifiers == modifiers);
        if (shortcut == null) return false;
        // While an overlay is open, only the keys that open one act on the app.
        if ((Search.IsOpen || Help.IsOpen) && key is not (Key.K or Key.F1)) return false;
        return shortcut.Run();
    }

    private void ShowHelp()
    {
        Search.Close();
        Help.Open(_shortcuts.Where(s => !(s.Modifiers == ModifierKeys.Control && s.Key is >= Key.D2 and <= Key.D9))
            .Select(s => s.Key == Key.D1 ? ("Ctrl+1 to Ctrl+9", "Open a sidebar page by position") : (s.Keys, s.Description))
            .Append(("Menu or Shift+F10", "Right-click menu for the focused item")));
    }

    private bool GoTo(int position)
    {
        var ids = SidebarIds;
        if (position < 0 || position >= ids.Count) return false;
        Navigate(ids[position]);
        return true;
    }

    private bool Step(int direction)
    {
        var ids = SidebarIds;
        var current = ids.ToList().IndexOf(CurrentModuleId);
        return ids.Count > 0 && GoTo((Math.Max(0, current) + direction + ids.Count) % ids.Count);
    }

    private IEnumerable<MenuAction> PageMenu()
    {
        if (PageHost.Content is DependencyObject page && Shortcuts.Find(page, ButtonRole.Refresh) != null)
            yield return new MenuAction("Refresh this page  (F5)", () => RunShortcut(Key.F5, ModifierKeys.None));
        yield return new MenuAction("Search  (Ctrl+K)", Search.Open);
        yield return new MenuAction("Switch light or dark theme  (Ctrl+T)", () => _ = ToggleThemeAsync());
        yield return new MenuAction("Keyboard shortcuts  (F1)", ShowHelp);
    }

    public async Task<string> RunQuickActionAsync(string actionId)
    {
        var action = _quickActions.FirstOrDefault(a => a.Action.Id == actionId).Action;
        if (action == null) return "That action is not available.";
        if (_quickBusy) return "Another quick action is still running.";
        _quickBusy = true;
        try
        {
            return await action.Run(question =>
            {
                Restore();   // so the question is not hidden behind other windows
                return MessageBox.Show(this, question, "WinBlazer", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            });
        }
        catch (Exception ex) { return "The action stopped: " + ex.Message; }
        finally { _quickBusy = false; }
    }

    /// <summary>In the window when it is in front; otherwise as a Windows notification.</summary>
    private void Notify(string title, string message)
    {
        if (IsVisible && WindowState != WindowState.Minimized && (IsActive || _tray == null)) Toast.Show(title, message);
        else _tray?.Notify(title, message);
    }

    private void ShowCleanupNotice(Services.Activity activity)
    {
        if (activity.Category != Services.ActivityLog.Cleanup || !_services.Settings.Current.NotifyAfterCleanup) return;
        Notify(activity.Success ? "Cleanup finished" : "Cleanup finished with problems",
               activity.BytesFreed is { } bytes
                   ? $"{activity.Action}: freed {Services.CleanupEngine.Size(bytes)}."
                   : $"{activity.Action}. See History for details.");
    }

    public void Navigate(string id, string? section)
    {
        Navigate(id);
        if (section != null && CurrentModuleId == id && CurrentView is ISectionHost host) host.ShowSection(section);
    }

    private void Restore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void BuildSidebar()
    {
        var visible = _services.Modules.Visible(_services.Settings.Current).ToList();
        var shownIds = visible.Select(m => m.Id).ToList();
        if (shownIds.SequenceEqual(SidebarIds)) return;

        Sidebar.Children.Clear();
        foreach (var module in visible)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = module.Icon, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
                Width = 30, VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock { Text = module.Title, FontSize = 14 });
            var button = new RadioButton
            {
                Content = row, Tag = module.Id, ToolTip = module.Description, GroupName = "nav",
                Style = (Style)FindResource("NavItem"), IsChecked = module.Id == CurrentModuleId
            };
            System.Windows.Automation.AutomationProperties.SetName(button, module.Title);
            button.Checked += (_, _) => Navigate((string)button.Tag);
            Sidebar.Children.Add(button);
        }
    }

    public void Navigate(string id)
    {
        if (id == CurrentModuleId) return;
        var module = _services.Modules.All.First(m => m.Id == id);
        if (!_views.TryGetValue(id, out var view))
        {
            try { view = module.CreateView(_services); _views[id] = view; }
            catch (Exception ex)   // one broken module must not take down the app
            {
                view = Controls.Ui.Text($"{module.Title} could not be opened: {ex.Message}");
            }
        }
        // Slides up when moving down the sidebar, and down when moving up.
        var order = _services.Modules.All.Select(m => m.Id).ToList();
        var direction = order.IndexOf(id) >= order.IndexOf(CurrentModuleId) ? 1 : -1;
        CurrentModuleId = id;
        PageHost.Content = view;
        foreach (var button in Sidebar.Children.OfType<RadioButton>())
            if ((string)button.Tag == id) button.IsChecked = true;
        Motion.Enter(PageHost, direction);
    }
}