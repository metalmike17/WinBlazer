using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using PCOptimizer.Core;
using PCOptimizer.Services.GameTools;
using PCOptimizer.Services.MemoryTools;
using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer;

/// <summary>
/// Uses a fake system and a throwaway registry key. The only real calls read the GPU setting and power
/// plans. No real setting is changed.
/// </summary>
public static class GameModeTests
{
    private sealed class Fake : IGameSystem
    {
        public static readonly Guid Balanced = Guid.NewGuid(), Ultimate = Guid.NewGuid(), Other = Guid.NewGuid();
        public Guid Plan = Balanced;
        public uint? Gpu = 5;
        public bool GpuFails, Recovery;
        public readonly HashSet<string> Apps = new() { @"C:\a\closes.exe", @"C:\a\stays.exe" };
        public readonly List<string> Launched = new();
        public Guid ActivePlan() => Plan;
        public Guid UltimatePlan() => Ultimate;
        public bool IsUltimate(Guid plan) => plan == Ultimate;
        public void SetPlan(Guid plan) => Plan = plan;
        public GpuPowerMode ReadGpu() => new(Gpu);
        public void WriteGpu(uint? value) { if (GpuFails) throw new IOException("denied"); Gpu = value; }
        public bool IsRunning(string path) => Apps.Contains(path);
        public bool CloseApp(string path) => !path.Contains("stays") && Apps.Remove(path);
        public void Launch(string path) { Launched.Add(path); Apps.Add(path); }
        public ActionResult PurgeStandby() => new(false, "test");
        public void SetSignInRecovery(bool on) => Recovery = on;
    }

    private const string Dvr = @"System\GameConfigStore";
    private const string Toast = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications";

    public static async Task RunAsync(string work, Action<string, bool> check, List<string> log)
    {
        var root = @"Software\PCOptimizerSelfTest-" + Guid.NewGuid().ToString("N");
        var store = new RegistryStore(root);
        var journal = Path.Combine(work, "gamemode", "session.json");
        object? Value(string path, string name)
        {
            using var key = store.Open(Hive.CurrentUser, path);
            return key?.GetValue(name);
        }
        void Set(string path, string name, int value)
        {
            using var key = store.Create(Hive.CurrentUser, path);
            key.SetValue(name, value, RegistryValueKind.DWord);
        }
        try
        {
            Set(Dvr, "GameDVR_Enabled", 1);   // the other values start out absent
            var fake = new Fake();
            var engine = new GameModeEngine(fake, store, journal);
            var options = new GameModeSettings { AppsToClose = { @"C:\a\closes.exe", @"C:\a\stays.exe", @"C:\a\off.exe" } };
            bool Original() => fake.Plan == Fake.Balanced && fake.Gpu == 5 && Value(Dvr, "GameDVR_Enabled") is 1 &&
                               Value(Toast, "ToastEnabled") == null && !engine.HasSession && !fake.Recovery;

            var on = engine.Activate(options);
            check("activation applies every step", on.Ok && fake.Plan == Fake.Ultimate && fake.Gpu == 1 &&
                  Value(Dvr, "GameDVR_Enabled") is 0 && Value(Toast, "ToastEnabled") is 0 && fake.Recovery);
            check("only apps that exit are recorded",
                  engine.Load()!.ClosedApps.SequenceEqual(new[] { @"C:\a\closes.exe" }));
            check("a second activation cannot overwrite the originals", !engine.Activate(options).Ok);
            check("a live owner's session is not recovered", engine.Recover() == null && engine.HasSession);
            var off = engine.Restore();
            check("restore returns every original value",
                  off.Ok && Original() && fake.Launched.SequenceEqual(new[] { @"C:\a\closes.exe" }));

            engine.Activate(options);
            fake.Plan = Fake.Other;
            Set(Dvr, "GameDVR_Enabled", 7);
            engine.Restore();
            check("changes you make during a session are kept",
                  fake.Plan == Fake.Other && Value(Dvr, "GameDVR_Enabled") is 7 && fake.Gpu == 5 && !engine.HasSession);
            fake.Plan = Fake.Balanced;
            Set(Dvr, "GameDVR_Enabled", 1);

            engine.Activate(options);
            fake.GpuFails = true;
            var partial = engine.Restore();
            check("a failed item stays recorded for another try", !partial.Ok && engine.HasSession &&
                  engine.Load()!.GpuChanged && engine.Load()!.Registry.Count == 0 && fake.Recovery &&
                  fake.Plan == Fake.Balanced);
            fake.GpuFails = false;
            check("the retry finishes the restore", engine.Restore().Ok && Original());

            engine.Activate(options);
            var crashed = engine.Load()!;
            crashed.OwnerStarted -= 1;   // as if the owning process no longer exists
            File.WriteAllText(journal, JsonSerializer.Serialize(crashed));
            check("an abandoned session is recovered", engine.Recover() is { Ok: true } && Original());

            var blocker = Path.Combine(work, "gm-blocker");
            File.WriteAllText(blocker, "x");
            var threw = false;
            try { new GameModeEngine(fake, store, Path.Combine(blocker, "s.json")).Activate(options); }
            catch (IOException) { threw = true; }
            check("no record means no change", threw && fake.Plan == Fake.Balanced && fake.Gpu == 5);

            Guarded(work, fake, store, options, Original, check);

            await WatchdogAsync(engine, options, root, journal, () => Value(Dvr, "GameDVR_Enabled") is 1, check);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }

        Detection(work, check);
        Watcher(check);
        RealReadings(check, log);
    }

