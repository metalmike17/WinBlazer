using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace PCOptimizer.Services.RegistryTools;

public sealed record RegistryCleanReport(string? BackupPath, int Deleted, IReadOnlyList<string> Problems);

public sealed record RestoreReport(string BackupPath, int Restored, int AlreadyPresent, int KeptNewer,
                                   IReadOnlyList<string> Problems);

/// <summary>
/// Backups made as administrator go in a folder only administrators can change. Backups from the
/// user's own folder can never restore machine-wide settings.
/// </summary>
public sealed class BackupFolders
{
    public string User { get; }
    public string? Protected { get; }
    public bool IsAdmin { get; }

    public BackupFolders(bool isAdmin, string? user = null, string? protectedFolder = null)
    {
        IsAdmin = isAdmin;
        User = user ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "PCOptimizerNet", "RegistryBackups");
        Protected = protectedFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCOptimizerNet-RegistryBackups");
    }

    public string PrepareForWriting()
    {
        if (!IsAdmin) { Directory.CreateDirectory(User); return User; }
        var folder = new DirectoryInfo(Protected!);
        if (!folder.Exists)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(administrators);
            foreach (var sid in new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                    AccessControlType.Allow));
            folder.Create(security);
            folder.Refresh();
        }
        if (!IsTrusted(folder))
            throw new IOException($"The protected backup folder is not secure, so nothing was changed: {Protected}");
        return Protected!;
    }

    /// <summary>Owned by administrators, not a link, and writable by nobody else.</summary>
    public static bool IsTrusted(DirectoryInfo folder)
    {
        if (!folder.Exists || folder.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var security = folder.GetAccessControl();
        bool Privileged(IdentityReference? id) => id is SecurityIdentifier s &&
            (s.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || s.IsWellKnown(WellKnownSidType.LocalSystemSid));
        if (!Privileged(security.GetOwner(typeof(SecurityIdentifier)))) return false;
        const FileSystemRights changes = FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .All(r => r.AccessControlType != AccessControlType.Allow || Privileged(r.IdentityReference) ||
                      (r.FileSystemRights & changes) == 0);
    }

    /// <summary>The newest backup. Names begin with the date and time, so they sort by age.</summary>
    public (string Path, bool FromProtected)? Latest()
    {
        var found = new List<(string Path, bool FromProtected)>();
        void Add(string? folder, bool isProtected)
        {
            try
            {
                if (folder == null || !Directory.Exists(folder)) return;
                if (isProtected && !IsTrusted(new DirectoryInfo(folder))) return;
                found.AddRange(Directory.GetFiles(folder, "registry-*.reg").Select(f => (f, isProtected)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Add(User, false);
        if (IsAdmin) Add(Protected, true);
        return found.Count == 0 ? null : found.OrderByDescending(f => Path.GetFileName(f.Path), StringComparer.Ordinal).First();
    }
}

public sealed class RegistryCleanerService
{
    private readonly RegistryStore _store;
    private readonly RegistryScanner _scanner;
    private readonly BackupFolders _folders;

    public RegistryCleanerService(RegistryStore store, RegistryScanner scanner, BackupFolders folders)
    {
        _store = store;
        _scanner = scanner;
        _folders = folders;
    }

    public bool CanDelete(RegistryIssue issue) => _folders.IsAdmin || issue.Hive == Hive.CurrentUser;

    private List<KeyData> Capture(RegistryIssue issue)
    {
        if (issue.ValueName == null) return _store.Capture(issue.Hive, issue.KeyPath);
        using var key = _store.Open(issue.Hive, issue.KeyPath) ?? throw new IOException("The key no longer exists.");
        var value = RegistryStore.ReadValue(key, issue.ValueName) ?? throw new IOException("The value no longer exists.");
        return new List<KeyData> { new(issue.Hive, issue.KeyPath, new[] { value }) };
    }

    /// <summary>
    /// Saves and verifies a backup of everything about to be removed, then deletes. If the backup cannot
    /// be written or verified, this throws before anything is deleted.
    /// </summary>
    public RegistryCleanReport Clean(IReadOnlyList<RegistryIssue> selected)
    {
        var problems = new List<string>();
        // Scan again so that only entries that are still broken are removed.
        var current = _scanner.Scan().Select(i => i.Identity).ToHashSet();
        var ready = new List<(RegistryIssue Issue, string Saved)>();
        var backup = new List<KeyData>();
        foreach (var issue in selected.DistinctBy(i => i.Identity))
        {
            try
            {
                if (!RegistryScanner.IsAllowed(issue.Hive, issue.KeyPath))
                    problems.Add($"{issue.Title}: outside the allowed locations, left alone.");
                else if (!CanDelete(issue)) problems.Add($"{issue.Title}: needs administrator rights.");
                else if (!current.Contains(issue.Identity)) problems.Add($"{issue.Title}: no longer detected, left alone.");
                else
                {
                    var data = Capture(issue);
                    ready.Add((issue, RegFile.Write(data)));
                    backup.AddRange(data);
                }
            }
            catch (Exception ex) when (IsExpected(ex))
            { problems.Add($"{issue.Title}: could not be backed up, so it was left alone. {ex.Message}"); }
        }
        if (ready.Count == 0) return new(null, 0, problems);

        var text = RegFile.Write(backup);
        var folder = _folders.PrepareForWriting();
        var path = Path.Combine(folder, $"registry-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.reg");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray();
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        var saved = File.ReadAllText(path);
        if (saved != text || RegFile.Write(RegFile.Read(saved)) != text)
            throw new IOException("The backup could not be verified. Nothing was deleted.");

        var deleted = 0;
        foreach (var (issue, expected) in ready)
        {
            try
            {
                // If it changed since the backup was taken, the backup is stale: leave it.
                if (RegFile.Write(Capture(issue)) != expected)
                { problems.Add($"{issue.Title}: changed during cleanup, left alone."); continue; }
                if (issue.ValueName == null)
                {
                    var slash = issue.KeyPath.LastIndexOf('\\');
                    using var parent = _store.Open(issue.Hive, issue.KeyPath[..slash], writable: true)
                                       ?? throw new IOException("The parent key is missing.");
                    parent.DeleteSubKeyTree(issue.KeyPath[(slash + 1)..]);
                }
                else
                {
                    using var key = _store.Open(issue.Hive, issue.KeyPath, writable: true)
                                    ?? throw new IOException("The key is missing.");
                    key.DeleteValue(issue.ValueName);
                }
                deleted++;
            }
            catch (Exception ex) when (IsExpected(ex)) { problems.Add($"{issue.Title}: not fully removed. {ex.Message}"); }
        }
        return new(path, deleted, problems);
    }

    /// <summary>Puts back what is missing from the newest backup. Existing values are never overwritten.</summary>
    public RestoreReport RestoreLatest()
    {
        var latest = _folders.Latest() ?? throw new FileNotFoundException("There is no backup to restore.");
        var keys = RegFile.Read(File.ReadAllText(latest.Path));   // checked in memory, then applied from memory

        // Check the whole file before writing anything.
        if (keys.Any(k => !RegistryScanner.IsAllowed(k.Hive, k.Path) && !IsChildOfAllowed(k)))
            throw new InvalidDataException("The backup refers to locations this app does not manage. Nothing was restored.");
        if (_folders.IsAdmin && !latest.FromProtected && keys.Any(k => k.Hive != Hive.CurrentUser))
            throw new InvalidDataException("This machine-wide backup is in a folder that is not protected, so the app " +
                "will not import it as administrator. Nothing was restored.");
        if (!_folders.IsAdmin && keys.Any(k => k.Hive != Hive.CurrentUser))
            throw new UnauthorizedAccessException("This backup needs administrator rights to restore.");

        int restored = 0, present = 0, newer = 0;
        var problems = new List<string>();
        foreach (var data in keys)
        {
            try
            {
                using var key = _store.Create(data.Hive, data.Path);
                foreach (var value in data.Values)
                {
                    var now = RegistryStore.ReadValue(key, value.Name);
                    if (now == null) { RegistryStore.WriteValue(key, value); restored++; }
                    else if (now.SameAs(value)) present++;
                    else newer++;
                }
            }
            catch (Exception ex) when (IsExpected(ex)) { problems.Add($"{data.Path}: {ex.Message}"); }
        }
        return new(latest.Path, restored, present, newer, problems);
    }

    /// <summary>Subkeys of a removed uninstall, App Paths or file type key.</summary>
    private static bool IsChildOfAllowed(KeyData key)
    {
        if (key.Path.Split('\\').Any(p => p.Length == 0 || p == "." || p == "..")) return false;
        var parts = key.Path;
        while (parts.LastIndexOf('\\') is var slash && slash > 0)
        {
            parts = parts[..slash];
            var wholeKeyRoot = RegistryScanner.UninstallRoots.Concat(RegistryScanner.AppPathRoots)
                .Any(r => r.Hive == key.Hive && parts.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase));
            if (wholeKeyRoot) return true;
        }
        return false;
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SecurityException or InvalidDataException or ArgumentException or InvalidOperationException;
}
