using System.Diagnostics;
using PCOptimizer.Services.Monitor;

namespace PCOptimizer;

/// <summary>The only process ever ended is a temporary idle copy of this app that the test starts.</summary>
public static class MonitorTests
{
    public static void Run(string work, Action<string, bool> check)
    {
        var cores = HardwareMonitor.OrderCores(new[] { ("0,10", 10.0), ("0,2", 2.0), ("0,_Total", 99.0), ("_Total", 99.0), ("0,1", 140.0) });
        check("per-core readings are ordered numerically without totals", cores.SequenceEqual(new[] { 100.0, 2, 10 }));
        check("an invalid core reading keeps its place",
              HardwareMonitor.OrderCores(new[] { ("0,1", double.NaN), ("0,0", 1.0), ("0,2", 3.0) }) is [1, var gap, 3] && double.IsNaN(gap));
        check("GPU and VRAM output is read, with gaps allowed",
              HardwareMonitor.ParseGpu("NVIDIA GeForce RTX 5060, 5, 1668, 8151, 44") is { Load: 5, MemoryUsedMb: 1668, MemoryTotalMb: 8151 } &&
              HardwareMonitor.ParseGpu("Card, [N/A], 10, 20, 30") is { Load: null } && HardwareMonitor.ParseGpu("error") == null);

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string? Reason(string name, string path, int session = 1, bool critical = false, int id = 500) =>
            ProcessProtection.Reason(id, name, path, session, 1, critical, 999);
        check("core, critical, service, unknown and own processes are locked",
              Reason("System", "", id: 4) != null && Reason("csrss.exe", "") != null &&
              Reason("svchost.exe", @"D:\fake\svchost.exe") != null && Reason("explorer.exe", windows + @"\explorer.exe") != null &&
              Reason("x.exe", @"C:\Apps\x.exe", critical: true) != null && Reason("x.exe", @"C:\Apps\x.exe", session: 0) != null &&
              Reason("x.exe", "") != null && Reason("x.exe", @"C:\Apps\x.exe", id: 999) != null &&
              Reason("thing.exe", windows + @"\System32\thing.exe") != null &&
              Reason("MsMpEng.exe", @"C:\ProgramData\Microsoft\Windows Defender\x\MsMpEng.exe") != null);
        check("ordinary apps can be ended", Reason("game.exe", @"C:\Games\game.exe") == null &&
              Reason("notepad.exe", windows + @"\System32\notepad.exe") == null &&
              Reason("notepad.exe", windows + @"\Temp\notepad.exe") != null);

        var monitor = new ProcessMonitor();
        monitor.Sample();
        Thread.Sleep(600);
        var rows = monitor.Sample();
        var own = rows.First(r => r.Id == Environment.ProcessId);
        check("the live list has paths, memory and CPU", rows.Count > 40 && own.Memory > 1_000_000 &&
              own.Path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
              rows.All(r => r.Cpu is >= 0 and <= 100));
        string[] vital = { "System", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
            "lsass.exe", "svchost.exe", "dwm.exe", "explorer.exe", "MsMpEng.exe" };
        check("every running critical Windows process is locked", own.LockReason != null &&
              rows.Where(r => vital.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).All(r => r.LockReason != null) &&
              rows.Any(r => r.Name.Equals("csrss.exe", StringComparison.OrdinalIgnoreCase)));
        var groups = monitor.Group(rows);
        check("processes are grouped by program without losing any", groups.Sum(g => g.Processes.Count) == rows.Count &&
              groups.Count < rows.Count);

        // A copy of the app outside the Windows folder, running as an idle helper.
        var folder = Path.Combine(work, "end-task");
        var source = AppContext.BaseDirectory;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(folder, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
        var program = Path.Combine(folder, Path.GetFileName(Environment.ProcessPath!));
        using var helper = Process.Start(new ProcessStartInfo(program, "--selftest-sleep") { UseShellExecute = false })!;
        try
        {
            Thread.Sleep(1500);
            var row = monitor.Sample().First(r => r.Id == helper.Id);
            check("an ordinary test process is not locked", row.LockReason == null);
            ProcessRow Copy(long created, string path, string? locked = null) => new()
            { Id = row.Id, Created = created, Name = row.Name, Path = path, LockReason = locked };
            var stale = monitor.End(Copy(row.Created + 1, row.Path)).Ok;
            var moved = monitor.End(Copy(row.Created, @"C:\Other\app.exe")).Ok;
            var locked = monitor.End(Copy(row.Created, row.Path, "test lock")).Ok;
            check("stale, changed or locked entries are never ended", !stale && !moved && !locked && !helper.HasExited);
            var ownRow = new ProcessRow { Id = own.Id, Created = own.Created, Name = own.Name, Path = own.Path };
            check("protection is checked again when ending", !monitor.End(ownRow).Ok);
            check("End Task ends the chosen process", monitor.End(row).Ok && helper.WaitForExit(3000));
        }
        finally { if (!helper.HasExited) helper.Kill(); }
    }
}
