using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer.Services.StartupTools;

/// <summary>Reads startup entries and switches them on or off. Nothing is ever deleted.</summary>
public sealed class StartupCatalog
{
    private const string Windows = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";
    private const string Windows32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\";
    private const string Approved = Windows + @"Explorer\StartupApproved\";

    private readonly RegistryStore _store;
    private readonly ITaskSource _tasks;
    private readonly string _userFolder, _commonFolder;

    public List<string> Warnings { get; } = new();

    public StartupCatalog(RegistryStore? store = null, ITaskSource? tasks = null,
                          string? userFolder = null, string? commonFolder = null)
    {
        _store = store ?? new RegistryStore();
        _tasks = tasks ?? new ScheduledTaskSource();
        _userFolder = userFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _commonFolder = commonFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
    }

    public List<StartupEntry> Scan()
    {
        Warnings.Clear();
        var entries = new List<StartupEntry>();
        RunKey(entries, Hive.CurrentUser, Windows + "Run", "Run", null);
        RunKey(entries, Hive.LocalMachine, Windows + "Run", "Run", null);
        RunKey(entries, Hive.LocalMachine, Windows32 + "Run", "Run32", null);
        // These have no Windows on/off switch, so they are listed but not changed.
        const string once = "Runs once, then Windows removes it: view only.";
        const string policy = "Set by policy: view only.";
        RunKey(entries, Hive.CurrentUser, Windows + "RunOnce", null, once);
        RunKey(entries, Hive.LocalMachine, Windows + "RunOnce", null, once);
        RunKey(entries, Hive.LocalMachine, Windows32 + "RunOnce", null, once);
        RunKey(entries, Hive.CurrentUser, Windows + @"Policies\Explorer\Run", null, policy);
        RunKey(entries, Hive.LocalMachine, Windows + @"Policies\Explorer\Run", null, policy);
        RunKey(entries, Hive.CurrentUser, Windows32 + "Run", null, "32-bit user key: view only.");

        Folder(entries, Hive.CurrentUser, _userFolder, "Your Startup folder");
        Folder(entries, Hive.LocalMachine, _commonFolder, "All users Startup folder");

        try { entries.AddRange(_tasks.Read()); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        { Warnings.Add("Scheduled tasks could not be read: " + ex.Message); }

        // Entries the user can control come first; view-only ones follow.
        return entries.Select(Describe).OrderBy(e => e.LockReason != null)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void RunKey(List<StartupEntry> entries, Hive hive, string path, string? approvedKey, string? lockReason)
    {
        try
        {
            using var key = _store.Open(hive, path);
            if (key == null) return;
            foreach (var name in key.GetValueNames())
            {
                var command = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
                if (name == "" || string.IsNullOrWhiteSpace(command)) continue;
                var approvedPath = approvedKey == null ? null : Approved + approvedKey;
                entries.Add(new StartupEntry
                {
                    Id = $"reg|{hive}|{path}|{name}", Name = name, Source = StartupSource.RunKey,
                    Location = $@"{RegistryStore.HiveName(hive)}\{path}", Command = command,
                    ProgramPath = ResolveProgram(command),
                    Enabled = approvedPath == null || IsEnabled(hive, approvedPath, name),
                    NeedsAdmin = hive == Hive.LocalMachine, LockReason = lockReason,
                    ApprovedHive = hive, ApprovedPath = approvedPath, ApprovedValue = name
                });
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        { Warnings.Add($"Could not read {path}: {ex.Message}"); }
    }

    private void Folder(List<StartupEntry> entries, Hive hive, string folder, string label)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (var file in Directory.GetFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var (command, program) = ReadFolderItem(file);
                entries.Add(new StartupEntry
                {
                    Id = $"folder|{hive}|{name}", Name = Path.GetFileNameWithoutExtension(name),
                    Source = StartupSource.StartupFolder, Location = $"{label}: {file}", Command = command,
                    ProgramPath = program, Enabled = IsEnabled(hive, Approved + "StartupFolder", name),
                    NeedsAdmin = hive == Hive.LocalMachine, ApprovedHive = hive,
                    ApprovedPath = Approved + "StartupFolder", ApprovedValue = name
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Warnings.Add($"Could not read {label}: {ex.Message}"); }
    }

    private static (string Command, string? Program) ReadFolderItem(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return (file, file);
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
            dynamic link = shell.CreateShortcut(file);
            string target = link.TargetPath ?? "";
            string arguments = link.Arguments ?? "";
            return target == "" ? (file, null)
                : ($"\"{target}\" {arguments}".Trim(), RegistryScanner.CleanPath(target));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        { return (file, null); }
    }

    /// <summary>The program a command starts, or null if unclear. Network paths are never opened.</summary>
    public static string? ResolveProgram(string command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        if (RegistryScanner.ProgramPath(expanded) is { } path) return path;
        // A bare name such as "rundll32.exe" refers to the Windows system folder.
        var first = expanded.StartsWith('"') ? expanded.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                                             : expanded.Split(' ')[0];
        if (string.IsNullOrEmpty(first) || first.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return null;
        if (!first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) first += ".exe";
        var system = Path.Combine(Environment.SystemDirectory, first);
        return File.Exists(system) ? system : null;
    }

    private static readonly string[] Hosts =
        { "rundll32", "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "conhost", "explorer" };

    private static StartupEntry Describe(StartupEntry entry)
    {
        var publisher = "Unknown";
        var program = entry.ProgramPath;
        if (program != null)
        {
            var name = Path.GetFileNameWithoutExtension(program);
            if (Hosts.Contains(name, StringComparer.OrdinalIgnoreCase))
                publisher = $"Unknown (runs through {name})";   // the host's publisher says nothing about the script
            else
            {
                try
                {
                    if (File.Exists(program) &&
                        FileVersionInfo.GetVersionInfo(program).CompanyName is { Length: > 0 } company)
                        publisher = company.Trim();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        var security = new[] { "SecurityHealth", "Defender" }.Any(word =>
            entry.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            entry.Command.Contains(word, StringComparison.OrdinalIgnoreCase));
        return entry with
        {
            Publisher = publisher,
            BulkSkipReason = security ? "Windows security item" : null
        };
    }

    // ---- The on/off switch (the same values Task Manager uses) ----

    private bool IsEnabled(Hive hive, string path, string name)
    {
        using var key = _store.Open(hive, path);
        // An odd first byte means disabled. No value means enabled.
        return key?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    public void SetEnabled(StartupEntry entry, bool enabled)
    {
        if (entry.LockReason != null) throw new InvalidOperationException(entry.LockReason);
        if (entry.Source == StartupSource.ScheduledTask) { _tasks.SetEnabled(entry.TaskPath!, enabled); return; }
        var data = new byte[12];
        data[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
        using var key = _store.Create(entry.ApprovedHive, entry.ApprovedPath!);
        key.SetValue(entry.ApprovedValue!, data, RegistryValueKind.Binary);
    }
}
