using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer.Services.StartupTools;

public enum StartupSource { RunKey, StartupFolder, ScheduledTask }

public sealed record StartupEntry
{
    /// <summary>Stable key used to find the same entry again.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required StartupSource Source { get; init; }
    public required string Location { get; init; }
    public required string Command { get; init; }
    public string? ProgramPath { get; init; }
    public string Publisher { get; init; } = "Unknown";
    public bool Enabled { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool Hidden { get; init; }
    /// <summary>Set when this app will not change the entry.</summary>
    public string? LockReason { get; init; }
    /// <summary>Set when Disable All leaves the entry alone.</summary>
    public string? BulkSkipReason { get; init; }
    public string Impact { get; init; } = "Not measured";

    // Where the on/off switch lives for Run keys and Startup folders.
    public Hive ApprovedHive { get; init; }
    public string? ApprovedPath { get; init; }
    public string? ApprovedValue { get; init; }
    public string? TaskPath { get; init; }

    public bool CanChange(bool isAdmin) => LockReason == null && (isAdmin || !NeedsAdmin);
}

public interface ITaskSource
{
    /// <summary>Tasks that start at boot or sign-in, including hidden ones.</summary>
    IReadOnlyList<StartupEntry> Read();
    void SetEnabled(string taskPath, bool enabled);
}
