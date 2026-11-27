using System.Security.AccessControl;
using System.Security.Principal;
using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer.Core;

/// <summary>
/// Where originals are kept for changes that need administrator rights. Everyone may read it, but only
/// administrators may change it, so a program without those rights cannot alter what gets restored.
/// </summary>
public sealed class ProtectedFolder
{
    private readonly Func<DirectoryInfo, bool> _isTrusted;
    private readonly Action<DirectoryInfo> _create;

    public string FullPath { get; }

    /// <summary>The two functions are replaced only by the self-test, which has no administrator rights.</summary>
    public ProtectedFolder(string path, Func<DirectoryInfo, bool>? isTrusted = null, Action<DirectoryInfo>? create = null)
    {
        FullPath = Path.GetFullPath(path);
        _isTrusted = isTrusted ?? BackupFolders.IsTrusted;
        _create = create ?? (folder => folder.Create(Security()));
    }

    public static ProtectedFolder ForThisPc() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PCOptimizerNet-AdminBackups"));

    public static DirectorySecurity Security()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        void Allow(IdentityReference who, FileSystemRights rights) => security.AddAccessRule(new FileSystemAccessRule(
            who, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        Allow(administrators, FileSystemRights.FullControl);
        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute);
        return security;
    }

    /// <summary>A file of the current user's own; the folder is shared by everyone on the PC.</summary>
    public string UserFile(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return Path.Combine(FullPath, $"{name}-{identity.User!.Value}.json");
    }

    /// <summary>True if it exists and can be trusted. Anything in an untrusted folder is ignored.</summary>
    public bool IsUsable
    {
        get
        {
            try { return Directory.Exists(FullPath) && _isTrusted(new DirectoryInfo(FullPath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>Creates the folder if needed. Throws rather than write into one that others can change.</summary>
    public void PrepareForWriting()
    {
        var folder = new DirectoryInfo(FullPath);
        if (!folder.Exists) _create(folder);
        if (!IsUsable)
            throw new IOException($"The protected backup folder is not secure, so nothing was changed: {FullPath}");
    }
}
