using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PCOptimizer.Services;

public sealed record FoundFile(string Path, string Root, long Size);

public sealed class CategoryScan
{
    public required CleanCategory Category { get; init; }
    public List<FoundFile> Files { get; } = new();
    public long Bytes { get; set; }
    public long Items { get; set; }
    /// <summary>Some of it could not be read, so the real size may be larger.</summary>
    public bool Incomplete { get; set; }
    public string? Error { get; set; }
    public DateTime? CutoffUtc { get; set; }
}

public sealed record CleanResult(CleanCategory Category, long BytesBefore, long BytesAfter, long BytesDeleted,
                                 long Deleted, long Skipped, string? Message, bool AfterIncomplete);

public static class CleanupEngine
{
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N0} KB",
        _ => $"{bytes} bytes"
    };

    /// <summary>Read-only.</summary>
    public static CategoryScan Scan(CleanCategory category, int minimumAgeDays, CancellationToken token = default)
    {
        var scan = new CategoryScan
        {
            Category = category,
            CutoffUtc = category.UsesMinimumAge ? DateTime.UtcNow.AddDays(-Math.Max(1, minimumAgeDays)) : null
        };
        if (category.Kind == CategoryKind.RecycleBin)
        {
            if (TryQueryRecycleBin(out var bytes, out var items)) (scan.Bytes, scan.Items) = (bytes, items);
            else scan.Error = "Windows did not report the Recycle Bin size.";
            return scan;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in category.Targets)
        {
            var pending = new Stack<DirectoryInfo>();
            try
            {
                var root = new DirectoryInfo(target.Root);
                // GetAttributes distinguishes "missing" from "access denied". Exists does not.
                var attributes = File.GetAttributes(root.FullName);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) { scan.Incomplete = true; continue; }
                pending.Push(root);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { scan.Incomplete = true; continue; }

            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
                    {
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;   // never follow links
                        if (entry is DirectoryInfo dir)
                        {
                            if (target.Recurse) pending.Push(dir);
                            continue;
                        }
                        var file = (FileInfo)entry;
                        if (!FileSystemName.MatchesSimpleExpression(target.Pattern, file.Name)) continue;
                        if (scan.CutoffUtc is { } cutoff && !IsOld(file, cutoff)) continue;
                        if (!seen.Add(file.FullName)) continue;
                        scan.Bytes += file.Length;
                        scan.Items++;
                        if (category.Kind == CategoryKind.Files)
                            scan.Files.Add(new FoundFile(file.FullName, target.Root, file.Length));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { scan.Incomplete = true; }
            }
        }
        return scan;
    }

    private static bool IsOld(FileInfo file, DateTime cutoff) =>
        file.LastWriteTimeUtc < cutoff && file.CreationTimeUtc < cutoff;

    /// <summary>Why this category cannot be cleaned right now, or null if it can.</summary>
    public static string? WhyBlocked(CleanCategory category, bool isAdmin, Func<string, bool>? isRunning = null)
    {
        isRunning ??= name =>
        {
            var found = Process.GetProcessesByName(name);
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        };
        if (category.Kind == CategoryKind.ReportOnly) return "Report only.";
        if (category.RequiresAdmin && !isAdmin) return "Needs administrator rights.";
        var running = category.BlockingProcesses.FirstOrDefault(isRunning);
        if (running != null) return $"{running} is running. Close it or wait, then try again.";
        return category.BlockReason?.Invoke();
    }

    /// <summary>Permanently deletes what the scan listed, and nothing else.</summary>
    public static CleanResult Clean(CategoryScan scan, int minimumAgeDays, bool isAdmin,
                                    Func<string, bool>? isRunning = null,
                                    Action<FoundFile, long>? onFile = null, CancellationToken token = default)
    {
        var category = scan.Category;
        var blocked = WhyBlocked(category, isAdmin, isRunning);
        if (blocked != null)
            return new CleanResult(category, scan.Bytes, scan.Bytes, 0, 0, 0, "Not cleaned: " + blocked, scan.Incomplete);

        long deletedBytes = 0, deleted = 0, skipped = 0;
        string? message = null;
        if (category.Kind == CategoryKind.RecycleBin)
        {
            // Measure again: items may have been added since the scan.
            if (TryQueryRecycleBin(out var bytes, out var items) && items > 0)
            {
                const uint noConfirmation = 1, noProgress = 2, noSound = 4;
                var hr = SHEmptyRecycleBin(IntPtr.Zero, null, noConfirmation | noProgress | noSound);
                if (hr != 0) message = $"Windows could not empty the Recycle Bin (0x{hr:X8}).";
                var known = TryQueryRecycleBin(out var left, out var leftItems);
                if (known) (deletedBytes, deleted) = (Math.Max(0, bytes - left), Math.Max(0, items - leftItems));
            }
        }
        else if (category.Kind == CategoryKind.WindowsTool)
        {
            message = category.Tool?.Invoke() ?? (category.Tool == null ? "No cleanup tool is set." : null);
            var left = Scan(category, minimumAgeDays);
            // Freed space can only be known if the folder could be read both times.
            if (!scan.Incomplete && !left.Incomplete)
                (deletedBytes, deleted) = (Math.Max(0, scan.Bytes - left.Bytes), Math.Max(0, scan.Items - left.Items));
            else message ??= "Windows ran the cleanup. The amount freed could not be measured.";
        }
        else
        {
            foreach (var found in scan.Files)
            {
                if (token.IsCancellationRequested) { message = "Cancelled before finishing."; break; }
                long freed = 0;
                try
                {
                    var file = new FileInfo(found.Path);
                    if (!file.Exists) continue;
                    // Check again right before deleting, in case something changed after the scan.
                    if (!IsSafePath(found.Path, found.Root) ||
                        (scan.CutoffUtc is { } cutoff && !IsOld(file, cutoff))) skipped++;
                    else
                    {
                        var length = file.Length;
                        file.Delete();
                        freed = length;
                        deletedBytes += length;
                        deleted++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
                finally { onFile?.Invoke(found, freed); }
            }
        }
        var after = Scan(category, minimumAgeDays);
        return new CleanResult(category, scan.Bytes, after.Bytes, deletedBytes, deleted, skipped,
                               message ?? after.Error, after.Incomplete || after.Error != null);
    }

    /// <summary>True only if the file is inside root and no folder from root down is a link.</summary>
    public static bool IsSafePath(string path, string root)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.GetFullPath(path);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        while (current != null && current.Length >= fullRoot.Length)
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            current = Path.GetDirectoryName(current);
        }
        return true;
    }

    private static bool TryQueryRecycleBin(out long bytes, out long items)
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        var ok = SHQueryRecycleBin(null, ref info) == 0;
        (bytes, items) = ok ? (info.Bytes, info.Items) : (0, 0);
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo { public int Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref RecycleBinInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr window, string? root, uint flags);
}
