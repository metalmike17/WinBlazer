using System.Windows;

namespace PCOptimizer.Core;

/// <summary>
/// A feature page. To add one, create a class that implements this interface and has a
/// parameterless constructor. ModuleRegistry finds it automatically.
/// </summary>
public interface IModule
{
    string Id { get; }            // stable key used in the settings file
    string Title { get; }
    string Icon { get; }          // a Segoe MDL2 Assets glyph
    string Description { get; }
    int Order { get; }
    bool CanHide => true;
    FrameworkElement CreateView(AppContextServices services);
}

/// <summary>Optional: modules that support scheduled scans. A scan must never change anything.</summary>
public interface IScannableModule : IModule
{
    Task<string> ScanAsync(CleanupPreferences preferences, CancellationToken token);
}

/// <summary>
/// Optional: modules with work that continues while their page is closed. Started once when the app
/// starts (even if the module is hidden), and disposed when it exits.
/// </summary>
public interface IBackgroundModule : IModule
{
    IDisposable StartBackground(AppContextServices services);
}

public sealed record AppContextServices(SettingsService Settings, ModuleRegistry Modules)
{
    /// <summary>Set by the main window.</summary>
    public IShell? Shell { get; set; }
}
