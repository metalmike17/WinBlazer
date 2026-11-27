using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Clip = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace PCOptimizer.Services;

public enum TraceKind { RecentFiles, JumpLists, MruLists, Clipboard, AppUsage }

public sealed record TraceInfo(TraceKind Kind, string Name, string Note);

/// <summary>Current user only. The locations are fixed, and tests can substitute throwaway ones.</summary>
public sealed class WindowsTraces
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\";
    /// <summary>This jump list file stores Quick Access pinned folders, so it is kept.</summary>
    public const string QuickAccessFile = "f01b4d95cf55d32a.automaticDestinations-ms";

    private readonly string _recent;
    private readonly string _registryPrefix;
    private readonly bool _isReal;

    public WindowsTraces() : this(Environment.GetFolderPath(Environment.SpecialFolder.Recent), "") => _isReal = true;

    /// <summary>For tests: registry paths are placed under registryPrefix.</summary>
    public WindowsTraces(string recentFolder, string registryPrefix)
    {
        _recent = recentFolder;
        _registryPrefix = registryPrefix;
    }

    public static IReadOnlyList<TraceInfo> All { get; } = new[]
    {
        new TraceInfo(TraceKind.RecentFiles, "Recently opened files",
            "Shortcuts in your Recent folder. The files themselves are not touched."),
        new TraceInfo(TraceKind.JumpLists, "Recent shortcuts (jump lists)",
            "Taskbar and Start recent lists. App jump list pins are lost; Quick Access pins are kept."),
        new TraceInfo(TraceKind.MruLists, "Most-recently-used lists",
            "Run box, typed Explorer paths and searches, Open/Save dialog history, recent documents."),
        new TraceInfo(TraceKind.Clipboard, "Clipboard and clipboard history",
            "Empties the current clipboard and unpinned history. Pinned items stay."),
        new TraceInfo(TraceKind.AppUsage, "App usage tracking",
            "Explorer's app launch records (UserAssist, RecentApps). Can reset most-used app lists.")
    };

    private static readonly string[] MruKeys =
    {
        Explorer + "RunMRU", Explorer + "TypedPaths", Explorer + "RecentDocs", Explorer + "WordWheelQuery",
        Explorer + @"ComDlg32\OpenSavePidlMRU", Explorer + @"ComDlg32\LastVisitedPidlMRU",
        Explorer + @"ComDlg32\CIDSizeMRU"
    };

    private IEnumerable<CleanTarget> FileTargets(TraceKind kind)
    {
        if (kind == TraceKind.RecentFiles) return new[] { new CleanTarget(_recent, "*.lnk", false) };
        return new[]
        {
            new CleanTarget(Path.Combine(_recent, "AutomaticDestinations"), "*.automaticDestinations-ms", false),
            new CleanTarget(Path.Combine(_recent, "CustomDestinations"), "*.customDestinations-ms", false)
        };
    }

    private CategoryScan ScanFiles(TraceKind kind)
    {
        var category = new CleanCategory(kind.ToString(), kind.ToString(), "", FileTargets(kind).ToList());
        var scan = CleanupEngine.Scan(category, 1);
        scan.Files.RemoveAll(f => Path.GetFileName(f.Path).Equals(QuickAccessFile, StringComparison.OrdinalIgnoreCase));
        return scan;
    }

    /// <summary>Read-only. Returns null when there is nothing countable (the clipboard is never read).</summary>
    public int? Count(TraceKind kind)
    {
        switch (kind)
        {
            case TraceKind.RecentFiles:
            case TraceKind.JumpLists: return ScanFiles(kind).Files.Count;
            case TraceKind.MruLists: return MruKeys.Sum(k => CountValues(_registryPrefix + k));
            case TraceKind.AppUsage: return AppUsageKeys().Sum(CountValues);
            default: return null;
        }
    }

    public EraseResult Erase(TraceKind kind)
    {
        var name = All.First(t => t.Kind == kind).Name;
        try
        {
            switch (kind)
            {
                case TraceKind.RecentFiles:
                case TraceKind.JumpLists:
                {
                    var scan = ScanFiles(kind);
                    var result = CleanupEngine.Clean(scan, 1, false, _ => false);
                    var ok = result.Skipped == 0 && !scan.Incomplete;
                    return new(name, ok, $"Removed {result.Deleted:N0} item(s), skipped {result.Skipped:N0}." +
                                         (scan.Incomplete ? " Some folders were not read." : ""));
                }
                case TraceKind.MruLists:
                {
                    var count = MruKeys.Sum(k => EmptyKey(_registryPrefix + k));
                    return new(name, true, $"Removed {count:N0} entries.");
                }
                case TraceKind.AppUsage:
                {
                    var count = AppUsageKeys().ToList().Sum(EmptyKey);
                    return new(name, true, $"Removed {count:N0} entries.");
                }
                default:
                {
                    if (!_isReal) return new(name, false, "Clipboard is not available in test mode.");
                    // ClearHistory returns false when Windows refuses, for example if history is off.
                    var historyCleared = Clip.IsHistoryEnabled() && Clip.ClearHistory();
                    System.Windows.Clipboard.Clear();
                    if (historyCleared) return new(name, true, "Current clipboard and unpinned history cleared.");
                    return Clip.IsHistoryEnabled()
                        ? new(name, false, "Current clipboard cleared, but Windows did not clear the history.")
                        : new(name, true, "Current clipboard cleared. Clipboard history is turned off.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or ExternalException)
        {
            return new(name, false, "Failed: " + ex.Message);
        }
    }

    private IEnumerable<string> AppUsageKeys()
    {
        var userAssist = _registryPrefix + Explorer + "UserAssist";
        using var key = Registry.CurrentUser.OpenSubKey(userAssist);
        if (key != null)
            foreach (var id in key.GetSubKeyNames())
                yield return $@"{userAssist}\{id}\Count";   // only the records, not the settings
        yield return _registryPrefix + @"Software\Microsoft\Windows\CurrentVersion\Search\RecentApps";
    }

    private static int CountValues(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key == null ? 0 : Count(key);

        static int Count(RegistryKey k) => k.ValueCount + k.GetSubKeyNames().Sum(n =>
        {
            using var child = k.OpenSubKey(n);
            return child == null ? 0 : Count(child);
        });
    }

    /// <summary>Removes a key's contents but keeps the key itself.</summary>
    private static int EmptyKey(string path)
    {
        var count = CountValues(path);
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        if (key == null) return 0;
        foreach (var child in key.GetSubKeyNames()) key.DeleteSubKeyTree(child, throwOnMissingSubKey: false);
        foreach (var value in key.GetValueNames()) key.DeleteValue(value, throwOnMissingValue: false);
        return count;
    }
}
