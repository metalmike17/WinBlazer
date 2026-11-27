using Microsoft.Win32;

namespace PCOptimizer.Services;

/// <summary>One folder to look in. Only files matching Pattern are ever touched.</summary>
public sealed record CleanTarget(string Root, string Pattern = "*", bool Recurse = true);

/// <summary>WindowsTool: files are measured here but removed by a supported Windows command.</summary>
public enum CategoryKind { Files, RecycleBin, ReportOnly, WindowsTool }

public sealed record CleanCategory(string Id, string Name, string Note, IReadOnlyList<CleanTarget> Targets)
{
    public CategoryKind Kind { get; init; } = CategoryKind.Files;
    public bool RequiresAdmin { get; init; }
    /// <summary>Only files older than the user's minimum age are included.</summary>
    public bool UsesMinimumAge { get; init; }
    /// <summary>The category is not cleaned while any of these processes is running.</summary>
    public IReadOnlyList<string> BlockingProcesses { get; init; } = Array.Empty<string>();
    /// <summary>Returns a reason to refuse cleaning right now, or null.</summary>
    public Func<string?>? BlockReason { get; init; }
    /// <summary>For WindowsTool: performs the cleanup and returns an error message, or null.</summary>
    public Func<string?>? Tool { get; init; }
}

public static class CleanupCatalog
{
    private static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    public static IReadOnlyList<CleanCategory> Create()
    {
        var local = Folder(Environment.SpecialFolder.LocalApplicationData);
        var profile = Folder(Environment.SpecialFolder.UserProfile);
        var programData = Folder(Environment.SpecialFolder.CommonApplicationData);
        var windows = Folder(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windows)!;
        string L(params string[] parts) => Path.Combine(parts.Prepend(local).ToArray());
        string W(params string[] parts) => Path.Combine(parts.Prepend(windows).ToArray());

        var tempRoots = new List<string> { L("Temp") };
        // %TEMP% can be pointed anywhere, so accept it only if the folder is clearly a temp folder.
        var envTemp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (IsPlausibleTemp(envTemp) && !tempRoots.Contains(envTemp, StringComparer.OrdinalIgnoreCase))
            tempRoots.Add(envTemp);

        return new List<CleanCategory>
        {
            new("user-temp", "User temp files", "Old files in %TEMP%.",
                tempRoots.Select(r => new CleanTarget(r)).ToList()) { UsesMinimumAge = true },
            new("windows-temp", "Windows temp files", @"Old files in Windows\Temp.",
                new[] { new CleanTarget(W("Temp")) }) { UsesMinimumAge = true, RequiresAdmin = true },
            new("update-cache", "Windows Update cache", "Downloaded update files. Windows may download some again.",
                new[] { new CleanTarget(W("SoftwareDistribution", "Download")) })
                { RequiresAdmin = true, BlockingProcesses = new[] { "TiWorker", "TrustedInstaller" }, BlockReason = PendingUpdate },
            Chromium("chrome", "Chrome cache", L("Google", "Chrome", "User Data"), "chrome"),
            Chromium("edge", "Edge cache", L("Microsoft", "Edge", "User Data"), "msedge"),
            new("firefox", "Firefox cache", "Web cache only. Close Firefox first.",
                Children(L("Mozilla", "Firefox", "Profiles"), _ => true)
                    .Select(p => new CleanTarget(Path.Combine(p, "cache2"))).ToList())
                { BlockingProcesses = new[] { "firefox" } },
            Chromium("brave", "Brave cache", L("BraveSoftware", "Brave-Browser", "User Data"), "brave"),
            new("recycle-bin", "Recycle Bin", "Your deleted files on all drives. Check it before emptying.",
                Array.Empty<CleanTarget>()) { Kind = CategoryKind.RecycleBin },
            new("thumbnails", "Thumbnail cache", "Windows rebuilds thumbnails. Files in use are skipped.",
                new[] { new CleanTarget(L("Microsoft", "Windows", "Explorer"), "thumbcache_*.db", false) }),
            new("shaders", "Shader caches", "DirectX, NVIDIA, AMD, Intel. Games may stutter while rebuilding.",
                new[]
                {
                    L("D3DSCache"), L("NVIDIA", "DXCache"), L("NVIDIA", "GLCache"),
                    Path.Combine(programData, "NVIDIA Corporation", "NV_Cache"),
                    L("AMD", "DxCache"), L("AMD", "DxcCache"), L("AMD", "GLCache"), L("AMD", "VkCache"),
                    L("Intel", "ShaderCache"), Path.Combine(profile, "AppData", "LocalLow", "Intel", "ShaderCache")
                }.Select(r => new CleanTarget(r)).ToList()),
            new("prefetch", "Prefetch files", "Not usually worth cleaning: apps may start slower for a while.",
                new[] { new CleanTarget(W("Prefetch"), "*.pf", false) }) { RequiresAdmin = true },
            new("crash-dumps", "Crash dumps", "Keep these if you are troubleshooting crashes.",
                new[]
                {
                    new CleanTarget(L("CrashDumps"), "*.dmp"), new CleanTarget(W("Minidump"), "*.dmp"),
                    new CleanTarget(W("LiveKernelReports"), "*.dmp"), new CleanTarget(windows, "MEMORY.DMP", false)
                }),
            new("windows-old", "Windows.old", "Previous Windows install. Remove it with Windows Storage settings.",
                new[] { new CleanTarget(Path.Combine(systemDrive, "Windows.old")) })
                { Kind = CategoryKind.ReportOnly }
        };
    }

