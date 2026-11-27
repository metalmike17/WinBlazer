using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using PCOptimizer.Core;
using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer.Services.GameTools;

/// <summary>Id selects a fixed registry value; the journal can never name an arbitrary one.</summary>
public sealed record RegistryChange(string Id, int? OriginalType, byte[]? OriginalData);

public sealed class GameSession
{
    public int OwnerPid { get; set; }
    public long OwnerStarted { get; set; }
    public DateTime Started { get; set; }
    public Guid? PowerOriginal { get; set; }
    public Guid? PowerApplied { get; set; }
    public bool GpuChanged { get; set; }
    public uint? GpuOriginal { get; set; }
    public List<RegistryChange> Registry { get; set; } = new();
    public List<string> ClosedApps { get; set; } = new();

    public bool IsEmpty => PowerOriginal == null && !GpuChanged && Registry.Count == 0 && ClosedApps.Count == 0;
}

public sealed record GameModeReport(bool Ok, List<string> Lines);

/// <summary>
/// Applies and reverses Game Mode. Each original value is written to the journal before the change, so an
/// interrupted session can always be reversed. All methods lock across processes.
/// An administrator copy also saves the record in a protected folder and restores only from that one. The copy
/// in the user folder stays, so that restoring without administrator rights (as at sign-in) still works; once
/// it is gone, the session counts as finished.
/// </summary>
public sealed class GameModeEngine
{
    private sealed record Switch(string Id, string Group, string Path, string Name);

