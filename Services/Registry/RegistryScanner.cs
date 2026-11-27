using System.Security;
using Microsoft.Win32;

namespace PCOptimizer.Services.RegistryTools;

public enum Risk { Safe, Moderate, Risky }

public enum IssueKind { InvalidUninstall, OrphanedUninstall, SharedDll, FileExtension, AppPath, Leftover }

/// <summary>ValueName null means the whole key would be removed.</summary>
public sealed record RegistryIssue(IssueKind Kind, Risk Risk, string Title, string Reason, Hive Hive,
                                   string KeyPath, string? ValueName)
{
    public string Location => $@"{RegistryStore.HiveName(Hive)}\{KeyPath}" +
                              (ValueName == null ? "" : $"  →  {(ValueName == "" ? "(Default)" : ValueName)}");
    public (Hive, string, string?) Identity => (Hive, KeyPath.ToUpperInvariant(), ValueName?.ToUpperInvariant());
}

/// <summary>Read-only. When in doubt, nothing is reported.</summary>
public sealed class RegistryScanner
{
    private const string Windows = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";
    private const string Windows32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\";
    public const string Classes = @"SOFTWARE\Classes";

    private readonly RegistryStore _store;
    private readonly Func<string, bool>? _progIdExists;

    /// <summary>Locations that could not be read during the last scan.</summary>
    public int Unreadable { get; private set; }

    public RegistryScanner(RegistryStore store, Func<string, bool>? progIdExists = null)
    {
        _store = store;
        _progIdExists = progIdExists;
    }

    public static string CategoryName(IssueKind kind) => kind switch
    {
        IssueKind.InvalidUninstall => "Invalid uninstall entries",
        IssueKind.OrphanedUninstall => "Orphaned uninstall keys",
        IssueKind.SharedDll => "Missing shared DLLs",
        IssueKind.FileExtension => "Unused file extensions",
        IssueKind.AppPath => "Obsolete App Paths",
        _ => "Leftovers from removed software (startup entries)"
    };

    private static IEnumerable<(Hive Hive, string Path)> Both(string tail) => new[]
    {
        (Hive.CurrentUser, Windows + tail), (Hive.LocalMachine, Windows + tail), (Hive.LocalMachine, Windows32 + tail)
    };

    public static IEnumerable<(Hive Hive, string Path)> UninstallRoots => Both("Uninstall");
    public static IEnumerable<(Hive Hive, string Path)> AppPathRoots => Both("App Paths");
    public static IEnumerable<(Hive Hive, string Path)> RunRoots => Both("Run").Concat(Both("RunOnce"));
    public static IEnumerable<(Hive Hive, string Path)> SharedDllRoots => new[]
        { (Hive.LocalMachine, Windows + "SharedDLLs"), (Hive.LocalMachine, Windows32 + "SharedDLLs") };

    public List<RegistryIssue> Scan(CancellationToken token = default)
    {
        Unreadable = 0;
        var issues = new List<RegistryIssue>();
        foreach (var (hive, path) in UninstallRoots) ForEachSubKey(hive, path, token, (name, key) =>
        {
            var issue = CheckUninstall(hive, $@"{path}\{name}", name, key);
            if (issue != null) issues.Add(issue);
        });

        foreach (var (hive, path) in AppPathRoots) ForEachSubKey(hive, path, token, (name, key) =>
        {
            var target = ProgramPath(Text(key, ""));
            if (target != null && IsDefinitelyMissing(target))
                issues.Add(new(IssueKind.AppPath, Risk.Safe, name, "The program it points to is missing: " + target,
                               hive, $@"{path}\{name}", null));
        });

        foreach (var (hive, path) in SharedDllRoots) WithKey(hive, path, key =>
        {
            foreach (var name in key.GetValueNames())
            {
                token.ThrowIfCancellationRequested();
                if (CleanPath(name) is { } file && IsDefinitelyMissing(file))
                    issues.Add(new(IssueKind.SharedDll, Risk.Moderate, file,
                        "This shared file is listed but missing. An installer repair may expect the entry.",
                        hive, path, name));
            }
        });

        foreach (var (hive, path) in RunRoots) WithKey(hive, path, key =>
        {
            foreach (var name in key.GetValueNames())
            {
                var target = ProgramPath(Text(key, name));
                if (name != "" && target != null && IsDefinitelyMissing(target))
                    issues.Add(new(IssueKind.Leftover, Risk.Moderate, name,
                        "Starts a program that is missing: " + target, hive, path, name));
            }
        });

        foreach (var hive in new[] { Hive.CurrentUser, Hive.LocalMachine })
            ForEachSubKey(hive, Classes, token, (name, key) =>
            {
                var issue = CheckExtension(hive, name, key);
                if (issue != null) issues.Add(issue);
            });
        return issues;
    }