    /// <summary>Extra categories used by Deep Clean. Log cleanup is limited to the folders named here.</summary>
    public static IReadOnlyList<CleanCategory> DeepExtras()
    {
        var local = Folder(Environment.SpecialFolder.LocalApplicationData);
        var programData = Folder(Environment.SpecialFolder.CommonApplicationData);
        var windows = Folder(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windows)!;
        CleanTarget[] Reports(string root) => new[] { "ReportArchive", "ReportQueue" }
            .Select(n => new CleanTarget(Path.Combine(root, "Microsoft", "Windows", "WER", n))).ToArray();

        return new List<CleanCategory>
        {
            new("delivery-optimization", "Delivery Optimization files", "Cached update downloads shared between PCs.",
                new[] { new CleanTarget(Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local",
                    "Microsoft", "Windows", "DeliveryOptimization", "Cache")) })
                { Kind = CategoryKind.WindowsTool, RequiresAdmin = true, Tool = ClearDeliveryOptimization },
            new("iis-logs", "IIS logs", @"Old .log files in inetpub\logs\LogFiles.",
                new[] { new CleanTarget(Path.Combine(systemDrive, "inetpub", "logs", "LogFiles"), "*.log") })
                { RequiresAdmin = true, UsesMinimumAge = true },
            new("user-error-reports", "Your error reports", "Old Windows Error Reporting files for your account.",
                Reports(local)) { UsesMinimumAge = true },
            new("system-error-reports", "System error reports", "Old Windows Error Reporting files for the PC.",
                Reports(programData)) { RequiresAdmin = true, UsesMinimumAge = true },
            new("windows-logs", "Windows log files", @"Old .log files in Windows\Logs only.",
                new[] { new CleanTarget(Path.Combine(windows, "Logs"), "*.log") })
                { RequiresAdmin = true, UsesMinimumAge = true }
        };
    }

    private static string? ClearDeliveryOptimization()
    {
        var start = new System.Diagnostics.ProcessStartInfo(PCOptimizer.Core.SystemPowerShell.Program)
            { CreateNoWindow = true, UseShellExecute = false };
        foreach (var argument in PCOptimizer.Core.SystemPowerShell.Arguments(
                     "try { Delete-DeliveryOptimizationCache -Force -ErrorAction Stop } catch { exit 1 }"))
            start.ArgumentList.Add(argument);
        PCOptimizer.Core.SystemPowerShell.Isolate(start);
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            if (!process.WaitForExit(120_000)) { process.Kill(); return "Windows did not finish in time."; }
            return process.ExitCode == 0 ? null : "Windows could not clear the Delivery Optimization cache.";
        }
        catch (System.ComponentModel.Win32Exception ex) { return ex.Message; }
    }

    public static bool IsPlausibleTemp(string path)
    {
        var name = Path.GetFileName(path);
        var parent = Path.GetDirectoryName(path);
        return parent != null && Path.GetDirectoryName(parent) != null &&   // not a root or top-level folder
               (name.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Tmp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Only named cache folders inside each profile. Never cookies, passwords or history.</summary>
    private static CleanCategory Chromium(string id, string name, string userData, string process)
    {
        var targets = Children(userData, n => n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal))
            .SelectMany(p => new[] { "Cache", "Code Cache", "GPUCache" }.Select(c => new CleanTarget(Path.Combine(p, c))))
            .ToList();
        return new CleanCategory(id, name, "Cache files only. Close the browser first.", targets)
            { BlockingProcesses = new[] { process } };
    }

    private static IEnumerable<string> Children(string parent, Func<string, bool> accept)
    {
        try
        {
            return Directory.Exists(parent)
                ? Directory.GetDirectories(parent).Where(d => accept(Path.GetFileName(d))).ToList()
                : Enumerable.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Enumerable.Empty<string>(); }
    }

    private static string? PendingUpdate()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return key != null ? "An update is waiting for a restart." : null;
    }
}
