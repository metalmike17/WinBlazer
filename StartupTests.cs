using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;
using PCOptimizer.Services.RegistryTools;
using PCOptimizer.Services.StartupTools;

namespace PCOptimizer;

/// <summary>Uses a throwaway registry key, temporary folders and fake tasks.</summary>
public static class StartupTests
{
    private sealed class FakeTasks : ITaskSource
    {
        public bool UserEnabled = true;
        public int WindowsChanges;
        public IReadOnlyList<StartupEntry> Read() => new[]
        {
            new StartupEntry { Id = @"task|\User", Name = "UserTask", Source = StartupSource.ScheduledTask,
                Location = "t", Command = "x", TaskPath = @"\User", Enabled = UserEnabled, Hidden = true },
            new StartupEntry { Id = @"task|\Microsoft\W", Name = "WinTask", Source = StartupSource.ScheduledTask,
                Location = "t", Command = "x", TaskPath = @"\Microsoft\W", Enabled = true, LockReason = "view only" }
        };
        public void SetEnabled(string path, bool enabled)
        {
            if (path == @"\User") UserEnabled = enabled; else WindowsChanges++;
        }
    }

    public static void Run(string work, Action<string, bool> check, List<string> log)
    {
        const string cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";
        var root = @"Software\PCOptimizerSelfTest-" + Guid.NewGuid().ToString("N");
        var user = Directory.CreateDirectory(Path.Combine(work, "startup-user")).FullName;
        var common = Directory.CreateDirectory(Path.Combine(work, "startup-common")).FullName;
        File.WriteAllText(Path.Combine(user, "script.cmd"), "rem");
        var store = new RegistryStore(root);
        var tasks = new FakeTasks();
        try
        {
            void Set(Hive hive, string path, string name, object value)
            {
                using var key = store.Create(hive, path);
                key.SetValue(name, value);
            }
            Set(Hive.CurrentUser, cv + "Run", "MyApp", "\"C:\\Program Files\\My\\app.exe\" -x");
            Set(Hive.CurrentUser, cv + "Run", "Off", "off.exe");
            Set(Hive.CurrentUser, cv + @"Explorer\StartupApproved\Run", "Off", new byte[] { 3, 0, 0, 0 });
            Set(Hive.CurrentUser, cv + "Run", "SecurityHealth", "SecurityHealthSystray.exe");
            Set(Hive.CurrentUser, cv + "RunOnce", "Once", "once.exe");
            Set(Hive.LocalMachine, cv + "Run", "Machine", "machine.exe");

            var catalog = new StartupCatalog(store, tasks, user, common);
            var changes = new StartupChanges(catalog, Path.Combine(work, "undo.json"));
            Dictionary<string, bool> State() => catalog.Scan().ToDictionary(e => e.Name, e => e.Enabled);
            var entries = catalog.Scan();
            check("startup scan finds registry, folder and task entries", entries.Count == 8);
            check("disabled state is read", !State()["Off"] && State()["MyApp"]);

            var targets = StartupChanges.BulkTargets(entries, false).Select(e => e.Name).OrderBy(n => n).ToList();
            check("Disable All skips locked, security, machine and already disabled entries",
                  targets.SequenceEqual(new[] { "MyApp", "script", "UserTask" }));
            var before = State();
            var report = changes.Apply(StartupChanges.BulkTargets(entries, false), false, false);
            var after = State();
            check("Disable All switches off exactly those entries", report.Changed == 3 && !after["MyApp"] &&
                  !after["script"] && !tasks.UserEnabled && after["SecurityHealth"] && after["Machine"] && after["Once"]);
            using (var run = store.Open(Hive.CurrentUser, cv + "Run"))
                check("nothing is deleted", run!.GetValue("MyApp") != null && File.Exists(Path.Combine(user, "script.cmd")));
            var undo = changes.UndoLast(false);
            check("undo restores the earlier state", undo.Changed == 3 &&
                  State().OrderBy(p => p.Key).SequenceEqual(before.OrderBy(p => p.Key)) && changes.Load().Count == 0);

            var locked = changes.Apply(entries.Where(e => e.Name is "WinTask" or "Once" or "Machine").ToList(), false, false);
            check("locked and machine entries are refused",
                  locked.Changed == 0 && locked.Problems.Count == 3 && tasks.WindowsChanges == 0);

            var blocker = Path.Combine(work, "blocker");
            File.WriteAllText(blocker, "x");
            var unable = new StartupChanges(catalog, Path.Combine(blocker, "undo.json"));
            var threw = false;
            try { unable.Apply(StartupChanges.BulkTargets(catalog.Scan(), false), false, false); }
            catch (IOException) { threw = true; }
            check("no undo record means no change", threw && State()["MyApp"] && tasks.UserEnabled);

            // With a protected folder, as the app uses it.
            var vault = Path.Combine(work, "startup-vault");
            var userRecord = Path.Combine(work, "undo-own.json");
            var guarded = new StartupChanges(catalog, userRecord, SecurityTests.Stand(vault));
            var targetsNow = catalog.Scan().Where(e => e.Name is "MyApp" or "Machine").ToList();
            var asBefore = State();
            guarded.Apply(targetsNow, false, true);
            check("administrator startup changes are recorded only in the protected folder",
                  !File.Exists(userRecord) && guarded.Load(true).Single().Items.Count == 2 && guarded.Load().Count == 0 &&
                  guarded.NothingToUndo(false).Contains("restart as administrator"));
            check("administrator undo restores from the protected record",
                  guarded.UndoLast(true).Changed == 2 &&
                  State().OrderBy(p => p.Key).SequenceEqual(asBefore.OrderBy(p => p.Key)));

            var machine = targetsNow.Single(e => e.Name == "Machine");
            File.WriteAllText(userRecord, System.Text.Json.JsonSerializer.Serialize(new[]
                { new UndoBatch(DateTime.Now, new() { new(machine.Id, machine.Name, false) }) }));
            var forgedAdmin = guarded.UndoLast(true);
            check("as administrator, a startup record planted in your own folder is not used",
                  forgedAdmin.Changed == 0 && State()["Machine"] &&
                  forgedAdmin.Problems.Single().Contains("open WinBlazer normally"));
            var forgedUser = guarded.UndoLast(false);
            check("without administrator rights it cannot change an all-users entry, and is dropped",
                  forgedUser.Changed == 0 && State()["Machine"] && guarded.Load().Count == 0);

            var distrusted = new StartupChanges(catalog, userRecord,
                new PCOptimizer.Core.ProtectedFolder(vault, _ => false, _ => { }));
            var refused = false;
            try { distrusted.Apply(targetsNow, false, true); }
            catch (IOException) { refused = true; }
            check("an untrusted protected folder means no startup change", refused && State()["MyApp"] && State()["Machine"]);

            var xml = XDocument.Parse("<StartupData><Process Name=\"C:\\a.exe\"><DiskUsage Units=\"bytes\">100</DiskUsage>" +
                "<CpuUsage Units=\"us\">200000</CpuUsage></Process><Process Name=\"C:\\a.exe\">" +
                "<DiskUsage Units=\"bytes\">100</DiskUsage><CpuUsage Units=\"us\">200000</CpuUsage></Process></StartupData>");
            var cost = BootImpact.ParseStartupInfo(xml)[@"C:\a.exe"];
            check("boot measurements are totalled and rated",
                  cost.CpuMilliseconds == 400 && cost.DiskBytes == 200 && BootImpact.Rate(cost) == "Medium" &&
                  BootImpact.Rate(new ProcessCost(1500, 0)) == "High" && BootImpact.Rate(new ProcessCost(10, 10)) == "Low");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }

        TestRealTask(check, log);
    }

