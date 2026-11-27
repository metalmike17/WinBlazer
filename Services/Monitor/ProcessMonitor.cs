using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PCOptimizer.Services.Monitor;

/// <summary>Raw values for one process. Times are in 100 ns units.</summary>
public sealed record RawProcess(int Id, string Name, int Session, long Created, long CpuTime, long PrivateMemory,
    long ReadBytes, long WriteBytes);

public sealed class ProcessRow
{
    public required int Id { get; init; }
    public required long Created { get; init; }
    public required string Name { get; init; }
    public string Path { get; init; } = "";
    public double Cpu { get; init; }
    public long Memory { get; init; }
    public double IoPerSecond { get; init; }
    /// <summary>Why the process cannot be ended, or null when it can.</summary>
    public string? LockReason { get; init; }
}

public sealed class AppGroup
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required List<ProcessRow> Processes { get; init; }
    public double Cpu => Processes.Sum(p => p.Cpu);
    public long Memory => Processes.Sum(p => p.Memory);
    public double IoPerSecond => Processes.Sum(p => p.IoPerSecond);
    public string? LockReason => Processes.Select(p => p.LockReason).FirstOrDefault(r => r != null);
}

/// <summary>Decides which processes may never be ended. When in doubt, a process is locked.</summary>
public static class ProcessProtection
{
    private static readonly HashSet<string> CoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "secure system", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost", "sihost", "explorer", "logonui", "audiodg",
        "ctfmon", "taskhostw", "runtimebroker", "shellhost", "shellexperiencehost", "startmenuexperiencehost",
        "searchhost", "textinputhost", "wudfhost", "spoolsv", "conhost", "wmiprvse", "dllhost",
        "msmpeng", "mpdefendercoreservice", "nissrv", "securityhealthservice", "securityhealthsystray"
    };

    /// <summary>Everyday programs that live in the Windows folder but are safe to end.</summary>
    private static readonly string[] OrdinaryWindowsTools =
    {
        @"System32\notepad.exe", @"notepad.exe", @"System32\cmd.exe", @"System32\mspaint.exe",
        @"System32\WindowsPowerShell\v1.0\powershell.exe"
    };

    public static string? Reason(int id, string name, string path, int session, int ownSession, bool critical,
                                 int ownId)
    {
        if (id <= 4) return "Core Windows process";
        if (id == ownId) return "This is WinBlazer";
        if (critical) return "Windows marks this process as critical";
        var plain = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (CoreNames.Contains(plain)) return "Core Windows or security process";
        if (session != ownSession) return "Belongs to Windows, a service or another user";
        if (path.Length == 0) return "Cannot be verified";
        if (path.Contains(@"\Windows Defender", StringComparison.OrdinalIgnoreCase)) return "Security software";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) &&
            !OrdinaryWindowsTools.Any(t => path.Equals(windows + t, StringComparison.OrdinalIgnoreCase)))
            return "Part of Windows";
        return null;
    }
}

/// <summary>Call Sample from one thread at a time.</summary>
public sealed class ProcessMonitor
{
    private readonly Dictionary<(int, long), (long Cpu, long Io)> _previous = new();
    private readonly Dictionary<(int, long), (string Path, bool Critical)> _identity = new();
    private readonly Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = new();
    private readonly int _ownId = Environment.ProcessId;
    private readonly int _session;

    public ProcessMonitor()
    {
        using var self = Process.GetCurrentProcess();
        _session = self.SessionId;
    }

    public List<ProcessRow> Sample()
    {
        var raw = Snapshot();
        var seconds = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        var rows = new List<ProcessRow>(raw.Count);
        var alive = new HashSet<(int, long)>();
        foreach (var p in raw)
        {
            if (p.Id == 0) continue;   // the idle process is unused CPU time
            var key = (p.Id, p.Created);
            alive.Add(key);
            if (!_identity.TryGetValue(key, out var who)) _identity[key] = who = Identify(p.Id);
            var io = p.ReadBytes + p.WriteBytes;
            double cpu = 0, rate = 0;
            if (seconds > 0 && _previous.TryGetValue(key, out var old))
            {
                cpu = Math.Clamp(100.0 * (p.CpuTime - old.Cpu) / 1e7 / seconds / Environment.ProcessorCount, 0, 100);
                rate = Math.Max(0, io - old.Io) / seconds;
            }
            _previous[key] = (p.CpuTime, io);
            rows.Add(new ProcessRow
            {
                Id = p.Id, Created = p.Created, Name = p.Name, Path = who.Path, Cpu = cpu, Memory = p.PrivateMemory,
                IoPerSecond = rate,
                LockReason = ProcessProtection.Reason(p.Id, p.Name, who.Path, p.Session, _session, who.Critical, _ownId)
            });
        }
        foreach (var gone in _previous.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            _previous.Remove(gone);
            _identity.Remove(gone);
        }
        return rows;
    }

