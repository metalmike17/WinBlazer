using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using PCOptimizer.Core;

namespace PCOptimizer.Services.StartupTools;

public sealed record UndoItem(string Id, string Name, bool WasEnabled);
public sealed record UndoBatch(DateTime Time, List<UndoItem> Items);
public sealed record ChangeReport(int Changed, List<string> Problems);

/// <summary>
/// Saves the previous state before changing anything. The undo file stores only entry ids and on/off,
/// and is applied only to entries that a fresh scan finds. With a protected folder, an administrator copy keeps
/// its own record there and never reads the one in the user folder.
/// </summary>
public sealed class StartupChanges
{
    private const int MaximumBatches = 20;
    private readonly StartupCatalog _catalog;

    private readonly ProtectedFolder? _protected;

    /// <summary>The record used without administrator rights.</summary>
    public string FilePath { get; }

    public StartupChanges(StartupCatalog catalog, string? filePath = null, ProtectedFolder? protectedFolder = null)
    {
        _catalog = catalog;
        _protected = protectedFolder;
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizerNet", "startup-undo.json");
    }

    private bool UsesProtected(bool isAdmin) => isAdmin && _protected != null;
    private string PathFor(bool isAdmin) => UsesProtected(isAdmin) ? _protected!.UserFile("startup-undo") : FilePath;

    public List<UndoBatch> Load(bool isAdmin = false)
    {
        if (UsesProtected(isAdmin) && !_protected!.IsUsable) return new();
        var path = PathFor(isAdmin);
        if (!File.Exists(path)) return new();
        // A damaged file throws, so it is never silently overwritten.
        return JsonSerializer.Deserialize<List<UndoBatch>>(File.ReadAllText(path)) ?? new();
    }

    private void Store(List<UndoBatch> batches, bool isAdmin)
    {
        if (UsesProtected(isAdmin)) _protected!.PrepareForWriting();
        var path = PathFor(isAdmin);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(batches, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Why Undo has nothing to do, including where the other record can be used.</summary>
    public string NothingToUndo(bool isAdmin)
    {
        var other = false;
        if (_protected != null)
        {
            try { other = Load(!isAdmin).Count > 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return !other ? "There is nothing to undo."
            : isAdmin ? "There is nothing to undo here. To undo changes made without administrator rights, open " +
                        "WinBlazer normally."
            : "There is nothing to undo here. To undo changes made as administrator, restart as administrator.";
    }

    /// <summary>What Disable All would switch off.</summary>
    public static List<StartupEntry> BulkTargets(IEnumerable<StartupEntry> entries, bool isAdmin) =>
        entries.Where(e => e.Enabled && e.CanChange(isAdmin) && e.BulkSkipReason == null).ToList();

    public ChangeReport Apply(IReadOnlyList<StartupEntry> entries, bool enabled, bool isAdmin)
    {
        var problems = new List<string>();
        var current = _catalog.Scan().ToDictionary(e => e.Id);
        var targets = new List<StartupEntry>();
        foreach (var wanted in entries)
        {
            if (!current.TryGetValue(wanted.Id, out var now)) problems.Add($"{wanted.Name}: no longer exists.");
            else if (!now.CanChange(isAdmin)) problems.Add($"{now.Name}: {now.LockReason ?? "needs administrator rights."}");
            else if (now.Enabled != enabled) targets.Add(now);
        }
        if (targets.Count == 0) return new(0, problems);

        var history = Load(isAdmin);
        var batch = new UndoBatch(DateTime.Now, targets.Select(t => new UndoItem(t.Id, t.Name, t.Enabled)).ToList());
        history.Add(batch);
        if (history.Count > MaximumBatches) history.RemoveRange(0, history.Count - MaximumBatches);
        Store(history, isAdmin);   // if this fails, nothing is changed

        var done = new HashSet<string>();
        foreach (var target in targets)
        {
            try { _catalog.SetEnabled(target, enabled); done.Add(target.Id); }
            catch (Exception ex) when (IsExpected(ex)) { problems.Add($"{target.Name}: {ex.Message}"); }
        }
        // Keep only real changes in the undo record.
        batch.Items.RemoveAll(i => !done.Contains(i.Id));
        if (batch.Items.Count == 0) history.Remove(batch);
        Store(history, isAdmin);
        return new(done.Count, problems);
    }

    /// <summary>Reverses the most recent change. Items that fail stay in the record to try again.</summary>
    public ChangeReport UndoLast(bool isAdmin)
    {
        var history = Load(isAdmin);
        if (history.Count == 0) return new(0, new() { NothingToUndo(isAdmin) });
        var batch = history[^1];
        var current = _catalog.Scan().ToDictionary(e => e.Id);
        var problems = new List<string>();
        var remaining = new List<UndoItem>();
        var changed = 0;
        foreach (var item in batch.Items)
        {
            if (!current.TryGetValue(item.Id, out var entry)) { problems.Add($"{item.Name}: no longer exists."); continue; }
            if (entry.Enabled == item.WasEnabled) continue;
            if (_protected != null && !isAdmin && entry.NeedsAdmin && entry.LockReason == null)
            {
                // The app never records such a change here, so it is dropped rather than left to block Undo.
                problems.Add($"{entry.Name}: this record is not trusted for entries that need administrator " +
                             "rights, so it was not restored. You can use its switch as administrator.");
                continue;
            }
            if (!entry.CanChange(isAdmin))
            {
                problems.Add($"{item.Name}: {entry.LockReason ?? "needs administrator rights."}");
                remaining.Add(item);
                continue;
            }
            try { _catalog.SetEnabled(entry, item.WasEnabled); changed++; }
            catch (Exception ex) when (IsExpected(ex)) { problems.Add($"{item.Name}: {ex.Message}"); remaining.Add(item); }
        }
        history.RemoveAt(history.Count - 1);
        if (remaining.Count > 0) history.Add(batch with { Items = remaining });
        Store(history, isAdmin);
        return new(changed, problems);
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SecurityException or COMException or InvalidOperationException;
}
