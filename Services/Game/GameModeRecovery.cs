using System.Diagnostics;
using PCOptimizer.Core;
using PCOptimizer.Services.MemoryTools;
using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer.Services.GameTools;

/// <summary>
/// The windowless commands: --gamemode-restore (next sign-in) and --gamemode-watchdog PID (waits for
/// that process to end). Both restore only a session whose owner is gone.
/// </summary>
public static class GameModeRecovery
{
#if !NO_SELFTEST
    private const string TestKeyPrefix = @"Software\PCOptimizerSelfTest-";

    /// <summary>Used by the self-test: the real power, GPU and apps are never touched.</summary>
    private sealed class InertSystem : IGameSystem
    {
        public Guid ActivePlan() => Guid.Empty;
        public Guid UltimatePlan() => Guid.Empty;
        public bool IsUltimate(Guid plan) => false;
        public void SetPlan(Guid plan) { }
        public GpuPowerMode ReadGpu() => new(null);
        public void WriteGpu(uint? value) { }
        public bool IsRunning(string path) => true;
        public bool CloseApp(string path) => false;
        public void Launch(string path) { }
        public ActionResult PurgeStandby() => new(false, "test");
        public void SetSignInRecovery(bool on) { }
    }

#endif

    public static int Run(string[] args)
    {
        try
        {
#if NO_SELFTEST
            var watchdog = args[0] == "--gamemode-watchdog" && args.Length == 2;
#else
            var watchdog = args[0] == "--gamemode-watchdog" && args.Length is 2 or 4;
#endif
            if (!watchdog && !(args[0] == "--gamemode-restore" && args.Length == 1)) return 2;

            GameModeEngine engine;
#if !NO_SELFTEST
            if (args.Length == 4)
            {
                if (!args[3].StartsWith(TestKeyPrefix, StringComparison.Ordinal)) return 2;
                engine = new GameModeEngine(new InertSystem(), new RegistryStore(args[3]), args[2]);
            }
            else
#endif
            {
                var folder = Path.GetDirectoryName(new SettingsService().FilePath)!;
                engine = new GameModeEngine(new WindowsGameSystem(), new RegistryStore(),
                                            Path.Combine(folder, "gamemode-session.json"),
                                            ProtectedFolder.ForThisPc(), CleanupEngine.IsAdministrator());
            }

            if (watchdog)
            {
                if (!int.TryParse(args[1], out var parent)) return 2;
                try { using var process = Process.GetProcessById(parent); process.WaitForExit(); }
                catch (ArgumentException) { }   // already gone
                Thread.Sleep(1000);
            }
            var report = engine.Recover();
            return report is { Ok: false } ? 1 : 0;
        }
        catch (Exception) { return 1; }   // the journal stays for the next attempt
    }
}