    private static readonly Switch[] Switches =
    {
        new("dvr", "dvr", @"System\GameConfigStore", "GameDVR_Enabled"),
        new("capture", "dvr", @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
        new("gamebar", "dvr", @"SOFTWARE\Microsoft\GameBar", "UseNexusForGameBarEnabled"),
        new("toast", "notify", @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled"),
        new("toast-global", "notify", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings",
            "NOC_GLOBAL_SETTING_TOASTS_ENABLED")
    };

    private readonly IGameSystem _system;
    private readonly RegistryStore _registry;
    private readonly string _mutexName;

    private readonly ProtectedFolder? _protected;   // only set for an administrator copy

    public string JournalPath { get; }

    public GameModeEngine(IGameSystem system, RegistryStore registry, string journalPath,
                          ProtectedFolder? protectedFolder = null, bool isAdmin = false)
    {
        _protected = isAdmin ? protectedFolder : null;
        (_system, _registry, JournalPath) = (system, registry, Path.GetFullPath(journalPath));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(JournalPath.ToLowerInvariant()));
        _mutexName = @"Local\PCOptimizerGameMode-" + Convert.ToHexString(hash, 0, 12);
    }

    private T Locked<T>(Func<T> work)
    {
        using var mutex = new Mutex(false, _mutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(60)))
                throw new IOException("Another Game Mode operation is still running.");
        }
        catch (AbandonedMutexException) { }   // the previous owner crashed; we now hold the lock
        try { return work(); }
        finally { mutex.ReleaseMutex(); }
    }

    public bool HasSession => File.Exists(JournalPath);

    /// <summary>A damaged journal throws; it is never silently discarded.</summary>
    public GameSession? Load()
    {
        if (!File.Exists(JournalPath)) return null;
        var copy = Read(JournalPath);
        if (_protected == null) return copy;
        var path = _protected.UserFile("gamemode-session");
        if (!_protected.IsUsable || !File.Exists(path)) return null;
        var saved = Read(path);
        // A leftover protected record must not be used for a different, later session.
        return saved.OwnerPid == copy.OwnerPid && saved.OwnerStarted == copy.OwnerStarted &&
               saved.Started == copy.Started ? saved : null;
    }

    private static GameSession Read(string path) =>
        JsonSerializer.Deserialize<GameSession>(File.ReadAllText(path))
        ?? throw new JsonException("The Game Mode record is empty.");

    private void Save(GameSession session)
    {
        if (_protected != null)
        {
            _protected.PrepareForWriting();
            Write(_protected.UserFile("gamemode-session"), session);
        }
        Write(JournalPath, session);
    }

    private static void Write(string path, GameSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create))
        {
            JsonSerializer.Serialize(stream, session, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(flushToDisk: true);   // must survive a power cut
        }
        File.Move(temp, path, overwrite: true);
    }

    private static GameModeReport NotTrusted() => new(false, new()
    {
        "This Game Mode session has no record that can be trusted with administrator rights, so nothing was " +
        "changed. Open WinBlazer normally to restore it."
    });

    private static bool OwnerAlive(GameSession session)
    {
        try
        {
            using var owner = Process.GetProcessById(session.OwnerPid);
            return owner.StartTime.ToUniversalTime().Ticks == session.OwnerStarted;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception) { return false; }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SecurityException or InvalidOperationException or COMException
        or System.ComponentModel.Win32Exception;

    public GameModeReport Activate(GameModeSettings options) => Locked(() =>
    {
        if (HasSession)
            return new GameModeReport(false, new()
                { "An earlier Game Mode session has not been fully restored. Restore it first." });

        using var self = Process.GetCurrentProcess();
        var session = new GameSession
        {
            OwnerPid = self.Id, OwnerStarted = self.StartTime.ToUniversalTime().Ticks, Started = DateTime.Now
        };
        Save(session);                      // if this fails, nothing is changed
        var lines = new List<string>();
        try { _system.SetSignInRecovery(true); }
        catch (Exception ex) when (IsExpected(ex))
        { lines.Add("Warning: restore at next sign-in could not be set up. " + ex.Message); }

        void Step(string name, Action work)
        {
            try { work(); }
            catch (Exception ex) when (IsExpected(ex)) { lines.Add($"{name}: failed. {ex.Message}"); }
        }

        if (options.UltimatePower) Step("Power plan", () =>
        {
            var original = _system.ActivePlan();
            if (_system.IsUltimate(original)) { lines.Add("Power plan: Ultimate Performance was already active."); return; }
            var target = _system.UltimatePlan();
            (session.PowerOriginal, session.PowerApplied) = (original, target);
            Save(session);
            _system.SetPlan(target);
            lines.Add("Power plan: switched to Ultimate Performance.");
        });

        foreach (var (group, on, label) in new[]
                 {
                     ("dvr", options.DisableGameDvr, "Game DVR and Game Bar capture"),
                     ("notify", options.SilenceNotifications, "Notification pop-ups")
                 })
        {
            if (!on) continue;
            Step(label, () =>
            {
                foreach (var item in Switches.Where(s => s.Group == group))
                {
                    using var key = _registry.Create(Hive.CurrentUser, item.Path);
                    var original = RegistryStore.ReadValue(key, item.Name);
                    if (original is { Type: 4, Data.Length: 4 } && BitConverter.ToInt32(original.Data) == 0) continue;
                    session.Registry.Add(new RegistryChange(item.Id, original?.Type, original?.Data));
                    Save(session);
                    key.SetValue(item.Name, 0, RegistryValueKind.DWord);
                }
                lines.Add(label + (group == "dvr" ? ": turned off (a running game may need a restart to notice)."
                    : ": setting turned off (Windows may not apply it until later)."));
            });
        }

        if (options.GpuMaxPerformance) Step("GPU", () =>
        {
            var original = _system.ReadGpu();
            if (original.Value == NvidiaPowerMode.PreferMaximum)
            { lines.Add("GPU: already set to prefer maximum performance."); return; }
            (session.GpuChanged, session.GpuOriginal) = (true, original.Value);
            Save(session);
            _system.WriteGpu(NvidiaPowerMode.PreferMaximum);
            if (_system.ReadGpu().Value != NvidiaPowerMode.PreferMaximum)
                throw new IOException("The driver did not keep the new setting.");
            lines.Add("GPU: NVIDIA global power mode set to prefer maximum performance.");
        });

        if (options.AppsToClose.Count > 0) Step("Background apps", () =>
        {
            foreach (var app in options.AppsToClose.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(app);
                if (!_system.IsRunning(app)) continue;
                session.ClosedApps.Add(app);
                Save(session);
                if (_system.CloseApp(app)) { lines.Add($"{name}: closed."); continue; }
                session.ClosedApps.Remove(app);
                Save(session);
                lines.Add($"{name}: stayed open (it was not force-closed).");
            }
            if (!options.RelaunchClosedApps) { session.ClosedApps.Clear(); Save(session); }
        });

        if (options.PurgeStandby) Step("Standby memory", () =>
        {
            var result = _system.PurgeStandby();
            lines.Add(result.Ok ? "Standby memory: purged." : "Standby memory: skipped. " + result.Message);
        });

        return new GameModeReport(true, lines);
    });

    /// <summary>Restores only if no running instance owns the session (used after a crash).</summary>
    public GameModeReport? Recover() => Locked(() =>
    {
        var session = Load();
        if (session == null) return HasSession ? NotTrusted() : null;
        return OwnerAlive(session) ? null : RestoreSession(session);
    });

    public bool OwnedByAnotherInstance()
    {
        var session = Load();
        return session != null && session.OwnerPid != Environment.ProcessId && OwnerAlive(session);
    }

    /// <summary>Restores this instance's session, or an abandoned one.</summary>
    public GameModeReport Restore() => Locked(() =>
    {
        var session = Load();
        if (session == null)
            return HasSession ? NotTrusted() : new GameModeReport(true, new() { "There is nothing to restore." });
        if (session.OwnerPid != Environment.ProcessId && OwnerAlive(session))
            return new GameModeReport(false, new() { "Game Mode belongs to another open WinBlazer window." });
        return RestoreSession(session);
    });

    /// <summary>Each item is removed from the journal once restored; failures stay for another try.</summary>
    private GameModeReport RestoreSession(GameSession session)
    {
        var lines = new List<string>();
        var ok = true;
        void Step(string name, Action work)
        {
            try { work(); Save(session); }
            catch (Exception ex) when (IsExpected(ex)) { ok = false; lines.Add($"{name}: not restored. {ex.Message}"); }
        }

        if (session.PowerOriginal is { } plan) Step("Power plan", () =>
        {
            var current = _system.ActivePlan();
            if (current == session.PowerApplied) { _system.SetPlan(plan); lines.Add("Power plan: restored."); }
            else if (current != plan) lines.Add("Power plan: you changed it during the session, so it was left alone.");
            (session.PowerOriginal, session.PowerApplied) = (null, null);
        });

        foreach (var change in session.Registry.ToList())
        {
            var item = Switches.FirstOrDefault(s => s.Id == change.Id);
            if (item == null) { session.Registry.Remove(change); continue; }
            Step(item.Name, () =>
            {
                using var key = _registry.Create(Hive.CurrentUser, item.Path);
                var now = RegistryStore.ReadValue(key, item.Name);
                // Only undo our own 0; anything else was changed by someone else.
                if (now is { Type: 4, Data.Length: 4 } && BitConverter.ToInt32(now.Data) == 0)
                {
                    if (change is { OriginalType: { } type, OriginalData: { } data })
                        RegistryStore.WriteValue(key, new RawValue(item.Name, type, data));
                    else key.DeleteValue(item.Name, throwOnMissingValue: false);
                }
                session.Registry.Remove(change);
            });
        }

        if (session.GpuChanged) Step("GPU", () =>
        {
            if (_system.ReadGpu().Value == NvidiaPowerMode.PreferMaximum)
            { _system.WriteGpu(session.GpuOriginal); lines.Add("GPU: power mode restored."); }
            else lines.Add("GPU: power mode changed during the session, so it was left alone.");
            (session.GpuChanged, session.GpuOriginal) = (false, null);
        });

        foreach (var app in session.ClosedApps.ToList())
        {
            var name = Path.GetFileNameWithoutExtension(app);
            try
            {
                if (!_system.IsRunning(app)) { _system.Launch(app); lines.Add($"{name}: reopened."); }
            }
            catch (Exception ex) when (IsExpected(ex)) { lines.Add($"{name}: could not be reopened. {ex.Message}"); }
            // Not retried: reopening an app much later would be unexpected.
            session.ClosedApps.Remove(app);
            Step(name, () => { });
        }

        if (session.IsEmpty)
        {
            File.Delete(JournalPath);
            if (_protected is { IsUsable: true }) File.Delete(_protected.UserFile("gamemode-session"));
            try { _system.SetSignInRecovery(false); }
            catch (Exception ex) when (IsExpected(ex)) { lines.Add("Sign-in restore entry remains: " + ex.Message); }
            lines.Add("Everything was restored.");
        }
        else
        {
            try { _system.SetSignInRecovery(true); } catch (Exception ex) when (IsExpected(ex)) { }
            lines.Add("Some items still need restoring. Try again, as administrator if needed.");
        }
        return new GameModeReport(ok && session.IsEmpty, lines);
    }
}