    /// <summary>A harmless, temporary, hidden sign-in task, to test the real Task Scheduler interface.</summary>
    private static void TestRealTask(Action<string, bool> check, List<string> log)
    {
        var name = "PCOptimizer SelfTest " + Guid.NewGuid().ToString("N");
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        service.Connect();
        dynamic folder = service.GetFolder("\\");
        try
        {
            dynamic definition = service.NewTask(0);
            definition.Settings.Hidden = true;
            dynamic trigger = definition.Triggers.Create(9);   // at sign-in, this user only
            trigger.UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            dynamic action = definition.Actions.Create(0);
            action.Path = Path.Combine(Environment.SystemDirectory, "where.exe");
            try { folder.RegisterTaskDefinition(name, definition, 6, null, null, 3); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                log.Add("SKIPPED real task test (Windows refused to create it): " + ex.Message);
                return;
            }
            var source = new ScheduledTaskSource();
            StartupEntry? Find() => source.Read().FirstOrDefault(e => e.Name == name);
            var found = Find();
            check("a hidden sign-in task is listed", found is { Hidden: true, Enabled: true, LockReason: null } &&
                  found.Command.Contains("where.exe"));
            source.SetEnabled(found!.TaskPath!, false);
            var disabled = Find()?.Enabled == false;
            source.SetEnabled(found.TaskPath!, true);
            check("a real task can be disabled and re-enabled", disabled && Find()?.Enabled == true);
        }
        finally
        {
            try { folder.DeleteTask(name, 0); }
            catch (Exception ex) when (ex is COMException or FileNotFoundException) { }
        }
    }
}
