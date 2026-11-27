using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCOptimizer.Services.DriverTools;

namespace PCOptimizer.Services.Bloatware;

public sealed record AppPackage(string Name, string FullName, string FamilyName, string InstallLocation,
    string SignatureKind, bool NonRemovable, bool IsFramework);

public sealed record RemovedApp(string Name, string FamilyName, DateTime RemovedAt, long Bytes);

public sealed class BloatEntry
{
    public required CatalogApp App { get; init; }
    /// <summary>Null when the app is not installed for this user.</summary>
    public AppPackage? Package { get; init; }
    public bool Installed => Package != null;
    public RemovedApp? Removed { get; init; }
    /// <summary>App files plus this user's app data. Null until measured or when unreadable.</summary>
    public long? AppBytes { get; set; }
    public long DataBytes { get; set; }
}

public sealed record AppAction(bool Ok, string Message);

public sealed class AppSnapshot
{
    public required List<BloatEntry> Entries { get; init; }
    public bool StoreInstalled { get; init; }
}

/// <summary>Lists, removes and restores catalog apps for the current user only.</summary>
public sealed class AppxService
{
    private const string ListScript = @"$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
ConvertTo-Json -Compress -InputObject @(Get-AppxPackage -PackageTypeFilter Main | ForEach-Object { [pscustomobject]@{
  Name=$_.Name; FullName=$_.PackageFullName; FamilyName=$_.PackageFamilyName; InstallLocation=$_.InstallLocation
  SignatureKind=[string]$_.SignatureKind; NonRemovable=[bool]$_.NonRemovable; IsFramework=[bool]$_.IsFramework } })";

    private readonly Func<string, string[], TimeSpan, ToolOutput> _run;
    private readonly Func<AppPackage, bool> _isRunning;
    private readonly string _recordPath;
    private readonly string _dataRoot;

    public AppxService(string recordPath, Func<string, string[], TimeSpan, ToolOutput>? run = null,
                       Func<AppPackage, bool>? isRunning = null, string? dataRoot = null)
    {
        _recordPath = recordPath;
        _run = run ?? DriverService.Run;
        _isRunning = isRunning ?? IsRunning;
        _dataRoot = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
    }

    /// <summary>The catalog decides what is allowed; Windows' own protection flags can only veto.</summary>
    public static bool IsRemovable(AppPackage p) => AppCatalog.Find(p.Name) != null && !p.NonRemovable &&
        !p.IsFramework && !p.SignatureKind.Equals("System", StringComparison.OrdinalIgnoreCase) &&
        SafeId(p.FullName) && SafeId(p.FamilyName);

    /// <summary>Only these characters are ever placed into a script.</summary>
    private static bool SafeId(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9._~-]+\z");

    private ToolOutput PowerShell(string script) => _run(PCOptimizer.Core.SystemPowerShell.Program,
        PCOptimizer.Core.SystemPowerShell.Arguments("$ProgressPreference='SilentlyContinue'\n" + script),
        TimeSpan.FromMinutes(5));

    private List<AppPackage> ReadPackages()
    {
        var result = PowerShell(ListScript);
        if (result.ExitCode != 0) throw new IOException("Apps could not be listed: " + result.Output.Trim());
        return JsonSerializer.Deserialize<List<AppPackage>>(result.Output) ?? new();
    }

    public List<RemovedApp> Records()
    {
        if (!File.Exists(_recordPath)) return new();
        // A damaged file throws, so that it is never silently overwritten.
        try { return JsonSerializer.Deserialize<List<RemovedApp>>(File.ReadAllText(_recordPath)) ?? new(); }
        catch (JsonException ex) { throw new IOException($"The removal record is damaged: {_recordPath}. {ex.Message}"); }
    }

    private void Save(List<RemovedApp> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
        File.WriteAllText(_recordPath + ".tmp",
            JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_recordPath + ".tmp", _recordPath, true);
    }

