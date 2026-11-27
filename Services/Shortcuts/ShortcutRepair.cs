using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace PCOptimizer.Services.ShortcutTools;

/// <summary>AfterHash is null for a removed shortcut.</summary>
public sealed record ShortcutChange(string Path, string Backup, string BeforeHash, string? AfterHash);
public sealed record ShortcutBatch(DateTime Time, string Action, List<ShortcutChange> Items);
public sealed record ShortcutReport(int Changed, List<string> Problems);

/// <summary>Every shortcut is copied and the copy verified before it is changed or removed.</summary>
public sealed class ShortcutRepair
{
    private readonly IReadOnlyList<ShortcutRoot> _roots;

    private readonly PCOptimizer.Core.ProtectedFolder? _protected;

    public string BackupFolder { get; }

    /// <summary>Where one set of backups and its history are kept.</summary>
    private sealed record Area(string Folder, string Manifest, bool Shared);

    private Area UserArea => new(BackupFolder, Path.Combine(BackupFolder, "history.json"), false);

    private Area SharedArea => new(_protected!.FullPath,
        Path.Combine(_protected.FullPath, "shortcut-history.json"), true);

    /// <summary>
    /// The app passes a protected folder, used for shortcuts shared by all users. Without one, every backup goes
    /// in backupFolder (tests).
    /// </summary>
    public ShortcutRepair(IReadOnlyList<ShortcutRoot> roots, string backupFolder,
                          PCOptimizer.Core.ProtectedFolder? protectedFolder = null) =>
        (_roots, BackupFolder, _protected) = (roots, Path.GetFullPath(backupFolder), protectedFolder);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Only .lnk files inside the scanned folders may be touched.</summary>
    private ShortcutRoot? RootOf(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? _roots.FirstOrDefault(r => full.StartsWith(Path.TrimEndingDirectorySeparator(r.Path) + "\\",
                                                         StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>The history that Undo would use: the protected one when running as administrator.</summary>
    public List<ShortcutBatch> History(bool isAdmin = false) =>
        Read(_protected != null && isAdmin ? SharedArea : UserArea);

    private List<ShortcutBatch> Read(Area area) =>
        (area.Shared && !_protected!.IsUsable) || !File.Exists(area.Manifest)
            ? new()
            : JsonSerializer.Deserialize<List<ShortcutBatch>>(File.ReadAllText(area.Manifest))
              ?? throw new JsonException("The backup history is empty.");

    private void Prepare(Area area)
    {
        if (area.Shared) _protected!.PrepareForWriting();
        else Directory.CreateDirectory(area.Folder);
    }

    private void Store(Area area, List<ShortcutBatch> history)
    {
        Prepare(area);
        File.WriteAllText(area.Manifest + ".tmp",
            JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(area.Manifest + ".tmp", area.Manifest, overwrite: true);
    }

    /// <summary>
    /// Re-targets a moved file, or repairs the start folder or arguments. A null target keeps the current one.
    /// </summary>
    public ShortcutReport Repair(IReadOnlyList<(ShortcutFinding Finding, string? NewTarget, string? NewArguments)> repairs,
                                 bool isAdmin) =>
        Change("Repair", repairs.Select(r => (r.Finding, (Action)(() =>
        {
            var link = r.Finding.Link;
            if (link.Advertised) throw new InvalidOperationException("Windows Installer shortcuts cannot be edited.");
            var oldTarget = Environment.ExpandEnvironmentVariables(link.Target);
            var target = r.NewTarget ?? oldTarget;
            if (r.NewTarget != null && !File.Exists(r.NewTarget) && !Directory.Exists(r.NewTarget))
                throw new IOException("The new target no longer exists.");
            string? directory = null, icon = null;
            var current = Environment.ExpandEnvironmentVariables(link.WorkingDirectory.Trim().Trim('"'));
            var follows = r.NewTarget != null && current.TrimEnd('\\').Equals(
                Path.GetDirectoryName(oldTarget), StringComparison.OrdinalIgnoreCase);
            if (current.Length > 0 && (follows || ShortcutScanner.Exists(current) == false) &&
                (File.Exists(target) || Directory.Exists(target)))
                directory = Path.GetDirectoryName(target) ?? "";
            if (r.NewTarget != null && Environment.ExpandEnvironmentVariables(link.IconPath)
                    .Equals(oldTarget, StringComparison.OrdinalIgnoreCase)) icon = r.NewTarget;
            if (r.NewTarget == null && directory == null && r.NewArguments == null)
                throw new InvalidOperationException("There is nothing to repair automatically.");
            ShellLink.Update(r.Finding.Path, r.NewTarget, r.NewArguments, directory, icon);
        }))).ToList(), isAdmin, removing: false);

    public ShortcutReport Remove(IReadOnlyList<ShortcutFinding> findings, bool isAdmin) =>
        Change("Remove", findings.Select(f => (f, (Action)(() => File.Delete(f.Path)))).ToList(), isAdmin, removing: true);

    private ShortcutReport Change(string action, List<(ShortcutFinding Finding, Action Work)> items, bool isAdmin,
                                  bool removing)
    {
        var problems = new List<string>();
        // A damaged history stops the change.
        var ownHistory = Read(UserArea);
        var sharedHistory = _protected == null ? new List<ShortcutBatch>() : Read(SharedArea);
        var time = DateTime.Now;
        var ownBatch = new ShortcutBatch(time, action, new());
        var sharedBatch = new ShortcutBatch(time, action, new());
        var changed = 0;
        foreach (var (finding, work) in items)
        {
            try
            {
                var root = RootOf(finding.Path) ?? throw new InvalidOperationException("Outside the scanned folders.");
                if (root.NeedsAdmin && !isAdmin) throw new InvalidOperationException("Needs administrator rights.");
                // The shortcut must still be the one that was shown.
                if (!File.Exists(finding.Path) || ShellLink.Read(finding.Path) != finding.Link)
                    throw new InvalidOperationException("Changed since the scan. Scan again.");
                if (removing && finding.Has(ProblemKind.MissingTarget) &&
                    ShortcutScanner.Exists(Environment.ExpandEnvironmentVariables(finding.Link.Target)) != false)
                    throw new InvalidOperationException("The target can no longer be confirmed missing. Scan again.");

                var shared = _protected != null && root.NeedsAdmin;
                var area = shared ? SharedArea : UserArea;
                var history = shared ? sharedHistory : ownHistory;
                var batch = shared ? sharedBatch : ownBatch;
                Prepare(area);
                var backup = Path.Combine(area.Folder, Guid.NewGuid().ToString("N") + ".lnk");
                File.Copy(finding.Path, backup);
                var before = Hash(finding.Path);
                if (Hash(backup) != before) throw new IOException("The backup copy did not match.");

                var entry = new ShortcutChange(finding.Path, Path.GetFileName(backup), before, null);
                batch.Items.Add(entry);
                if (!history.Contains(batch)) history.Add(batch);
                Store(area, history);   // recorded before the change
                try { work(); }
                catch
                {
                    if (File.Exists(finding.Path) && Hash(finding.Path) == before)
                    {
                        batch.Items.Remove(entry);
                        if (batch.Items.Count == 0) history.Remove(batch);
                        Store(area, history);
                    }
                    throw;
                }
                if (!removing)
                {
                    batch.Items[batch.Items.IndexOf(entry)] = entry with { AfterHash = Hash(finding.Path) };
                    Store(area, history);
                }
                changed++;
            }
            catch (Exception ex) when (IsExpected(ex)) { problems.Add($"{finding.Name}: {ex.Message}"); }
        }
        return new(changed, problems);
    }

    /// <summary>Reverses the newest batch. Newer edits and newly created files are never overwritten.</summary>
    public ShortcutReport UndoLast(bool isAdmin)
    {
        // As administrator, only the protected history is used: the other one can be altered by any program.
        var area = _protected != null && isAdmin ? SharedArea : UserArea;
        var history = Read(area);
        if (history.Count == 0)
        {
            var elsewhere = _protected != null && Read(isAdmin ? UserArea : SharedArea).Count > 0;
            return new(0, new() { !elsewhere ? "There is nothing to undo."
                : isAdmin ? "To undo changes to your own shortcuts, open WinBlazer normally, not as administrator."
                : "To undo changes to shortcuts shared by all users, restart as administrator." });
        }
        var batch = history[^1];
        var problems = new List<string>();
        var remaining = new List<ShortcutChange>();
        var changed = 0;
        foreach (var item in batch.Items)
        {
            var name = Path.GetFileNameWithoutExtension(item.Path);
            try
            {
                var root = RootOf(item.Path) ?? throw new InvalidDataException("The saved path is outside the scanned folders.");
                if (item.Backup != Path.GetFileName(item.Backup)) throw new InvalidDataException("Invalid backup name.");
                if (_protected != null && root.NeedsAdmin != area.Shared)
                {
                    // Dropped from the history, so that it cannot block older entries.
                    problems.Add($"{name}: this history is not trusted for that shortcut, so it was not restored. " +
                                 $"Backup copies are in {area.Folder}.");
                    continue;
                }
                if (root.NeedsAdmin && !isAdmin)
                {
                    problems.Add($"{name}: needs administrator rights.");
                    remaining.Add(item);
                    continue;
                }
                var backup = Path.Combine(area.Folder, item.Backup);
                if (Hash(backup) != item.BeforeHash) throw new InvalidDataException("The backup was altered.");
                if (File.Exists(item.Path))
                {
                    var now = Hash(item.Path);
                    if (now == item.BeforeHash) continue;   // already as it was
                    if (item.AfterHash == null || now != item.AfterHash)
                    { problems.Add($"{name}: a different file is there now, so it was left alone."); continue; }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(item.Path)!);
                File.Copy(backup, item.Path, overwrite: true);
                changed++;
            }
            catch (Exception ex) when (IsExpected(ex)) { problems.Add($"{name}: {ex.Message}"); remaining.Add(item); }
        }
        history.RemoveAt(history.Count - 1);
        if (remaining.Count > 0) history.Add(batch with { Items = remaining });
        Store(area, history);
        return new(changed, problems);
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or COMException
        or InvalidOperationException or InvalidDataException or ArgumentException;
}
