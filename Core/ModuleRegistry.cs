using System.Reflection;

namespace PCOptimizer.Core;

/// <summary>
/// Discovers modules compiled into this app. It deliberately does not load DLLs from a folder,
/// because that would run any code someone dropped there.
/// </summary>
public sealed class ModuleRegistry
{
    public IReadOnlyList<IModule> All { get; }

    public ModuleRegistry(Assembly assembly)
    {
        var modules = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IModule).IsAssignableFrom(t))
            .Select(t => (IModule)Activator.CreateInstance(t)!)
            .OrderBy(m => m.Order).ThenBy(m => m.Title)
            .ToList();

        var duplicate = modules.GroupBy(m => m.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"Two modules use the id '{duplicate.Key}'.");
        All = modules;
    }

    public IEnumerable<IModule> Visible(AppSettings settings) =>
        All.Where(m => !m.CanHide || !settings.HiddenModules.Contains(m.Id));
}
