using Microsoft.Win32;
using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer;

/// <summary>Both registry hives are redirected under a throwaway key in HKCU.</summary>
public static class RegistryTests
{
    private const string Cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";

    public static void Run(string work, Action<string, bool> check)
    {
        var root = @"Software\PCOptimizerSelfTest-" + Guid.NewGuid().ToString("N");
        var real = Path.Combine(work, "registry");
        Directory.CreateDirectory(real);
        var exists = Path.Combine(real, "present.exe");
        File.WriteAllText(exists, "x");
        var gone = Path.Combine(real, "gone folder", "gone.exe");
        var store = new RegistryStore(root);
        var scanner = new RegistryScanner(store, id => id == "Known.Type");
        var backups = Path.Combine(real, "backups");
        var service = new RegistryCleanerService(store, scanner, new BackupFolders(false, backups));

        void Set(Hive hive, string path, string name, object value, RegistryValueKind kind = RegistryValueKind.String)
        {
            using var key = store.Create(hive, path);
            key.SetValue(name, value, kind);
        }
        try
        {
            var broken = Cv + @"App Paths\broken.exe";
            Set(Hive.CurrentUser, broken, "", gone);
            // Many value types, to prove the backup restores them exactly.
            Set(Hive.CurrentUser, broken, "quote\"and\\slash", "line1\r\nline2 \u00e9");
            Set(Hive.CurrentUser, broken, "number", unchecked((int)0xFFFFFFFE), RegistryValueKind.DWord);
            Set(Hive.CurrentUser, broken, "big", 1234567890123L, RegistryValueKind.QWord);
            Set(Hive.CurrentUser, broken, "bytes", new byte[] { 0, 1, 255 }, RegistryValueKind.Binary);
            Set(Hive.CurrentUser, broken, "list", new[] { "a", "b" }, RegistryValueKind.MultiString);
            Set(Hive.CurrentUser, broken, "expand", "%TEMP%\\x", RegistryValueKind.ExpandString);
            Set(Hive.CurrentUser, broken + @"\child", "inner", "value");
            Set(Hive.CurrentUser, Cv + @"App Paths\fine.exe", "", exists);

            var run = Cv + "Run";
            Set(Hive.CurrentUser, run, "Dead", $"\"{gone}\" -quiet");
            Set(Hive.CurrentUser, run, "Alive", $"\"{exists}\"");
            Set(Hive.CurrentUser, run, "Ambiguous", "somecommand /x");
            Set(Hive.CurrentUser, run, "Network", @"\\server\share\app.exe");

            var uninstall = Cv + @"Uninstall\";
            Set(Hive.CurrentUser, uninstall + "orphan", "DisplayName", "Orphan App");
            Set(Hive.CurrentUser, uninstall + "orphan", "InstallLocation", Path.GetDirectoryName(gone)!);
            Set(Hive.CurrentUser, uninstall + "invalid", "DisplayName", "Invalid App");
            Set(Hive.CurrentUser, uninstall + "invalid", "UninstallString", gone);
            Set(Hive.CurrentUser, uninstall + "msi", "DisplayName", "Installer App");
            Set(Hive.CurrentUser, uninstall + "msi", "WindowsInstaller", 1, RegistryValueKind.DWord);
            Set(Hive.CurrentUser, uninstall + "msi", "UninstallString", gone);
            Set(Hive.CurrentUser, uninstall + "working", "DisplayName", "Working App");
            Set(Hive.CurrentUser, uninstall + "working", "UninstallString", exists + " /remove");

            Set(Hive.LocalMachine, Cv + "SharedDLLs", gone, 1, RegistryValueKind.DWord);
            Set(Hive.LocalMachine, Cv + "SharedDLLs", exists, 1, RegistryValueKind.DWord);
            using (store.Create(Hive.CurrentUser, @"SOFTWARE\Classes\.empty")) { }
            Set(Hive.CurrentUser, @"SOFTWARE\Classes\.dangling", "", "Missing.Type");
            Set(Hive.CurrentUser, @"SOFTWARE\Classes\.good", "", "Known.Type");

            var issues = scanner.Scan();
            var titles = issues.Select(i => i.Title).ToHashSet();
            check("registry scan finds exactly the broken entries",
                  issues.Count == 7 && new[] { "broken.exe", "Dead", "Orphan App", "Invalid App", gone, ".empty", ".dangling" }
                      .All(titles.Contains));

            var machine = issues.First(i => i.Hive == Hive.LocalMachine);
            var refused = service.Clean(new[] { machine });
            check("machine entry is refused without administrator rights",
                  refused.Deleted == 0 && refused.BackupPath == null && scanner.Scan().Count == 7);

            // A backup that cannot be written must stop all deletion.
            var blocker = Path.Combine(real, "not-a-folder");
            File.WriteAllText(blocker, "x");
            var failing = new RegistryCleanerService(store, scanner, new BackupFolders(false, blocker));
            var threw = false;
            try { failing.Clean(issues.Where(i => i.Hive == Hive.CurrentUser).ToList()); }
            catch (IOException) { threw = true; }
            check("no backup means no deletion", threw && scanner.Scan().Count == 7);

            var outside = issues[0] with { KeyPath = @"SOFTWARE\Important", Hive = Hive.CurrentUser };
            check("entries outside allowed locations are refused", service.Clean(new[] { outside }).Deleted == 0);

            var original = RegFile.Write(store.Capture(Hive.CurrentUser, broken));
            var mine = issues.Where(i => i.Hive == Hive.CurrentUser).ToList();
            var report = service.Clean(mine);
            check("backup is created and the ticked entries are deleted",
                  report.Deleted == 6 && report.Problems.Count == 0 && File.Exists(report.BackupPath) &&
                  scanner.Scan().Count == 1);
            using (var key = store.Open(Hive.CurrentUser, run))
                check("healthy values are untouched",
                      key!.GetValue("Alive") != null && key.GetValue("Ambiguous") != null && key.GetValue("Dead") == null);

            Set(Hive.CurrentUser, run, "Dead", "newer value");
            var restore = service.RestoreLatest();
            using (var key = store.Open(Hive.CurrentUser, run))
                check("restore keeps newer values", restore.KeptNewer == 1 && (string?)key!.GetValue("Dead") == "newer value");
            check("restore rebuilds a deleted key exactly",
                  RegFile.Write(store.Capture(Hive.CurrentUser, broken)) == original && restore.Problems.Count == 0);
            using (var empty = store.Open(Hive.CurrentUser, @"SOFTWARE\Classes\.empty"))
                check("restore brings back empty keys", empty != null);

            // A tampered backup must be rejected as a whole.
            var evil = new[]
            {
                new KeyData(Hive.CurrentUser, @"SOFTWARE\Classes\.ok", Array.Empty<RawValue>()),
                new KeyData(Hive.CurrentUser, @"SOFTWARE\Important", Array.Empty<RawValue>())
            };
            File.WriteAllText(Path.Combine(backups, "registry-99999999-evil.reg"), RegFile.Write(evil));
            var rejected = false;
            try { service.RestoreLatest(); } catch (InvalidDataException) { rejected = true; }
            using (var ok = store.Open(Hive.CurrentUser, @"SOFTWARE\Classes\.ok"))
                check("backup with foreign locations is rejected entirely", rejected && ok == null);

            File.WriteAllText(Path.Combine(backups, "registry-99999999-evil.reg"), RegFile.Write(new[]
                { new KeyData(Hive.LocalMachine, Cv + @"App Paths\x.exe", Array.Empty<RawValue>()) }));
            var elevated = new RegistryCleanerService(store, scanner,
                new BackupFolders(true, backups, Path.Combine(real, "no-protected-folder")));
            var blocked = false;
            try { elevated.RestoreLatest(); } catch (InvalidDataException) { blocked = true; }
            check("administrator restore ignores machine entries from an unprotected folder", blocked);
            check("a user-writable folder is not trusted",
                  !BackupFolders.IsTrusted(new DirectoryInfo(real)));

            // Regression: a file that exists only in SysWOW64 must not be reported when listed as System32.
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var only32 = Directory.EnumerateFiles(Path.Combine(windows, "SysWOW64"))
                .Select(Path.GetFileName)
                .FirstOrDefault(n => !File.Exists(Path.Combine(windows, "System32", n!)));
            check("32-bit files listed under System32 are not reported missing",
                  only32 != null && !RegistryScanner.IsDefinitelyMissing(Path.Combine(windows, "System32", only32)) &&
                  RegistryScanner.IsDefinitelyMissing(Path.Combine(windows, "System32", Guid.NewGuid() + ".dll")));

            check("command parsing",
                  RegistryScanner.ProgramPath("\"C:\\A B\\x.exe\" /s") == @"C:\A B\x.exe" &&
                  RegistryScanner.ProgramPath(@"C:\A B\x.exe /s") == @"C:\A B\x.exe" &&
                  RegistryScanner.ProgramPath("rundll32 thing.dll") == null &&
                  RegistryScanner.ProgramPath("MsiExec.exe /X{1}") == null);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }
}
