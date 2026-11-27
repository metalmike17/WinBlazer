using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCOptimizer.Core;

public enum ScanFrequency { Off, Daily, Weekly }

public sealed class CleanupPreferences
{
    public int MinimumAgeDays { get; set; } = 7;
}

public sealed class ModuleSchedule
{
    public ScanFrequency Frequency { get; set; } = ScanFrequency.Off;
    public DateTime? LastRunUtc { get; set; }
}

public sealed class MemorySettings
{
    public bool SmartCleanerEnabled { get; set; }
    public int FreeThresholdMb { get; set; } = 1024;
    /// <summary>Full program paths chosen in the Background App Limiter.</summary>
    public List<string> LimitedApps { get; set; } = new();
}

public sealed class GameModeSettings
{
    // The one-click steps, used only when Game Mode is activated.
    public bool UltimatePower { get; set; } = true;
    public bool DisableGameDvr { get; set; } = true;
    public bool SilenceNotifications { get; set; } = true;
    public bool GpuMaxPerformance { get; set; } = true;
    public bool PurgeStandby { get; set; } = true;
    public bool RelaunchClosedApps { get; set; } = true;
    public bool AutoActivate { get; set; }
    public List<string> AppsToClose { get; set; } = new();
    public List<string> CustomPaths { get; set; } = new();
    public List<string> IgnoredGames { get; set; } = new();

    public GameModeSettings Copy()
    {
        var copy = (GameModeSettings)MemberwiseClone();
        copy.AppsToClose = new(AppsToClose);
        copy.CustomPaths = new(CustomPaths);
        copy.IgnoredGames = new(IgnoredGames);
        return copy;
    }
}

public sealed class AppSettings
{
    public GameModeSettings GameMode { get; set; } = new();
    public MemorySettings Memory { get; set; } = new();
    public HashSet<string> HiddenModules { get; set; } = new();
    public Dictionary<string, ModuleSchedule> Schedules { get; set; } = new();
    public CleanupPreferences Cleanup { get; set; } = new();
    public bool MinimizeToTray { get; set; } = true;
    public bool NotifyAfterScheduledScan { get; set; } = true;
    public bool NotifyAfterCleanup { get; set; } = true;
    public bool LightTheme { get; set; }
    /// <summary>Set when the first-run wizard is finished.</summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>Repairs values that may be missing or out of range in a hand-edited file.</summary>
    public AppSettings Normalize()
    {
        HiddenModules ??= new();
        Schedules ??= new();
        foreach (var key in Schedules.Where(p => p.Value is null).Select(p => p.Key).ToList())
            Schedules.Remove(key);
        GameMode ??= new();
        GameMode.AppsToClose ??= new();
        GameMode.CustomPaths ??= new();
        GameMode.IgnoredGames ??= new();
        foreach (var list in new[] { GameMode.AppsToClose, GameMode.CustomPaths, GameMode.IgnoredGames })
            list.RemoveAll(string.IsNullOrWhiteSpace);
        Memory ??= new();
        Memory.LimitedApps ??= new();
        Memory.LimitedApps.RemoveAll(string.IsNullOrWhiteSpace);
        Memory.FreeThresholdMb = Math.Clamp(Memory.FreeThresholdMb, 128, 65536);
        Cleanup ??= new();
        Cleanup.MinimumAgeDays = Math.Clamp(Cleanup.MinimumAgeDays, 1, 365);
        return this;
    }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; }
    public AppSettings Current { get; private set; } = new();
    public string? LoadWarning { get; private set; }
    public event Action? Changed;

    public SettingsService(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizerNet", "settings.json");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            Current = (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                       ?? new AppSettings()).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Current = new AppSettings();
            LoadWarning = "The settings file could not be read, so defaults are in use.";
            // Keep the unreadable file so the next save does not destroy it.
            try { File.Copy(FilePath, FilePath + ".bad", overwrite: true); }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
        }
    }

    private readonly object _writeGate = new();
    private long _requested, _written;

    public void Save()
    {
        var (json, version) = Snapshot();
        Write(json, version);
        Changed?.Invoke();
    }

    /// <summary>For windows: takes the values now, writes them without blocking, then raises Changed.</summary>
    public async Task SaveAsync()
    {
        var (json, version) = Snapshot();
        await Task.Run(() => Write(json, version));
        Changed?.Invoke();
    }

    private (string Json, long Version) Snapshot() =>
        (JsonSerializer.Serialize(Current, Options), Interlocked.Increment(ref _requested));

    private void Write(string json, long version)
    {
        lock (_writeGate)
        {
            if (version < _written) return;   // a newer save has already been written
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);   // never leaves a half-written file
            _written = version;
        }
    }
}