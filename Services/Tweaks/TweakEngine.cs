using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace PCOptimizer.Services.Tweaks;

public sealed record TweakRecord(string Id, DateTime Applied, string Original);
public sealed record TweakReport(int Changed, List<string> Problems);

/// <summary>
/// Saves each original before changing anything. Records are kept in the order applied and removed only
/// after a successful restore.
/// </summary>
public sealed class TweakEngine
{
    private readonly TweakContext _context;
    // Shared by every engine: they all use the same saved-originals file.
    private static readonly object _gate = new();

    public IReadOnlyList<Tweak> Tweaks { get; }
    public string JournalPath { get; }

    private readonly PCOptimizer.Core.ProtectedFolder? _protected;

    /// <summary>
    /// The app passes a protected folder: originals of administrator tweaks are kept there, and any found in the
    /// user's own file are not trusted. Without one, everything goes in journalPath (tests).
    /// </summary>
    public TweakEngine(IReadOnlyList<Tweak> tweaks, TweakContext context, string journalPath,
                       PCOptimizer.Core.ProtectedFolder? protectedFolder = null) =>
        (Tweaks, _context, JournalPath, _protected) = (tweaks, context, journalPath, protectedFolder);

    private string? AdminJournal => _protected == null ? null : Path.Combine(_protected.FullPath, "tweaks-journal.json");

    private bool IsAdminRecord(TweakRecord record) => Tweaks.Any(t => t.Id == record.Id && t.NeedsAdmin);

    /// <summary>A damaged file throws; it is never silently overwritten.</summary>
    private static List<TweakRecord> ReadJournal(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<List<TweakRecord>>(File.ReadAllText(path))
          ?? throw new JsonException("The tweak record is empty.")
        : new();

    private List<TweakRecord> AdminRecords() =>
        _protected is { IsUsable: true } ? ReadJournal(AdminJournal!).Where(IsAdminRecord).ToList() : new();

    /// <summary>Oldest first.</summary>
    public List<TweakRecord> Records() => _protected == null
        ? ReadJournal(JournalPath)
        : ReadJournal(JournalPath).Where(r => !IsAdminRecord(r)).Concat(AdminRecords())
            .OrderBy(r => r.Applied).ToList();

    private void Store(List<TweakRecord> records)
    {
        if (_protected == null) { Write(JournalPath, records); return; }
        var admin = records.Where(IsAdminRecord).ToList();
        // Written only when it changes, so work on your own settings never needs the protected folder.
        if (!admin.SequenceEqual(AdminRecords()))
        {
            _protected.PrepareForWriting();
            Write(AdminJournal!, admin);
        }
        // Untrusted administrator entries already in the user's file are left there, unused.
        Write(JournalPath, ReadJournal(JournalPath).Where(IsAdminRecord)
            .Concat(records.Where(r => !IsAdminRecord(r))).ToList());
    }

    /// <summary>The saved DNS addresses among these records, to show before restoring. Null if there are none.</summary>
    public string? DescribeSavedDns(IReadOnlyCollection<string>? ids)
    {
        var lines = Records().Where(r => (ids == null || ids.Contains(r.Id)) &&
                                         Tweaks.Any(t => t.Id == r.Id && t is DnsTweak))
            .Select(r => DnsTweak.Describe(r.Original)).ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static void Write(string path, List<TweakRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create))
        {
            JsonSerializer.Serialize(stream, records, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public TweakStatus Status(Tweak tweak)
    {
        try { return tweak.Read(_context); }
        catch (Exception ex) when (IsExpected(ex)) { return new(TweakState.Unavailable, "Could not read: " + ex.Message); }
    }

    public Dictionary<string, TweakStatus> AllStatuses() => Tweaks.ToDictionary(t => t.Id, Status);

    public List<Tweak> OneClickTargets(bool isAdmin) => Tweaks.Where(t =>
        t.Risk == TweakRisk.Safe && (isAdmin || !t.NeedsAdmin) &&
        Status(t).State is TweakState.Off or TweakState.Partial).ToList();

    public TweakReport Apply(IEnumerable<Tweak> tweaks, bool isAdmin)
    {
        lock (_gate)
        {
            var records = Records();
            var problems = new List<string>();
            var changed = 0;
            foreach (var tweak in tweaks)
            {
                if (tweak.NeedsAdmin && !isAdmin) { problems.Add($"{tweak.Name}: needs administrator rights."); continue; }
                var state = Status(tweak).State;
                if (state == TweakState.Applied) continue;   // nothing to do, and nothing to record
                if (state == TweakState.Unavailable) { problems.Add($"{tweak.Name}: not available."); continue; }
                try
                {
                    // An older record (changed since) is undone first, so no original is lost.
                    if (records.FirstOrDefault(r => r.Id == tweak.Id) is { } old)
                    {
                        tweak.Revert(_context, old.Original);
                        records.Remove(old);
                        Store(records);
                    }
                    var record = new TweakRecord(tweak.Id, DateTime.Now, tweak.Capture(_context));
                    records.Add(record);
                    Store(records);   // if this fails, the tweak is not applied
                    try
                    {
                        tweak.Apply(_context);
                        if (tweak.Read(_context).State != TweakState.Applied)
                            throw new IOException("Windows did not keep the change.");
                        changed++;
                    }
                    catch (Exception ex) when (IsExpected(ex))
                    {
                        problems.Add($"{tweak.Name}: {ex.Message}");
                        try
                        {
                            tweak.Revert(_context, record.Original);   // undo a half-applied change
                            records.Remove(record);
                            Store(records);
                        }
                        catch (Exception undo) when (IsExpected(undo))
                        { problems.Add($"{tweak.Name}: partly changed; use Reset to try restoring it. {undo.Message}"); }
                    }
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    problems.Add($"{tweak.Name}: {ex.Message}");
                    records = Records();
                }
            }
            return new(changed, problems);
        }
    }

    /// <summary>Restores the given tweaks, newest first. Null means every recorded tweak.</summary>
    public TweakReport Revert(IReadOnlyCollection<string>? ids, bool isAdmin)
    {
        lock (_gate)
        {
            var records = Records();
            var problems = new List<string>();
            var changed = 0;
            foreach (var record in records.Where(r => ids == null || ids.Contains(r.Id)).Reverse().ToList())
            {
                var tweak = Tweaks.FirstOrDefault(t => t.Id == record.Id);
                if (tweak == null) { problems.Add($"{record.Id}: unknown tweak; its record was kept."); continue; }
                if (tweak.NeedsAdmin && !isAdmin) { problems.Add($"{tweak.Name}: needs administrator rights."); continue; }
                try
                {
                    tweak.Revert(_context, record.Original);
                    records.Remove(record);
                    Store(records);
                    changed++;
                }
                catch (Exception ex) when (IsExpected(ex)) { problems.Add($"{tweak.Name}: {ex.Message}"); }
            }
            return new(changed, problems);
        }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SecurityException or InvalidOperationException or InvalidDataException or JsonException or COMException
        or System.ComponentModel.Win32Exception;
}
