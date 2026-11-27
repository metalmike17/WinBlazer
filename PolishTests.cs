using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Themes;

namespace PCOptimizer;

/// <summary>
/// Theme, menus, shortcuts, background saving and the first-run wizard. Uses throwaway settings, and only
/// presses buttons made here.
/// </summary>
public static class PolishTests
{
    public static async Task RunAsync(MainWindow window, AppContextServices services, string work, string output,
                                      Action<string, bool> check, List<string> log, Action<Window, string> render)
    {
        // Background saving and History.
        var path = Path.Combine(work, "async.json");
        var saved = new SettingsService(path);
        var writes = new List<Task>();
        for (var days = 1; days <= 25; days++)
        {
            saved.Current.Cleanup.MinimumAgeDays = days;
            writes.Add(saved.SaveAsync());
        }
        await Task.WhenAll(writes);
        check("overlapping background saves keep the newest values",
              new SettingsService(path).Current.Cleanup.MinimumAgeDays == 25);

        ActivityLog.Configure(Directory.CreateDirectory(Path.Combine(work, "post-test")).FullName);
        for (var i = 0; i < 20; i++) ActivityLog.Post("Test", "posted " + i, true);
        var posted = ActivityLog.Read();
        check("posted history is written in order",
              posted.Count == 20 && posted[0].Action == "posted 19" && posted[19].Action == "posted 0");
        ActivityLog.Configure(work);

        // Context menus.
        var clicks = 0;
        var button = new Button { Content = "Do it" };
        button.Click += (_, _) => clicks++;
        var blocked = new Button { Content = "Locked", IsEnabled = false };
        blocked.Click += (_, _) => clicks += 100;
        var tick = new CheckBox { Content = "Option" };
        var card = Ui.Card(Ui.Stack(Ui.Text("Card title"), button, blocked, tick));
        var menu = ContextMenus.Build(card);
        check("a card menu offers copy and the card's own controls",
              ContextMenus.FindTarget(button) == card &&
              menu.Select(m => m.Label).SequenceEqual(new[] { "Copy text", "Do it", "Locked", "Turn on: Option" }));
        menu.Single(m => m.Label == "Do it").Run();
        menu.Single(m => m.Label == "Turn on: Option").Run();
        var locked = menu.Single(m => m.Label == "Locked");
        locked.Run();
        check("choosing a menu entry is the same as clicking, and disabled stays disabled",
              clicks == 1 && tick.IsChecked == true && !locked.Enabled);
        check("text boxes keep their own menu", ContextMenus.FindTarget(new TextBox()) == null);

        // Shortcut roles.
        var off = new Button { IsEnabled = false }.Role(ButtonRole.Refresh);
        check("shortcuts ignore disabled buttons and show their key",
              !Shortcuts.Press(Ui.Stack(off), ButtonRole.Refresh) && (string)off.ToolTip == "Keyboard: F5");
        var keys = window.ShortcutList.Select(s => (s.Key, s.Modifiers)).ToList();
        check("no two shortcuts use the same keys", keys.Count == keys.Distinct().Count());

        window.Navigate("dashboard");
        await Task.Delay(400);
        check("Ctrl+2 opens the second sidebar page",
              window.RunShortcut(Key.D2, ModifierKeys.Control) && window.CurrentModuleId == window.SidebarIds[1]);
        window.RunShortcut(Key.D1, ModifierKeys.Control);
        window.RunShortcut(Key.Prior, ModifierKeys.Control);
        check("Ctrl+Page Up wraps to the last page", window.CurrentModuleId == window.SidebarIds[^1]);
        window.RunShortcut(Key.H, ModifierKeys.Control);
        check("Ctrl+H opens History", window.CurrentModuleId == "history");
        window.RunShortcut(Key.F1, ModifierKeys.None);
        var blockedByHelp = !window.RunShortcut(Key.D1, ModifierKeys.Control);
        await Task.Delay(400);
        check("F1 lists shortcuts and blocks the others while open", window.Help.IsOpen && blockedByHelp);
        render(window, Path.Combine(output, "shortcuts.png"));
        window.RunShortcut(Key.Escape, ModifierKeys.None);
        check("Esc closes the list", !window.Help.IsOpen);
        window.RunShortcut(Key.K, ModifierKeys.Control);
        check("Ctrl+K opens search", window.Search.IsOpen);
        window.Search.Close();

        services.Settings.Current.HiddenModules.Add("registry-cleaner");
        window.Search.Open();
        window.Search.Query = "Registry Cleaner";
        await Task.Delay(100);
        var hiddenFound = window.Search.Selected?.ModuleId == "registry-cleaner";
        services.Settings.Current.HiddenModules.Remove("registry-cleaner");
        window.Search.Query = "Registry Cleaner ";
        await Task.Delay(100);
        check("search leaves out modules that are turned off",
              !hiddenFound && window.Search.Selected?.ModuleId == "registry-cleaner");
        window.Search.Close();

        // Theme.
        var live = (SolidColorBrush)Ui.Brush("TextBrush");
        var darkText = live.Color;
        foreach (var light in new[] { false, true })
        {
            ThemeManager.Apply(light);
            var weak = new[] { "TextBrush", "MutedBrush", "AccentBrush", "GoodBrush", "WarnBrush", "BadBrush", "PurpleBrush" }
                .Where(k => Contrast(ThemeManager.Color(k), ThemeManager.Color("NavyBrush")) < 4.5).ToList();
            check($"{(light ? "light" : "dark")} theme text colors are readable (4.5:1) {string.Join(" ", weak)}",
                  weak.Count == 0);
        }
        ThemeManager.Apply(false);
        window.RunShortcut(Key.T, ModifierKeys.Control);
        for (var i = 0; i < 100 && !new SettingsService(services.Settings.FilePath).Current.LightTheme; i++)
            await Task.Delay(50);
        check("Ctrl+T switches to light, recolors in place and is remembered",
              ThemeManager.IsLight && live.Color != darkText &&
              new SettingsService(services.Settings.FilePath).Current.LightTheme);

        var noRefresh = new List<string>();
        foreach (var id in window.SidebarIds)
        {
            window.Navigate(id);
            await Task.Delay(id is "system-tweaks" or "driver-manager" or "bloatware-remover" ? 5000 : 1800);
            render(window, Path.Combine(output, "light-" + id + ".png"));
            if (Shortcuts.Find(window.CurrentView, ButtonRole.Refresh) == null) noRefresh.Add(id);
        }
        log.Add("Pages with no enabled F5 button when checked: " + string.Join(", ", noRefresh));
        await window.ToggleThemeAsync();
        check("the theme switches back to dark", !ThemeManager.IsLight && live.Color == darkText);
        log.Add($"Render tier: {Motion.RenderTier} (2 = hardware accelerated); Windows animations on: {Motion.Enabled}");

        // First-run wizard.
        var firstPath = Path.Combine(work, "firstrun.json");
        var firstSettings = new SettingsService(firstPath);
        var cancelled = Offscreen(new FirstRunWindow(firstSettings, services.Modules, false));
        await Task.Delay(500);
        render(cancelled, Path.Combine(output, "firstrun-admin.png"));
        cancelled.ShowStep(1);
        cancelled.SetEnabled("disk-cleaner", false);
        await Task.Delay(500);
        render(cancelled, Path.Combine(output, "firstrun-modules.png"));
        cancelled.Close();
        check("closing the wizard changes nothing",
              !cancelled.Completed && !File.Exists(firstPath) && !firstSettings.Current.FirstRunCompleted);

        var wizard = Offscreen(new FirstRunWindow(firstSettings, services.Modules, false));
        check("the wizard offers every optional module",
              wizard.ModuleCount == services.Modules.All.Count(m => m.CanHide));
        wizard.SetEnabled("disk-cleaner", false);
        var ok = await wizard.SaveChoicesAsync();
        wizard.Close();
        var reread = new SettingsService(firstPath).Current;
        check("finishing the wizard saves the module choices",
              ok && wizard.Completed && reread.FirstRunCompleted &&
              reread.HiddenModules.SetEquals(new[] { "disk-cleaner" }));

        var splash = Offscreen(new SplashWindow { Status = "Loading modules..." });
        await Task.Delay(500);
        render(splash, Path.Combine(output, "splash.png"));
        splash.Close();
    }

    private static T Offscreen<T>(T window) where T : Window
    {
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -4000;
        window.Top = 0;
        window.Show();
        return window;
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        static double Light(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double x = Light(a), y = Light(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
