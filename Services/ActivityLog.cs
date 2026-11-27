using System.Text.Json;

namespace PCOptimizer.Services;

/// <summary>BytesFreed is null when an action does not free disk space or it was not measured.</summary>
public sealed record Activity(DateTime Time, string Category, string Action, bool Success, long? BytesFreed,
                              string Details);

/// <summary>
/// The History file: one JSON line per action. Recording never throws, so it cannot break the action it
/// describes. Shared safely with the automatic-clean process.
/// </summary>
public static class ActivityLog
{
    /// <summary>The category that shows a notification when it finishes.</summary>
    public const string Cleanup = "Cleanup";
    private const int Keep = 2000;
    private const long TrimAbove = 1_500_000;

    private static string? _path;
    public static string? LastError { get; private set; }
    public static event Action<Activity>? Added;

    /// <summary>Nothing is written until this is called.</summary>
    public static void Configure(string dataFolder) => _path = Path.Combine(dataFolder, "history.jsonl");

    private static T Locked<T>(Func<T> work)
    {
        using var mutex = new Mutex(false, @"Local\PCOptimizerNet.History");
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(3000); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new IOException("The history file is busy.");
            return work();
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    private static readonly object QueueGate = new();
    private static Task _queue = Task.CompletedTask;

    /// <summary>Writes on the calling thread. Windows should use Post, so the disk is never waited on.</summary>
    public static void Record(string category, string action, bool success, long? bytesFreed = null, string details = "") =>
        Write(_path, Entry(category, action, success, bytesFreed, details));

    /// <summary>Like Record, but writes in the background, in the order called.</summary>
    public static void Post(string category, string action, bool success, long? bytesFreed = null, string details = "")
    {
        var entry = Entry(category, action, success, bytesFreed, details);
        var path = _path;
        lock (QueueGate)
            _queue = _queue.ContinueWith(_ => Write(path, entry), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Waits for posted entries to be written.</summary>
    public static void Flush()
    {
        Task pending;
        lock (QueueGate) pending = _queue;
        pending.Wait(5000);
    }

    private static Activity Entry(string category, string action, bool success, long? bytesFreed, string details)
    {
        if (details.Length > 2000) details = details[..2000] + "...";
        return new Activity(DateTime.Now, category, action, success, bytesFreed, details);
    }

    private static void Write(string? target, Activity entry)
    {
        try
        {
            if (target is { } path)
                Locked(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
                    if (new FileInfo(path).Length > TrimAbove)
                    {
                        File.WriteAllLines(path + ".tmp", File.ReadAllLines(path).TakeLast(Keep));
                        File.Move(path + ".tmp", path, true);
                    }
                    return true;
                });
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LastError = ex.Message; }
        try { Added?.Invoke(entry); }
        catch (Exception ex) when (ex is InvalidOperationException) { LastError = ex.Message; }
    }

    /// <summary>Newest first. Damaged lines are skipped.</summary>
    public static List<Activity> Read()
    {
        Flush();
        var result = new List<Activity>();
        if (_path is not { } path || !File.Exists(path)) return result;
        foreach (var line in Locked(() => File.ReadAllLines(path)))
        {
            try
            {
                if (JsonSerializer.Deserialize<Activity>(line) is { Category: not null, Action: not null } entry)
                    result.Add(entry);
            }
            catch (JsonException) { }
        }
        result.Reverse();
        return result;
    }

    public static void Clear()
    {
        Flush();
        if (_path is { } path) Locked(() => { File.Delete(path); return true; });
    }
}
