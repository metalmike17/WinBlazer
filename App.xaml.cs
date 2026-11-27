using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using PCOptimizer.Core;
using PCOptimizer.Themes;

namespace PCOptimizer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // First, before any file is read or written. Every administrator mode needs it, windowless ones too.
        if (Services.CleanupEngine.IsAdministrator() && !RedirectionGuard.TryEnable())
        {
            MessageBox.Show("Windows could not turn on its protection against redirected folders, so WinBlazer " +
                            "will not run as administrator. Update Windows, or open WinBlazer normally.",
                            "WinBlazer", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        // The self-test is left out, so that its failures are reported as before.
        if (e.Args.Length == 0 || !e.Args[0].StartsWith("--selftest", StringComparison.Ordinal)) LogCrashes();
#if !NO_SELFTEST
        if (e.Args.Length == 1 && e.Args[0] == "--selftest-sleep")
        {
            await Task.Delay(30_000);   // an idle helper process for the self-test
            Shutdown(0);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--selftest")
        {
            Shutdown(await SelfTest.RunAsync(e.Args[1]));
            return;
        }
#endif

        if (e.Args.Length > 0 && e.Args[0].StartsWith("--gamemode-", StringComparison.Ordinal))
        {
            Shutdown(await Task.Run(() => Services.GameTools.GameModeRecovery.Run(e.Args)));   // no window
            return;
        }

        if (e.Args.Length > 0 && e.Args[0] == "--auto-clean")
        {
            var settings = new SettingsService();
            Services.ActivityLog.Configure(Path.GetDirectoryName(settings.FilePath)!);
            Services.Activity? finished = null;
            Services.ActivityLog.Added += a => finished = a;
            var result = RunAutoClean(e.Args);   // no window
            if (finished != null && settings.Current.NotifyAfterCleanup)
            {
                using var tray = new TrayIcon(() => { }, () => { });
                tray.Notify("Automatic cleanup finished",
                    $"Freed {Services.CleanupEngine.Size(finished.BytesFreed ?? 0)}. See History for details.");
                await Task.Delay(7000);   // the notification belongs to the tray icon
            }
            Shutdown(result);
            return;
        }

        var restarted = e.Args.Length == 1 && e.Args[0] == SingleInstance.RestartArgument;
        _instance = SingleInstance.TryAcquire(TimeSpan.FromSeconds(restarted ? 30 : 0));
        if (_instance == null)
        {
            // After a restart the old copy is still busy, so say so instead of only bringing it forward.
            if (restarted || !SingleInstance.AskOpenCopyToShow())
                MessageBox.Show("WinBlazer is already open. Check the taskbar and the notification area. " +
                                "Close that copy before opening another.", "WinBlazer", MessageBoxButton.OK,
                                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }
        _instance.Listen(() => Dispatcher.BeginInvoke(ShowOpenWindow));

        await StartWindowsAsync();
    }

    private SingleInstance? _instance;
    private bool _crashShown;

    private void ShowOpenWindow()
    {
        var window = MainWindow is MainWindow { IsLoaded: true } main
            ? main
            : Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible);   // loading screen or setup
        if (window == null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Records unexpected errors. The app still closes as before; it does not carry on in a bad state.</summary>
    private void LogCrashes()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var saved = CrashLog.Write("Unexpected error in the window", args.Exception);
            if (_crashShown) return;   // the message box can let further errors through
            _crashShown = true;
            MessageBox.Show("WinBlazer hit an unexpected error and has to close.\n\n" + args.Exception.Message +
                            (saved ? "\n\nDetails were saved to:\n" + CrashLog.DefaultPath : ""),
                            "WinBlazer", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("Unhandled error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            CrashLog.Write("Unobserved background error (the app kept running)", args.Exception);
    }

    /// <summary>Loading screen, first-run setup when needed, then the main window.</summary>
    private async Task StartWindowsAsync()
    {
        // The loading screen and wizard must not end the app when they close.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ThemeManager.Apply(false);
        var splash = new SplashWindow();
        splash.Show();
        try
        {
            var shown = Stopwatch.StartNew();
            splash.Status = "Loading your settings...";
            var settings = await Task.Run(() => new SettingsService());
            ThemeManager.Apply(settings.Current.LightTheme);
            splash.Status = "Loading modules...";
            await Dispatcher.Yield(DispatcherPriority.Background);
            var services = new AppContextServices(settings, new ModuleRegistry(typeof(App).Assembly));
            Services.ActivityLog.Configure(Path.GetDirectoryName(settings.FilePath)!);
            // Long enough to be readable rather than a flash.
            var remaining = TimeSpan.FromMilliseconds(900) - shown.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);

            if (!settings.Current.FirstRunCompleted)
            {
                splash.Hide();
                var wizard = new FirstRunWindow(settings, services.Modules, Services.CleanupEngine.IsAdministrator());
                wizard.ShowDialog();
                if (wizard.Relaunched) { Shutdown(0); return; }
                splash.Show();
            }

            splash.Status = "Opening...";
            await Dispatcher.Yield(DispatcherPriority.Background);
            MainWindow = new MainWindow(services, enableBackgroundFeatures: true);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow.Show();
            splash.Close();
        }
        catch (Exception ex)
        {
            splash.Close();
            CrashLog.Write("Startup failed", ex);
            MessageBox.Show("WinBlazer could not start: " + ex.Message, "WinBlazer", MessageBoxButton.OK,
                            MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.ActivityLog.Flush();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static int RunAutoClean(string[] args)
    {
        if (!Services.AutoCleanArguments.TryParse(args, out var mode, out var recycle)) return 2;
        var settings = new SettingsService();
        var folder = Path.GetDirectoryName(settings.FilePath)!;
        string entry;
        var code = 0;
        try { entry = Services.CleanRunner.RunUnattended(mode, recycle, settings.Current.Cleanup.MinimumAgeDays); }
        catch (Exception ex)
        {
            entry = $"{DateTime.Now:yyyy-MM-dd HH:mm}  Auto clean failed: {ex.Message}\r\n";
            code = 1;
            Services.ActivityLog.Record(Services.ActivityLog.Cleanup, "Automatic clean failed", false, null, ex.Message);
        }
        try
        {
            Directory.CreateDirectory(folder);
            var log = Path.Combine(folder, "auto-clean.log");
            if (File.Exists(log) && new FileInfo(log).Length > 1_000_000) File.Delete(log);
            File.AppendAllText(log, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return code;
    }
}