    /// <summary>One group per program file; processes whose file cannot be read are grouped by name.</summary>
    public List<AppGroup> Group(IEnumerable<ProcessRow> rows) =>
        rows.GroupBy(r => r.Path.Length > 0 ? r.Path : "name:" + r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppGroup
            {
                Key = g.Key, Title = Title(g.First()),
                Processes = g.OrderByDescending(p => p.Memory).ToList()
            }).ToList();

    private string Title(ProcessRow row)
    {
        if (row.Path.Length == 0) return row.Name;
        if (_titles.TryGetValue(row.Path, out var title)) return title;
        try { title = FileVersionInfo.GetVersionInfo(row.Path).FileDescription?.Trim() ?? ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { title = ""; }
        title = title.Length == 0 ? row.Name : $"{title} ({row.Name})";
        return _titles[row.Path] = title;
    }

    // ---- Windows calls ----

    private const uint QueryLimited = 0x1000, Terminate = 0x0001, Synchronize = 0x00100000;

    private static string ImagePath(IntPtr handle)
    {
        var buffer = new StringBuilder(2048);
        var length = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : "";
    }

    private static (string, bool) Identify(int id)
    {
        var handle = OpenProcess(QueryLimited, false, id);
        if (handle == IntPtr.Zero) return ("", false);
        try { return (ImagePath(handle), IsProcessCritical(handle, out var critical) && critical); }
        finally { CloseHandle(handle); }
    }

    /// <summary>
    /// Ends one process. Everything is checked again on the same handle that is then ended, so a reused
    /// process id can never cause a different program to be closed.
    /// </summary>
    public (bool Ok, string Message) End(ProcessRow row)
    {
        if (row.LockReason != null) return (false, $"{row.Name} is locked: {row.LockReason}.");
        var handle = OpenProcess(QueryLimited | Terminate | Synchronize, false, row.Id);
        if (handle == IntPtr.Zero)
            return (false, $"{row.Name} could not be ended: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created != row.Created)
                return (false, $"{row.Name} has already closed.");
            var path = ImagePath(handle);
            if (!path.Equals(row.Path, StringComparison.OrdinalIgnoreCase))
                return (false, $"{row.Name} changed and was not ended.");
            if (!IsProcessCritical(handle, out var critical) || !ProcessIdToSessionId(row.Id, out var session))
                return (false, $"{row.Name} could not be verified and was not ended.");
            var reason = ProcessProtection.Reason(row.Id, row.Name, path, session, _session, critical, _ownId);
            if (reason != null) return (false, $"{row.Name} is locked: {reason}.");
            if (!TerminateProcess(handle, 1))
                return (false, $"{row.Name} could not be ended: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return WaitForSingleObject(handle, 3000) == 0
                ? (true, $"{row.Name} was ended.") : (false, $"{row.Name} has not closed yet.");
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>All processes in one call, including ones this account cannot open. 64-bit Windows only.</summary>
    public static List<RawProcess> Snapshot()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("The process list needs 64-bit Windows.");
        var size = 1 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(5, buffer, size, out var needed);
                if (status == unchecked((int)0xC0000004))   // buffer too small
                {
                    size = Math.Max(needed + 65536, size * 2);
                    continue;
                }
                if (status != 0) throw new IOException($"Windows could not list processes (0x{status:X8}).");
                var result = new List<RawProcess>();
                var entry = buffer;
                while (true)
                {
                    long Long(int offset) => Marshal.ReadInt64(entry, offset);
                    var id = (int)Long(80);
                    var length = (ushort)Marshal.ReadInt16(entry, 56) / 2;
                    var text = Marshal.ReadIntPtr(entry, 64);
                    var name = text == IntPtr.Zero ? (id == 0 ? "Idle" : "System") : Marshal.PtrToStringUni(text, length);
                    result.Add(new RawProcess(id, name, Marshal.ReadInt32(entry, 100), Long(32),
                        Long(40) + Long(48), Long(8), Long(232), Long(240)));
                    var next = Marshal.ReadInt32(entry, 0);
                    if (next == 0) return result;
                    entry += next;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool IsProcessCritical(IntPtr process, out bool critical);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(int id, out int session);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
