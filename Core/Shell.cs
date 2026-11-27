namespace PCOptimizer.Core;

/// <summary>What pages can ask the main window to do.</summary>
public interface IShell
{
    void Navigate(string moduleId, string? section);
    /// <summary>Runs a quick action such as "clean". Returns what happened.</summary>
    Task<string> RunQuickActionAsync(string actionId);
    string? FindQuickActionLabel(string actionId);
}

/// <summary>
/// An action offered in the tray menu. <paramref name="Run"/> receives a yes/no question function (default No)
/// that it must use before deleting or changing anything the user has not already chosen.
/// </summary>
public sealed record QuickAction(string Id, int Order, Func<string> Label, Func<Func<string, bool>, Task<string>> Run);

public interface IQuickActionProvider : IModule
{
    IEnumerable<QuickAction> QuickActions(AppContextServices services);
}

/// <summary>Implemented by page views that have sub-pages or items search can jump to.</summary>
public interface ISectionHost
{
    void ShowSection(string section);
}