    /// <summary>With a protected folder, as the app uses it. "Admin" is the same engine told it is elevated.</summary>
    private static void Guarded(string work, Fake fake, RegistryStore store, GameModeSettings options,
        Func<bool> original, Action<string, bool> check)
    {
        var journal = Path.Combine(work, "gamemode-guarded", "session.json");
        var folder = SecurityTests.Stand(Path.Combine(work, "gamemode-vault"));
        var saved = folder.UserFile("gamemode-session");
        var admin = new GameModeEngine(fake, store, journal, folder, true);
        var normal = new GameModeEngine(fake, store, journal, folder, false);
        bool Restored() => original() && !File.Exists(journal);
        void Tamper()
        {
            var copy = JsonSerializer.Deserialize<GameSession>(File.ReadAllText(journal))!;
            (copy.GpuOriginal, copy.PowerOriginal) = (99, Fake.Other);
            copy.Registry.Clear();
            File.WriteAllText(journal, JsonSerializer.Serialize(copy));
        }

        normal.Activate(options);
        check("a normal session does not use the protected folder", !File.Exists(saved));
        var onlyOwn = admin.Restore();
        check("as administrator, a record only in your own folder is not used",
              !onlyOwn.Ok && admin.Recover() is { Ok: false } && fake.Gpu == 1 && fake.Plan == Fake.Ultimate &&
              admin.HasSession && onlyOwn.Lines.Single().Contains("Open WinBlazer normally"));
        check("opening normally restores it", normal.Restore().Ok && Restored());

        admin.Activate(options);
        var protectedSaved = File.Exists(saved);
        Tamper();
        check("as administrator, changes to your copy are ignored",
              protectedSaved && admin.Restore().Ok && Restored() && !File.Exists(saved));

        admin.Activate(options);
        check("an administrator session can be restored without those rights, as at sign-in",
              normal.Restore().Ok && Restored());
        fake.Gpu = 1;   // chosen by the user afterwards
        check("a finished session is not restored again",
              !admin.HasSession && admin.Restore().Ok && fake.Gpu == 1);
        fake.Gpu = 5;

        var leftover = File.ReadAllText(saved);
        normal.Activate(options);
        check("a leftover protected record is not used for a later session",
              File.ReadAllText(saved) == leftover && !admin.Restore().Ok && fake.Gpu == 1 && normal.Restore().Ok &&
              Restored());

        var distrusted = new GameModeEngine(fake, store, journal,
            new ProtectedFolder(folder.FullPath, _ => false, _ => { }), true);
        var refused = false;
        try { distrusted.Activate(options); }
        catch (IOException) { refused = true; }
        check("an untrusted protected folder means Game Mode changes nothing", refused && Restored());
    }