    public AppSnapshot Read()
    {
        var packages = ReadPackages();
        var records = Records();
        var entries = new List<BloatEntry>();
        foreach (var app in AppCatalog.All)
        {
            var installed = packages.Where(p => p.Name.Equals(app.PackageName, StringComparison.OrdinalIgnoreCase)).ToList();
            var usable = installed.FirstOrDefault(IsRemovable);
            if (installed.Count > 0 && usable == null) continue;   // protected on this PC: never listed
            entries.Add(new BloatEntry
            {
                App = app, Package = usable,
                Removed = usable != null ? null
                    : records.LastOrDefault(r => r.Name.Equals(app.PackageName, StringComparison.OrdinalIgnoreCase))
            });
        }
        return new AppSnapshot
        {
            Entries = entries,
            StoreInstalled = packages.Any(p => p.Name.Equals("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase))
        };
    }

    public void Measure(BloatEntry entry)
    {
        if (entry.Package is not { } package) return;
        entry.AppBytes = FolderSize(package.InstallLocation);
        entry.DataBytes = FolderSize(Path.Combine(_dataRoot, package.FamilyName)) ?? 0;
    }

    /// <summary>Total file size without following links. Null if the folder cannot be read.</summary>
    public static long? FolderSize(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        var first = true;
        while (pending.Count > 0)
        {
            try
            {
                foreach (var item in pending.Pop().EnumerateFileSystemInfos())
                {
                    if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (item is DirectoryInfo directory) pending.Push(directory);
                    else total += ((FileInfo)item).Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (first) return null;
            }
            first = false;
        }
        return total;
    }

    private static bool IsRunning(AppPackage package)
    {
        if (package.InstallLocation.Length == 0) return false;
        var prefix = package.InstallLocation.TrimEnd('\\') + "\\";
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        return false;
    }

    /// <summary>
    /// Uninstalls for the current user. The record needed to restore it is saved first; if that fails,
    /// nothing is removed. Success is confirmed by listing the apps again.
    /// </summary>
    public AppAction Remove(BloatEntry entry)
    {
        var title = entry.App.Title;
        // Check again against what is installed now.
        var now = ReadPackages().FirstOrDefault(p => p.FullName == entry.Package?.FullName);
        if (now == null) return new(false, $"{title} changed or is already gone. Reload the list.");
        if (!IsRemovable(now)) return new(false, $"{title} is protected and was not removed.");
        if (_isRunning(now)) return new(false, $"{title} is running. Close it, then try again.");

        var records = Records();
        var record = new RemovedApp(now.Name, now.FamilyName, DateTime.Now, (entry.AppBytes ?? 0) + entry.DataBytes);
        records.RemoveAll(r => r.FamilyName.Equals(now.FamilyName, StringComparison.OrdinalIgnoreCase));
        records.Add(record);
        Save(records);

        var result = PowerShell($"$ErrorActionPreference='Stop'\nRemove-AppxPackage -Package '{now.FullName}'");
        if (ReadPackages().Any(p => p.FamilyName.Equals(now.FamilyName, StringComparison.OrdinalIgnoreCase)))
        {
            records.Remove(record);
            Save(records);
            return new(false, $"{title} was not removed. {FirstLine(result.Output)}");
        }
        return new(true, $"{title} was removed.");
    }

    /// <summary>
    /// Restores an app whose files are still on this PC, without downloading anything. Returns false when
    /// it has to come from the Store.
    /// </summary>
    public AppAction Reregister(string familyName, string title)
    {
        if (!SafeId(familyName)) return new(false, "The saved app name is not valid.");
        var result = PowerShell("$ErrorActionPreference='Stop'\n" +
                                $"Add-AppxPackage -RegisterByFamilyName -MainPackage '{familyName}'");
        if (!ReadPackages().Any(p => p.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"{title} could not be restored from files on this PC. {FirstLine(result.Output)}");
        var records = Records();
        if (records.RemoveAll(r => r.FamilyName.Equals(familyName, StringComparison.OrdinalIgnoreCase)) > 0)
            Save(records);
        return new(true, $"{title} was reinstalled from files already on this PC.");
    }

    private static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#<")) ?? "";

    /// <summary>
    /// The app's Store page. With the Store installed it opens there (exactly by family name when known);
    /// otherwise it is a search on Microsoft's web store.
    /// </summary>
    public static string StoreLink(CatalogApp app, string? familyName, bool storeInstalled)
    {
        var query = Uri.EscapeDataString(app.Title);
        if (!storeInstalled) return "https://apps.microsoft.com/search?query=" + query;
        return familyName != null && SafeId(familyName)
            ? "ms-windows-store://pdp/?PFN=" + familyName
            : "ms-windows-store://search/?query=" + query;
    }
}
