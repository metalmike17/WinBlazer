using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services.MemoryTools;

public sealed record RunningApp(string Path, string Name, int Processes, long WorkingSet);

/// <summary>
/// Sets chosen apps to low (idle) priority with Efficiency mode, and undoes it. Apps are identified by
/// full program path. Windows components are never touched. Call from one thread.
/// </summary>
public sealed class AppLimiter
{
    private const uint IdlePriority = 0x40, NormalPriority = 0x20, RealtimePriority = 0x100;

    // Keyed by process id and start time, because Windows reuses ids.
    private readonly Dictionary<(int Id, long Started), (string Path, uint Original)> _limited = new();

    public int LimitedCount => _limited.Count;
    public int LimitedCountFor(string path) =>
        _limited.Values.Count(v => v.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "dwm", "csrss", "audiodg", "winlogon", "services", "lsass", "taskmgr", "MsMpEng" };

    public static bool IsProtected(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ||
               Protected.Contains(System.IO.Path.GetFileNameWithoutExtension(path)) ||
               path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(Process Process, string Path)> SessionProcesses()
    {
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        {
            string? path = null;
            try
            {
                if (process.SessionId == self.SessionId && process.Id != self.Id) path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            if (path == null) process.Dispose();
            else yield return (process, path);
        }
    }

    /// <summary>Apps that may be limited.</summary>
    public static List<RunningApp> ListApps()
    {
        var found = new List<(string Path, long Memory)>();
        foreach (var (process, path) in SessionProcesses())
            using (process)
                if (!IsProtected(path)) found.Add((path, process.WorkingSet64));
        return found.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RunningApp(g.Key, System.IO.Path.GetFileNameWithoutExtension(g.Key), g.Count(),
                                        g.Sum(x => x.Memory)))
            .OrderByDescending(a => a.WorkingSet).ToList();
    }

    /// <summary>
    /// Limits any new processes of the chosen apps, and restores apps no longer chosen.
    /// Returns how many processes could not be changed.
    /// </summary>
    public int Apply(IReadOnlyCollection<string> chosenPaths)
    {
        var chosen = new HashSet<string>(chosenPaths, StringComparer.OrdinalIgnoreCase);
        Restore(v => !chosen.Contains(v));
        if (chosen.Count == 0) return 0;
        var failed = 0;
        var alive = new HashSet<(int, long)>();
        foreach (var (process, path) in SessionProcesses())
            using (process)
            {
                if (!chosen.Contains(path) || IsProtected(path)) continue;
                try
                {
                    var key = (process.Id, process.StartTime.ToUniversalTime().Ticks);
                    alive.Add(key);
                    if (_limited.ContainsKey(key)) continue;
                    // A new child of a limited app inherits idle priority; normal is its real original.
                    var inherited = LimitedCountFor(path) > 0;
                    if (LimitProcess(process.Id, out var original))
                        _limited[key] = (path, inherited && original == IdlePriority ? NormalPriority : original);
                    else failed++;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { failed++; }
            }
        foreach (var gone in _limited.Keys.Where(k => !alive.Contains(k)).ToList()) _limited.Remove(gone);
        return failed;
    }

    public void RestoreAll() => Restore(_ => true);

    private void Restore(Func<string, bool> which)
    {
        foreach (var (key, value) in _limited.Where(p => which(p.Value.Path)).ToList())
        {
            try
            {
                using var process = Process.GetProcessById(key.Id);
                if (process.StartTime.ToUniversalTime().Ticks == key.Started) RestoreProcess(key.Id, value.Original);
            }
            catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException) { }
            _limited.Remove(key);
        }
    }

    // ---- One process ----

    private static IntPtr Open(int id) => OpenProcess(0x1000 | 0x0200, false, id);   // query limited + set info

    private static bool SetEfficiency(IntPtr handle, bool on)
    {
        // ControlMask 0 hands the decision back to Windows.
        var state = new PowerThrottling { Version = 1, ControlMask = on ? 1u : 0u, StateMask = on ? 1u : 0u };
        return SetProcessInformation(handle, 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PowerThrottling>());
    }

    public static bool LimitProcess(int id, out uint originalPriority)
    {
        originalPriority = 0;
        var handle = Open(id);
        if (handle == IntPtr.Zero) return false;
        try
        {
            originalPriority = GetPriorityClass(handle);
            if (originalPriority is 0 or RealtimePriority) return false;
            if (!SetPriorityClass(handle, IdlePriority)) return false;
            if (SetEfficiency(handle, true)) return true;
            SetPriorityClass(handle, originalPriority);   // do not leave a half-applied limit
            return false;
        }
        finally { CloseHandle(handle); }
    }

    public static bool RestoreProcess(int id, uint originalPriority)
    {
        var handle = Open(id);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var efficiency = SetEfficiency(handle, false);
            // If something else has since changed the priority, leave that choice alone.
            var priority = GetPriorityClass(handle) != IdlePriority || SetPriorityClass(handle, originalPriority);
            return efficiency && priority;
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottling { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint GetPriorityClass(IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool SetPriorityClass(IntPtr process, uint priority);
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, int size);
}
