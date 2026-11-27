using System.Security.Cryptography;
using PCOptimizer.Services.ShortcutTools;

namespace PCOptimizer;

/// <summary>Changes only temporary fixture shortcuts. Real shortcuts are read and verified unchanged.</summary>
public static class ShortcutTests
{
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static void Run(string work, Action<string, bool> check, List<string> log)
    {
        var home = Directory.CreateDirectory(Path.Combine(work, "shortcut-fixture")).FullName;
        var links = Directory.CreateDirectory(Path.Combine(home, "Desktop")).FullName;
        var apps = Directory.CreateDirectory(Path.Combine(home, "Apps")).FullName;
        string Make(params string[] parts)
        {
            var path = Path.Combine(parts.Prepend(apps).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
            return path;
        }
        string Link(string name, string target, string arguments = "", string directory = "")
        {
            var path = Path.Combine(links, name + ".lnk");
            ShellLink.Create(path, target, arguments, directory);
            return path;
        }
        string Old(params string[] parts) => Path.Combine(parts.Prepend(apps).ToArray());

        var good = Make("Good", "good.exe");
        var moved = Make("NewPlace", "Tool", "tool.exe");
        Make("A", "Twin", "dup.exe");
        Make("B", "Twin", "dup.exe");
        Make("Elsewhere", "lonely.exe");
        var rundll = Path.Combine(Environment.SystemDirectory, "rundll32.exe");

        Link("Good \u00e9\u4e2d", good, "--flag", Path.GetDirectoryName(good)!);
        Link("Moved", Old("OldPlace", "Tool", "tool.exe"), "-keep", Old("OldPlace", "Tool"));
        Link("Ambiguous", Old("C", "Twin", "dup.exe"));
        Link("Unrelated", Old("Gone", "Different", "lonely.exe"));
        Link("Removed", Old("RemovedApp", "vanished-program.exe"));
        Link("BadStart", good, "", Old("no-such-folder"));
        Link("NoArgs", rundll);
        Link("Script", Path.Combine(Environment.SystemDirectory, "wscript.exe"), $"\"{Old("missing.vbs")}\"");
        Link("Network", @"\\no-such-server\share\a.exe");

        var roots = new[] { new ShortcutRoot("Test desktop", links, false) };
        // The fixtures live under Temp, which the app normally excludes.
        var excluded = ShortcutScanOptions.DefaultExcluded.Where(p => p != @"\temp\").ToList();
        var options = new ShortcutScanOptions { Roots = roots, SearchFolders = new[] { apps }, ExcludedParts = excluded };
        check("files in Temp are not offered as new targets", !apps.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
              ShortcutScanner.Scan(new ShortcutScanOptions { Roots = roots, SearchFolders = new[] { apps } })
                  .Findings.All(f => f.Candidates.Count == 0));
        var scan = ShortcutScanner.Scan(options);
        ShortcutFinding? Found(string name) => scan.Findings.FirstOrDefault(f => f.Name == name);
        log.Add($"Fixture scan: {scan.Checked} checked, " + string.Join("; ", scan.Findings.Select(f =>
            $"{f.Name}={string.Join("+", f.Problems.Select(p => p.Kind))}")));

        check("shortcuts are read, including non-English names",
              scan.Checked == 9 && Found("Good \u00e9\u4e2d") == null);
        check("a moved file gets one clear match",
              Found("Moved") is { CanAutoRepair: true } m && m.ConfidentTarget == moved &&
              m.Has(ProblemKind.MissingTarget) && m.Has(ProblemKind.BadWorkingDirectory));
        check("two equal matches are not chosen automatically",
              Found("Ambiguous") is { CanAutoRepair: false, ConfidentTarget: null, Candidates.Count: 2 });
        check("a same-named file in an unrelated folder is only a possibility",
              Found("Unrelated") is { ConfidentTarget: null, Candidates.Count: 1 } u && !u.Has(ProblemKind.Uninstalled));
        check("a vanished program is flagged as possibly uninstalled",
              Found("Removed")?.Has(ProblemKind.Uninstalled) == true);
        check("a missing start folder is found", Found("BadStart") is { CanAutoRepair: true } b &&
              b.Problems.Single().Kind == ProblemKind.BadWorkingDirectory);
        check("missing arguments and argument files are found",
              Found("NoArgs")?.Has(ProblemKind.MissingArguments) == true &&
              Found("Script")?.Has(ProblemKind.MissingArgumentFile) == true);
        check("a network target is not called broken",
              Found("Network") == null && scan.Unverified.Any(x => x.Contains("no-such-server")));
        var freeDrive = "ZYXWVUTSRQ".FirstOrDefault(c => !Directory.Exists(c + @":\"));
        check("an unplugged drive is not called broken", ShortcutScanner.Exists(freeDrive + @":\x\a.exe") == null);

        var installed = new ShortcutScanOptions
        { Roots = roots, SearchFolders = new[] { apps }, ExcludedParts = excluded, InstallLocations = new[] { Old("RemovedApp") } };
        check("an installed-program record prevents the uninstalled label", !ShortcutScanner.Scan(installed).Findings
              .First(f => f.Name == "Removed").Has(ProblemKind.Uninstalled));

        var repair = new ShortcutRepair(roots, Path.Combine(home, "Backups"));
        var targets = scan.Findings.Where(f => f.CanAutoRepair).ToList();
        var originals = targets.ToDictionary(f => f.Path, f => Hash(f.Path));
        var fixedUp = repair.Repair(targets.Select(f => (f, f.ConfidentTarget, (string?)null)).ToList(), false);
        var after = ShellLink.Read(Path.Combine(links, "Moved.lnk"));
        check("repair re-targets, fixes the start folder and keeps arguments", fixedUp.Changed == 2 &&
              after.Target == moved && after.WorkingDirectory == Path.GetDirectoryName(moved) && after.Arguments == "-keep" &&
              ShellLink.Read(Path.Combine(links, "BadStart.lnk")).WorkingDirectory == Path.GetDirectoryName(good));
        var undone = repair.UndoLast(false);
        check("undo restores repaired shortcuts byte for byte",
              undone.Changed == 2 && originals.All(p => Hash(p.Key) == p.Value));

        var gone = Found("Removed")!;
        var goneHash = Hash(gone.Path);
        var removed = repair.Remove(new[] { gone }, false);
        var absent = !File.Exists(gone.Path);
        repair.UndoLast(false);
        check("removal is reversible", removed.Changed == 1 && absent && Hash(gone.Path) == goneHash);

        repair.Remove(new[] { gone }, false);
        ShellLink.Create(gone.Path, good);
        var newer = Hash(gone.Path);
        repair.UndoLast(false);
        check("undo never overwrites a newer file", Hash(gone.Path) == newer);

        var ambiguous = Found("Ambiguous")!;
        ShellLink.Update(ambiguous.Path, arguments: "changed");
        check("a shortcut changed after the scan is left alone",
              repair.Remove(new[] { ambiguous }, false).Changed == 0 && File.Exists(ambiguous.Path));

        var restored = Found("Unrelated")!;
        Make("Gone", "Different", "lonely.exe");
        check("a target that has come back is not removed",
              repair.Remove(new[] { restored }, false).Changed == 0 && File.Exists(restored.Path));

        var locked = new ShortcutRepair(new[] { roots[0] with { NeedsAdmin = true } }, Path.Combine(home, "Backups2"));
        check("shared shortcuts need administrator rights",
              locked.Remove(new[] { Found("NoArgs")! }, false).Changed == 0);
        var blocker = Make("blocker.txt");
        var unable = new ShortcutRepair(roots, Path.Combine(blocker, "b"));
        var noArgs = Found("NoArgs")!;
        check("no backup means no removal", unable.Remove(new[] { noArgs }, false).Changed == 0 && File.Exists(noArgs.Path));

        File.WriteAllText(Path.Combine(repair.BackupFolder, "history.json"),
            System.Text.Json.JsonSerializer.Serialize(new[]
            { new ShortcutBatch(DateTime.Now, "Repair", new() { new(Old("evil.lnk"), "x.lnk", "00", null) }) }));
        check("a saved path outside the shortcut folders is rejected",
              repair.UndoLast(true).Changed == 0 && !File.Exists(Old("evil.lnk")));

        // With a protected folder, as the app uses it. "Shared" stands for the all-users folders.
        var ownLinks = Directory.CreateDirectory(Path.Combine(home, "own-links")).FullName;
        var sharedLinks = Directory.CreateDirectory(Path.Combine(home, "shared-links")).FullName;
        var split = new[] { new ShortcutRoot("Own", ownLinks, false), new ShortcutRoot("Shared", sharedLinks, true) };
        var ownBackups = Path.Combine(home, "own-backups");
        var vault = Path.Combine(home, "shortcut-vault");
        var guarded = new ShortcutRepair(split, ownBackups, SecurityTests.Stand(vault));

        // What an attacker could write: a "removed" shared shortcut with a matching backup and hash.
        Directory.CreateDirectory(ownBackups);
        var evilBackup = Path.Combine(ownBackups, "evil.lnk");
        ShellLink.Create(evilBackup, good);
        var evilTarget = Path.Combine(sharedLinks, "Startup", "evil.lnk");
        var forged = System.Text.Json.JsonSerializer.Serialize(new[] { new ShortcutBatch(DateTime.Now, "Remove",
            new() { new(evilTarget, "evil.lnk", Hash(evilBackup), null) }) });
        File.WriteAllText(Path.Combine(ownBackups, "history.json"), forged);
        var asAdmin = guarded.UndoLast(true);
        check("as administrator, forged history in your own folder is not used",
              asAdmin.Changed == 0 && !File.Exists(evilTarget) && asAdmin.Problems.Single().Contains("open WinBlazer normally"));
        var asUser = guarded.UndoLast(false);
        check("nor can it restore a shared shortcut without administrator rights",
              asUser.Changed == 0 && !File.Exists(evilTarget) && guarded.History().Count == 0);

        var sharedLink = Path.Combine(sharedLinks, "Shared.lnk");
        var ownLink = Path.Combine(ownLinks, "Mine.lnk");
        ShellLink.Create(sharedLink, Old("GoneShared", "a.exe"));
        ShellLink.Create(ownLink, Old("GoneOwn", "b.exe"));
        var sharedHash = Hash(sharedLink);
        var ownHash = Hash(ownLink);
        var both = guarded.Remove(new[]
        {
            new ShortcutFinding { Path = sharedLink, Root = split[1], Link = ShellLink.Read(sharedLink) },
            new ShortcutFinding { Path = ownLink, Root = split[0], Link = ShellLink.Read(ownLink) }
        }, true);
        check("shared and own shortcuts are backed up separately",
              both.Changed == 2 && Directory.GetFiles(vault, "*.lnk").Length == 1 &&
              guarded.History(true).Single().Items.Single().Path == sharedLink &&
              guarded.History(false).Single().Items.Single().Path == ownLink);
        var undoShared = guarded.UndoLast(true);
        check("administrator undo restores the shared shortcut only",
              undoShared.Changed == 1 && Hash(sharedLink) == sharedHash && !File.Exists(ownLink));
        var undoOwn = guarded.UndoLast(false);
        check("normal undo restores your own shortcut", undoOwn.Changed == 1 && Hash(ownLink) == ownHash);

        // Real PC: read-only, verified by hashing every shortcut before and after.
        var real = ShortcutScanOptions.ForThisPc();
        Dictionary<string, string> Hashes()
        {
            var result = new Dictionary<string, string>();
            foreach (var file in real.Roots.SelectMany(r => ShortcutScanner.ShortcutFiles(r.Path)))
                try { result[file] = Hash(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return result;
        }
        var before = Hashes();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var mine = ShortcutScanner.Scan(real);
        log.Add($"This PC: {mine.Checked} shortcuts, {mine.Findings.Count} with problems, " +
                $"{mine.Unverified.Count} unverified, {clock.Elapsed.TotalSeconds:N1} s");
        foreach (var f in mine.Findings)
            log.Add($"  * {f.Name} [{f.Root.Label}] args='{f.Link.Arguments}' candidates={f.Candidates.Count} " +
                    $"clear={f.ConfidentTarget ?? "-"}\n    " +
                    string.Join("\n    ", f.Problems.Select(p => $"{p.Kind}: {p.Expected} -> {p.Actual}")));
        foreach (var line in mine.Unverified) log.Add("  ? " + line);
        var unchanged = Hashes();
        check("scanning leaves real shortcuts unchanged",
              before.Count == unchanged.Count && before.All(p => unchanged.GetValueOrDefault(p.Key) == p.Value));
    }
}