    private RegistryIssue? CheckUninstall(Hive hive, string path, string name, RegistryKey key)
    {
        var title = Text(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(title)) return null;
        // Windows components, updates and Windows Installer products are managed by Windows.
        if (Equals(key.GetValue("SystemComponent"), 1) || Equals(key.GetValue("WindowsInstaller"), 1) ||
            Text(key, "ParentKeyName") != null || Text(key, "ReleaseType") != null) return null;

        var command = Text(key, "UninstallString");
        var uninstaller = ProgramPath(command);
        if (!string.IsNullOrWhiteSpace(command) && uninstaller == null) return null;   // cannot tell
        if (uninstaller != null && !IsDefinitelyMissing(uninstaller)) return null;

        var folder = CleanPath(Text(key, "InstallLocation"));
        if (folder != null && IsDefinitelyMissing(folder))
            return new(IssueKind.OrphanedUninstall, Risk.Moderate, title,
                $"The install folder is missing ({folder}) and there is no working uninstaller.", hive, path, null);
        if (uninstaller != null)
            return new(IssueKind.InvalidUninstall, folder == null ? Risk.Moderate : Risk.Risky, title,
                "The uninstaller is missing: " + uninstaller +
                (folder == null ? "" : ". The install folder still exists, so the app may still be there."),
                hive, path, null);
        return null;
    }

    private RegistryIssue? CheckExtension(Hive hive, string name, RegistryKey key)
    {
        if (name.Length < 2 || name[0] != '.' || key.SubKeyCount > 0) return null;
        var names = key.GetValueNames();
        var path = $@"{Classes}\{name}";
        var machine = hive == Hive.LocalMachine;
        if (names.Length == 0)
            return new(IssueKind.FileExtension, machine ? Risk.Risky : Risk.Safe, name,
                       "The file type key is completely empty.", hive, path, null);
        if (names.Length == 1 && names[0] == "" && Text(key, "") is { Length: > 0 } progId && !ProgIdExists(progId))
            return new(IssueKind.FileExtension, machine ? Risk.Risky : Risk.Moderate, name,
                       $"It points to a file type '{progId}' that is not registered.", hive, path, null);
        return null;
    }

    private bool ProgIdExists(string progId)
    {
        if (_progIdExists != null) return _progIdExists(progId);
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(progId);
            return key != null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { return true; }
    }

    private static string? Text(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    private void WithKey(Hive hive, string path, Action<RegistryKey> action)
    {
        try
        {
            using var key = _store.Open(hive, path);
            if (key != null) action(key);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        { Unreadable++; }
    }

    private void ForEachSubKey(Hive hive, string path, CancellationToken token, Action<string, RegistryKey> action) =>
        WithKey(hive, path, parent =>
        {
            foreach (var name in parent.GetSubKeyNames())
            {
                token.ThrowIfCancellationRequested();
                WithKey(hive, $@"{path}\{name}", key => action(name, key));
            }
        });

    // ---- Paths ----

    /// <summary>A full local path, or null if it cannot be judged reliably.</summary>
    public static string? CleanPath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var path = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
        if (path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\') return null;
        if (path.IndexOfAny(new[] { '%', '*', '?', '"', '<', '>', '|' }) >= 0 || path.IndexOf(':', 2) >= 0) return null;
        // Store app folders are protected, and "sysnative" only exists for some processes.
        if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\sysnative\", StringComparison.OrdinalIgnoreCase)) return null;
        return path;
    }

    /// <summary>Gets the program from a command line, or null if it is ambiguous.</summary>
    public static string? ProgramPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? CleanPath(text[1..close]) : null;
        }
        var end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        end += 4;
        if (end < text.Length && text[end] != ' ' && text[end] != ',') return null;
        return CleanPath(text[..end]);
    }

    /// <summary>
    /// 32-bit programs that write "System32" really use SysWOW64, so a System32 path has a second
    /// possible location. Returns null for any other path.
    /// </summary>
    public static string? Redirected(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var prefix = Path.Combine(windows, "System32") + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(windows, "SysWOW64", path[prefix.Length..])
            : null;
    }

    /// <summary>Missing from every place the path could refer to.</summary>
    public static bool IsDefinitelyMissing(string path) =>
        IsAbsent(path) && (Redirected(path) is not { } other || IsAbsent(other));

    /// <summary>True only when Windows says it is not there, on a connected fixed drive.</summary>
    private static bool IsAbsent(string path)
    {
        try
        {
            var drive = new DriveInfo(path[..1]);
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return false;
            File.GetAttributes(path);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException) { return false; }
    }

    /// <summary>The only places this module may ever delete from or restore to.</summary>
    public static bool IsAllowed(Hive hive, string path)
    {
        if (path.Split('\\').Any(p => p.Length == 0 || p == "." || p == "..")) return false;
        bool Under(IEnumerable<(Hive Hive, string Path)> roots) => roots.Any(r => r.Hive == hive &&
            path.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase));
        bool Exactly(IEnumerable<(Hive Hive, string Path)> roots) => roots.Any(r => r.Hive == hive &&
            path.Equals(r.Path, StringComparison.OrdinalIgnoreCase));
        if (Under(UninstallRoots) || Under(AppPathRoots) || Exactly(RunRoots) || Exactly(SharedDllRoots)) return true;
        return path.StartsWith(Classes + @"\.", StringComparison.OrdinalIgnoreCase) &&
               path.IndexOf('\\', Classes.Length + 1) < 0;
    }
}
