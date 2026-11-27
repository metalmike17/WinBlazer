using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCOptimizer.Services.MemoryTools;

namespace PCOptimizer.Services.GameTools;

/// <summary>Everything Game Mode changes outside the registry, so tests can use a fake.</summary>
public interface IGameSystem
{
    Guid ActivePlan();
    /// <summary>An existing Ultimate Performance plan, or a new copy of Windows' hidden one.</summary>
    Guid UltimatePlan();
    bool IsUltimate(Guid plan);
    void SetPlan(Guid plan);
    GpuPowerMode ReadGpu();
    void WriteGpu(uint? value);
    bool IsRunning(string path);
    /// <summary>Asks politely. True only if every process of the app has exited.</summary>
    bool CloseApp(string path);
    void Launch(string path);
    ActionResult PurgeStandby();
    /// <summary>Arranges (or cancels) a restore at the next sign-in.</summary>
    void SetSignInRecovery(bool on);
}

public sealed class WindowsGameSystem : IGameSystem
{
    private static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private const string RunOnce = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceName = "PCOptimizerGameModeRestore";

    private static string PowerCfg(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(); throw new IOException("powercfg did not respond."); }
        if (process.ExitCode != 0) throw new IOException("powercfg failed: " + error.Result.Trim());
        return output.Result;
    }

    public static List<(Guid Id, string Name)> ParsePlans(string output) =>
        Regex.Matches(output, @"([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s+\((.*)\)")
            .Select(m => (Guid.Parse(m.Groups[1].Value), m.Groups[2].Value)).ToList();

    private static List<(Guid Id, string Name)> Plans() => ParsePlans(PowerCfg("/list"));

    public Guid ActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0)
            throw new IOException("The active power plan could not be read.");
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    public bool IsUltimate(Guid plan) => plan == UltimateTemplate ||
        Plans().Any(p => p.Id == plan && p.Name.Equals("Ultimate Performance", StringComparison.OrdinalIgnoreCase));

    public Guid UltimatePlan()
    {
        var existing = Plans().Where(p => p.Id == UltimateTemplate ||
            p.Name.Equals("Ultimate Performance", StringComparison.OrdinalIgnoreCase)).ToList();
        if (existing.Count > 0) return existing[0].Id;
        var created = ParsePlans(PowerCfg("/duplicatescheme", UltimateTemplate.ToString()));
        return created.Count > 0 ? created[0].Id
            : throw new IOException("Windows did not create the Ultimate Performance plan.");
    }

    public void SetPlan(Guid plan)
    {
        if (PowerSetActiveScheme(IntPtr.Zero, ref plan) != 0)
            throw new IOException("Windows did not switch the power plan.");
    }

    public GpuPowerMode ReadGpu() => NvidiaPowerMode.Read();
    public void WriteGpu(uint? value) => NvidiaPowerMode.Write(value);

    private static List<int> ProcessesAt(string path) => GameLibrary.RunningProcesses()
        .Where(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToList();

    public bool IsRunning(string path) => ProcessesAt(path).Count > 0;

    public bool CloseApp(string path)
    {
        if (AppLimiter.IsProtected(path)) return false;
        foreach (var id in ProcessesAt(path))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.CloseMainWindow();   // the same as clicking X; the app may ask to save or stay in the tray
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
        for (var i = 0; i < 20; i++)
        {
            if (!IsRunning(path)) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    public void Launch(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || !File.Exists(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The program was not found.");
        // When elevated, Explorer starts the app so it does not inherit administrator rights.
        var start = CleanupEngine.IsAdministrator()
            ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                                "explorer.exe"), $"\"{path}\"")
            : new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)! };
        start.UseShellExecute = true;
        try { Process.Start(start)?.Dispose(); }
        catch (Win32Exception ex) { throw new IOException(ex.Message); }
    }

    public ActionResult PurgeStandby() =>
        MemoryActions.Run(MemoryAction.PurgeStandbyList, CleanupEngine.IsAdministrator());

    public void SetSignInRecovery(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunOnce);
        if (on) key.SetValue(RunOnceName, $"\"{Environment.ProcessPath}\" --gamemode-restore");
        else key.DeleteValue(RunOnceName, throwOnMissingValue: false);
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