    /// <summary>End to end: a real watchdog process restores after its parent process is ended.</summary>
    private static async Task WatchdogAsync(GameModeEngine engine, GameModeSettings options, string root,
        string journal, Func<bool> restored, Action<string, bool> check)
    {
        ProcessStartInfo Self(params string[] args)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            return start;
        }
        using var parent = Process.Start(Self("--selftest-sleep"))!;
        Process? watchdog = null;
        try
        {
            engine.Activate(new GameModeSettings
            { UltimatePower = false, GpuMaxPerformance = false, PurgeStandby = false });
            var session = engine.Load()!;
            (session.OwnerPid, session.OwnerStarted) = (parent.Id, parent.StartTime.ToUniversalTime().Ticks);
            File.WriteAllText(journal, JsonSerializer.Serialize(session));
            watchdog = Process.Start(Self("--gamemode-watchdog", parent.Id.ToString(), journal, root))!;
            await Task.Delay(2500);
            var waited = !watchdog.HasExited && engine.HasSession;
            parent.Kill();   // our own helper process: simulates a crash
            using var limit = new CancellationTokenSource(20000);
            await watchdog.WaitForExitAsync(limit.Token);
            check("the watchdog restores after the owner is ended",
                  waited && watchdog.ExitCode == 0 && !engine.HasSession && restored());
        }
        finally
        {
            if (!parent.HasExited) parent.Kill();
            if (watchdog is { HasExited: false }) watchdog.Kill();
            watchdog?.Dispose();
        }
    }

    private static void Detection(string work, Action<string, bool> check)
    {
        const string vdf = "\"libraryfolders\" { \"0\" { \"path\"  \"C:\\\\Program Files (x86)\\\\Steam\" } " +
                           "\"1\" { \"path\"  \"D:\\\\SteamLibrary\" } }";
        string Acf(string name, string dir) => $"\"AppState\" {{ \"appid\" \"10\" \"name\" \"{name}\" \"installdir\" \"{dir}\" }}";
        var parsed = GameLibrary.ParseSteamManifest(Acf("My Game", "MyGame"), @"D:\SteamLibrary");
        check("Steam library files are read",
              GameLibrary.ParseSteamLibraries(vdf).SequenceEqual(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }) &&
              parsed?.Folder == @"D:\SteamLibrary\steamapps\common\MyGame" &&
              GameLibrary.ParseSteamManifest(Acf("Wallpaper Engine", "wallpaper_engine"), @"D:\L") == null &&
              GameLibrary.ParseSteamManifest(Acf("Bad", ".."), @"D:\L") == null);

        var manifests = Directory.CreateDirectory(Path.Combine(work, "epic")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(work, "EpicGame")).FullName;
        string Item(string name, string category) => JsonSerializer.Serialize(new
        { DisplayName = name, AppName = name, InstallLocation = install, AppCategories = new[] { category } });
        File.WriteAllText(Path.Combine(manifests, "a.item"), Item("Real", "games"));
        File.WriteAllText(Path.Combine(manifests, "b.item"), Item("Engine", "engines"));
        File.WriteAllText(Path.Combine(manifests, "c.item"), "not json");
        var epic = new List<GameInfo>();
        GameLibrary.Epic(epic, manifests);
        check("Epic manifests are read", epic.Count == 1 && epic[0].Name == "Real");

        check("broad folders are rejected", !GameLibrary.IsUsableFolder(@"C:\") &&
              !GameLibrary.IsUsableFolder(Environment.SystemDirectory) &&
              !GameLibrary.IsUsableFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) &&
              !GameLibrary.IsUsableFolder(@"\\server\share\game") && GameLibrary.IsUsableFolder(@"D:\Games\One"));

        var games = new List<GameInfo> { new("a", "A", "Steam", @"D:\Games\A") };
        games.AddRange(GameLibrary.Custom(new[] { @"E:\x\solo.exe" }));
        List<RunningGame> Match(params string[] paths) =>
            GameLibrary.Match(games, paths.Select((p, i) => new ProcessInfo(i + 1, p)).ToList());
        check("running games are matched by path",
              Match(@"D:\Games\A\bin\a.exe", @"E:\x\solo.exe").Count == 2 &&
              Match(@"D:\Games\ABC\a.exe", @"E:\x\other.exe").Count == 0 &&
              Match(@"D:\Games\A\UnityCrashHandler64.exe", @"D:\Games\A\Launcher.exe").Count == 0);

        var plans = WindowsGameSystem.ParsePlans(
            "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\r\n" +
            "Power Scheme GUID: 51d598d7-f636-40b2-8966-f5faa49b7f6f  (Ultimate Performance) *\r\n");
        check("power plan list is read", plans.Count == 2 && plans[1].Name == "Ultimate Performance");
    }

    private static void Watcher(Action<string, bool> check)
    {
        var watcher = new GameWatcher();
        var game = new[] { "g" };
        var none = Array.Empty<string>();
        WatchDecision Poll(string[] running, bool active, bool auto = true) => watcher.Poll(running, active, auto);

        check("auto-activation waits for two sightings and respects the switch",
              Poll(game, false, false) == WatchDecision.None && Poll(game, false) == WatchDecision.None &&
              Poll(game, false) == WatchDecision.Activate);
        var steady = Enumerable.Range(0, 3).All(_ => Poll(none, true) == WatchDecision.None) &&
                     Poll(game, true) == WatchDecision.None &&
                     Enumerable.Range(0, 3).All(_ => Poll(none, true) == WatchDecision.None);
        check("a brief disappearance is tolerated, a closed game turns it off",
              steady && Poll(none, true) == WatchDecision.Deactivate);

        watcher.ManualOff(game);
        var quiet = Enumerable.Range(0, 4).All(_ => Poll(game, false) == WatchDecision.None);
        Poll(none, false);
        Poll(game, false);
        check("after a manual off it waits for the game to be reopened",
              quiet && Poll(game, false) == WatchDecision.Activate);

        watcher = new GameWatcher();
        var idle = Enumerable.Range(0, 10).All(_ => Poll(none, true) == WatchDecision.None);
        Poll(game, true);
        var results = Enumerable.Range(0, 4).Select(_ => Poll(none, true, false)).ToList();
        check("a manual session follows a game started later", idle && results[^1] == WatchDecision.Deactivate);
    }

    private static void RealReadings(Action<string, bool> check, List<string> log)
    {
        var system = new WindowsGameSystem();
        var plan = system.ActivePlan();
        log.Add($"Active power plan: {plan}, ultimate: {system.IsUltimate(plan)}");
        check("the real power plan can be read", plan != Guid.Empty);
        try { log.Add($"NVIDIA global power mode (read only): {NvidiaPowerMode.Read().Value?.ToString() ?? "driver default"}"); }
        catch (IOException ex) { log.Add("NOTE NVIDIA read not available: " + ex.Message); }
        var games = GameLibrary.Scan(Array.Empty<string>());
        log.Add($"Games found: {games.Count}" + string.Concat(games.Select(g => $"\n  {g.Store}: {g.Name}")));
        check("running programs can be listed", GameLibrary.RunningProcesses().Count > 0);
    }
}
