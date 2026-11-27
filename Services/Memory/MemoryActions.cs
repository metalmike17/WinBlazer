using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services.MemoryTools;

public enum MemoryAction { EmptyWorkingSets, FlushModifiedList, PurgeStandbyList, FlushFileCache }

public sealed record ActionResult(bool Ok, string Message);

public static class MemoryActions
{
    // NtSetSystemInformation(SystemMemoryListInformation) commands, as used by Microsoft's RAMMap.
    private const int SystemMemoryListInformation = 80;
    private const int CommandEmptyWorkingSets = 2, CommandFlushModified = 3, CommandPurgeStandby = 4;

    public static string Name(MemoryAction action) => action switch
    {
        MemoryAction.EmptyWorkingSets => "Empty Working Sets",
        MemoryAction.FlushModifiedList => "Flush Modified List",
        MemoryAction.PurgeStandbyList => "Purge Standby List",
        _ => "Flush System File Cache"
    };

    public static bool NeedsAdmin(MemoryAction action) => action != MemoryAction.EmptyWorkingSets;

    public static ActionResult Run(MemoryAction action, bool isAdmin)
    {
        if (NeedsAdmin(action) && !isAdmin) return new(false, "Needs administrator rights.");
        try
        {
            switch (action)
            {
                case MemoryAction.EmptyWorkingSets:
                    if (isAdmin) return MemoryListCommand(CommandEmptyWorkingSets);
                    return EmptyOwnProcesses();
                case MemoryAction.FlushModifiedList: return MemoryListCommand(CommandFlushModified);
                case MemoryAction.PurgeStandbyList: return MemoryListCommand(CommandPurgeStandby);
                default:
                    EnablePrivilege("SeIncreaseQuotaPrivilege");
                    // -1 for both sizes asks Windows to empty the file cache; limits are not changed.
                    return SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0)
                        ? new(true, "Done.")
                        : new(false, "Windows refused: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
        }
        catch (Win32Exception ex) { return new(false, ex.Message); }
    }

    private static ActionResult MemoryListCommand(int command)
    {
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        var status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        return status == 0 ? new(true, "Done.") : new(false, $"Windows refused the request (0x{status:X8}).");
    }

    /// <summary>Without administrator rights, only processes in this sign-in session that allow it.</summary>
    private static ActionResult EmptyOwnProcesses()
    {
        int done = 0, skipped = 0;
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.SessionId != self.SessionId) continue;
                if (TryEmptyWorkingSet(process.Id)) done++; else skipped++;
            }
        }
        return new(done > 0, $"Trimmed {done} of your processes ({skipped} could not be accessed). " +
                             "Run as administrator to include all processes.");
    }

    public static bool TryEmptyWorkingSet(int processId)
    {
        const uint queryLimited = 0x1000, setQuota = 0x0100;
        var handle = OpenProcess(queryLimited | setQuota, false, processId);
        if (handle == IntPtr.Zero) return false;
        try { return K32EmptyWorkingSet(handle); }
        finally { CloseHandle(handle); }
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x08, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var privileges = new TokenPrivileges { Count = 1, Attributes = 2 };
            if (!LookupPrivilegeValue(null, name, out privileges.Luid) ||
                !AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // The call can "succeed" without granting the privilege.
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error);
        }
        finally { CloseHandle(token); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetSystemFileCacheSize(IntPtr min, IntPtr max, int flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool K32EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returned);
}
