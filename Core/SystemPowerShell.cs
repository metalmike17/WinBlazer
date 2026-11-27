using System.Diagnostics;
using System.Text;

namespace PCOptimizer.Core;

/// <summary>
/// Runs Windows PowerShell so that it loads modules only from the Windows folder. By default it looks in the
/// user's Documents first, where any program could place a stand-in that would run with this app's rights.
/// </summary>
public static class SystemPowerShell
{
    public static string Program => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// Set inside the script, because PowerShell rebuilds the search path while starting.
    /// This must stay the first line, before any command can load a module.
    /// </summary>
    public const string FirstLine = "$env:PSModulePath = \"$PSHOME\\Modules\"\n";

    public static string[] Arguments(string script) => new[]
    {
        "-NoProfile", "-NonInteractive", "-EncodedCommand",
        Convert.ToBase64String(Encoding.Unicode.GetBytes(FirstLine + script))
    };

    // Environment variables that can make a .NET program load extra code as it starts.
    private static readonly string[] CodeLoading = { "COR_", "CORECLR_", "COMPlus_", "DOTNET_", "APPDOMAIN_MANAGER_" };

    /// <summary>Cleans what the new process inherits. Needs UseShellExecute to be false.</summary>
    public static void Isolate(ProcessStartInfo start)
    {
        foreach (var name in start.Environment.Keys
                     .Where(k => CodeLoading.Any(p => k.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList())
            start.Environment.Remove(name);
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(Program)!, "Modules");
    }
}
