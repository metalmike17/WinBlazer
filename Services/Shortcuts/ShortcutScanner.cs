using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCOptimizer.Services.ShortcutTools;

public sealed record ShortcutRoot(string Label, string Path, bool NeedsAdmin);

public enum ProblemKind { MissingTarget, MissingArguments, MissingArgumentFile, BadWorkingDirectory, Uninstalled }

public sealed record ShortcutProblem(ProblemKind Kind, string Expected, string Actual);

public sealed record Candidate(string Path, int Match);

public sealed class ShortcutFinding
{
    public required string Path { get; init; }
    public required ShortcutRoot Root { get; init; }
    public required LinkData Link { get; init; }
    public List<ShortcutProblem> Problems { get; } = new();
    public List<Candidate> Candidates { get; set; } = new();
    /// <summary>Arguments copied from a same-named shortcut to the same program.</summary>
    public string? SuggestedArguments { get; set; }
    /// <summary>The one clearly matching new location, if there is one.</summary>
    public string? ConfidentTarget { get; set; }

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public bool Has(ProblemKind kind) => Problems.Any(p => p.Kind == kind);
    public bool CanAutoRepair => !Link.Advertised && !Has(ProblemKind.MissingArguments) &&
        !Has(ProblemKind.MissingArgumentFile) &&
        (ConfidentTarget != null || (Has(ProblemKind.BadWorkingDirectory) && !Has(ProblemKind.MissingTarget)));
}

public sealed record ShortcutScan(List<ShortcutFinding> Findings, int Checked, List<string> Unverified);

public sealed class ShortcutScanOptions
{
    public required IReadOnlyList<ShortcutRoot> Roots { get; init; }
    /// <summary>Folders searched for moved files.</summary>
    public required IReadOnlyList<string> SearchFolders { get; init; }
    public IReadOnlyList<string> InstallLocations { get; init; } = Array.Empty<string>();
    public bool TryOtherDrives { get; init; }
    /// <summary>A file in one of these places is never offered as a new target.</summary>
    public IReadOnlyList<string> ExcludedParts { get; init; } = DefaultExcluded;

    public static readonly IReadOnlyList<string> DefaultExcluded = new[]
        { @"\$recycle.bin\", @"\windows\winsxs\", @"\windows\installer\", @"\package cache\", @"\temp\", @"\windows.old\" };

    private static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    public static IReadOnlyList<ShortcutRoot> DefaultRoots() => new[]
    {
        new ShortcutRoot("Your desktop", Folder(Environment.SpecialFolder.DesktopDirectory), false),
        new ShortcutRoot("Public desktop", Folder(Environment.SpecialFolder.CommonDesktopDirectory), true),
        new ShortcutRoot("Your Start menu", Folder(Environment.SpecialFolder.StartMenu), false),
        new ShortcutRoot("All users Start menu", Folder(Environment.SpecialFolder.CommonStartMenu), true)
    }.Where(r => r.Path.Length > 0).ToList();

    public static ShortcutScanOptions ForThisPc() => new()
    {
        Roots = DefaultRoots(),
        SearchFolders = new[]
        {
            Folder(Environment.SpecialFolder.ProgramFiles), Folder(Environment.SpecialFolder.ProgramFilesX86),
            System.IO.Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Folder(Environment.SpecialFolder.MyDocuments), Folder(Environment.SpecialFolder.MyPictures),
            Folder(Environment.SpecialFolder.DesktopDirectory)
        }.Where(p => p.Length > 3).ToList(),
        InstallLocations = ReadInstallLocations(),
        TryOtherDrives = true
    };

    private static List<string> ReadInstallLocations()
    {
        var result = new List<string>();
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, uninstall), (Registry.CurrentUser, uninstall),
                     (Registry.LocalMachine, uninstall.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\"))
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(name);
                    if (app?.GetValue("InstallLocation") is string location && location.Trim('"', ' ').Length > 3)
                        result.Add(location.Trim('"', ' ').TrimEnd('\\'));
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return result;
    }
}

public static class ShortcutScanner
{
    private const int DirectoryLimit = 40000;

    /// <summary>True or false only when certain. Null means it could not be checked.</summary>
    public static bool? Exists(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) return null;
        try
        {
            var drive = new DriveInfo(path[..1]);
            // An unplugged or network drive says nothing about whether the file still exists.
            if (drive.DriveType == DriveType.Network || !drive.IsReady) return null;
            if (Probe(path) is not false) return Probe(path);
            return Alternates(path).Any(a => Probe(a) == true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool? Probe(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException) { return null; }
    }

    /// <summary>32-bit and 64-bit views of the same folder, which shortcuts sometimes confuse.</summary>
    private static IEnumerable<string> Alternates(string path)
    {
        foreach (var (from, to) in new[]
                 {
                     (@"\Program Files (x86)\", @"\Program Files\"), (@"\Program Files\", @"\Program Files (x86)\"),
                     (@"\System32\", @"\SysWOW64\"), (@"\SysWOW64\", @"\System32\"), (@"\Sysnative\", @"\System32\")
                 })
            if (path.Contains(from, StringComparison.OrdinalIgnoreCase))
                yield return path.Replace(from, to, StringComparison.OrdinalIgnoreCase);
    }

    private static string Expand(string text) => Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));

    public static IEnumerable<string> ShortcutFiles(string root, CancellationToken token = default) =>
        Walk(root, 12, token).SelectMany(folder => SafeFiles(folder, "*.lnk"))
            .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase));

    private static string[] SafeFiles(string folder, string pattern)
    {
        try { return Directory.GetFiles(folder, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Folders under a root, without following links.</summary>
    private static IEnumerable<string> Walk(string root, int depth, CancellationToken token)
    {
        if (!Directory.Exists(root)) yield break;
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var count = 0;
        while (pending.Count > 0 && count++ < DirectoryLimit)
        {
            token.ThrowIfCancellationRequested();
            var (path, level) = pending.Dequeue();
            yield return path;
            if (level >= depth) continue;
            try
            {
                foreach (var child in new DirectoryInfo(path).GetDirectories())
                    if ((child.Attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Enqueue((child.FullName, level + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static ShortcutScan Scan(ShortcutScanOptions options, CancellationToken token = default)
    {
        var findings = new List<ShortcutFinding>();
        var unverified = new List<string>();
        var all = new List<(string Path, LinkData Link)>();
        var checkedCount = 0;
        foreach (var root in options.Roots)
            foreach (var file in ShortcutFiles(root.Path, token))
            {
                LinkData link;
                try { link = ShellLink.Read(file); }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                               or ArgumentException)
                { unverified.Add($"{file}: could not be read."); continue; }
                checkedCount++;
                all.Add((file, link));
                var finding = new ShortcutFinding { Path = file, Root = root, Link = link };
                Inspect(finding, unverified);
                if (finding.Problems.Count > 0) findings.Add(finding);
            }

        FindMovedFiles(findings, options, token);
        foreach (var finding in findings)
        {
            if (finding.Has(ProblemKind.MissingArguments))
            {
                var twins = all.Where(a => !a.Path.Equals(finding.Path, StringComparison.OrdinalIgnoreCase) &&
                        System.IO.Path.GetFileName(a.Path).Equals(System.IO.Path.GetFileName(finding.Path),
                                                                  StringComparison.OrdinalIgnoreCase) &&
                        a.Link.Target.Equals(finding.Link.Target, StringComparison.OrdinalIgnoreCase) &&
                        a.Link.Arguments.Trim().Length > 0)
                    .Select(a => a.Link.Arguments).Distinct().ToList();
                if (twins.Count == 1) finding.SuggestedArguments = twins[0];
            }
            if (LooksUninstalled(finding, options.InstallLocations))
                finding.Problems.Add(new(ProblemKind.Uninstalled, "Installed program",
                    "Possibly uninstalled: its program folder is gone and no installed-program record covers it."));
        }
        return new(findings.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
                   checkedCount, unverified);
    }

    private static readonly string[] NeedArguments = { "rundll32", "msiexec", "mshta", "wscript", "cscript" };
    private static readonly string[] Hosts =
        { "rundll32", "wscript", "cscript", "mshta", "cmd", "powershell", "pwsh", "javaw", "java", "pythonw", "python" };

    private static void Inspect(ShortcutFinding finding, List<string> unverified)
    {
        var link = finding.Link;
        if (link.Advertised)
        {
            if (ShellLink.AdvertisedProductMissing(finding.Path) == true)
                finding.Problems.Add(new(ProblemKind.Uninstalled, "Windows Installer product",
                    "Windows Installer says the product is not installed."));
            return;
        }
        // Shortcuts to Store apps and system folders have no file path to check.
        if (link.Target.Length == 0) return;

        var target = Expand(link.Target);
        var exists = Exists(target);
        if (exists == null) unverified.Add($"{finding.Path}: {target} could not be checked (network, unplugged or protected).");
        if (exists == false)
            finding.Problems.Add(new(ProblemKind.MissingTarget, target, "File not found"));

        if (link.WorkingDirectory.Trim().Length > 0)
        {
            var directory = Expand(link.WorkingDirectory);
            if (Exists(directory) == false)
                finding.Problems.Add(new(ProblemKind.BadWorkingDirectory,
                    "Start in: " + (exists == true ? System.IO.Path.GetDirectoryName(target) : "an existing folder"),
                    $"Start in folder not found: {directory}"));
        }

        var program = System.IO.Path.GetFileNameWithoutExtension(target).ToLowerInvariant();
        var arguments = link.Arguments.Trim();
        if (exists == true && arguments.Length == 0)
        {
            var parent = System.IO.Path.GetFileName(target).ToLowerInvariant();
            if (NeedArguments.Contains(program) || parent == "chrome_proxy.exe" || parent == "msedge_proxy.exe")
                finding.Problems.Add(new(ProblemKind.MissingArguments,
                    $"Arguments telling {System.IO.Path.GetFileName(target)} what to open", "No arguments"));
        }

        var product = Regex.Match(arguments, @"/[ix]\s*(\{[0-9A-F\-]{36}\})", RegexOptions.IgnoreCase);
        if (program == "msiexec" && product.Success && ShellLink.ProductMissing(product.Groups[1].Value))
            finding.Problems.Add(new(ProblemKind.Uninstalled, "Windows Installer product " + product.Groups[1].Value,
                "Windows Installer says the product is not installed."));

        if (Hosts.Contains(program))
            foreach (var file in ArgumentFiles(arguments))
                if (Exists(file) == false)
                    finding.Problems.Add(new(ProblemKind.MissingArgumentFile, file,
                        "A file named in the arguments was not found"));

        // Self-updating apps start through Update.exe --processStart name.exe from an app-version folder.
        var start = Regex.Match(arguments, "--processStart(?:AndWait)?[= ]\"?([^\" ]+\\.exe)", RegexOptions.IgnoreCase);
        if (program == "update" && exists == true && start.Success && start.Groups[1].Value.IndexOfAny(new[] { '\\', '/' }) < 0)
        {
            var home = System.IO.Path.GetDirectoryName(target)!;
            try
            {
                var found = File.Exists(System.IO.Path.Combine(home, start.Groups[1].Value)) ||
                    Directory.GetDirectories(home, "app-*").Any(d => File.Exists(System.IO.Path.Combine(d, start.Groups[1].Value)));
                if (!found)
                    finding.Problems.Add(new(ProblemKind.MissingArgumentFile, start.Groups[1].Value,
                        "The program this updater should start was not found"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Only complete, unambiguous script or program paths.</summary>
    public static IEnumerable<string> ArgumentFiles(string arguments)
    {
        foreach (Match match in Regex.Matches(arguments, "\"([^\"]+)\"|([^\\s\"]+)"))
        {
            var token = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            token = Environment.ExpandEnvironmentVariables(token);
            var file = Regex.Match(token, @"^([A-Za-z]:\\[^*?<>|]*?\.(?:vbs|vbe|js|wsf|ps1|bat|cmd|exe|dll|hta|jar|py|pyw|cpl))(?:,.*)?$",
                                   RegexOptions.IgnoreCase);
            if (file.Success) yield return file.Groups[1].Value;
        }
    }

    // ---- Finding moved files ----

    /// <summary>Folder names are compared without version numbers, so app-1.0.1 matches app-1.0.2.</summary>
    private static string Normalize(string segment) => Regex.Replace(segment, @"[\d._\- ]+", "").ToLowerInvariant();

    /// <summary>How many parent folder names, nearest first, the two paths share.</summary>
    public static int ParentMatch(string oldPath, string newPath)
    {
        var a = oldPath.Split('\\').SkipLast(1).Skip(1).Reverse().ToList();
        var b = newPath.Split('\\').SkipLast(1).Skip(1).Reverse().ToList();
        var count = 0;
        while (count < a.Count && count < b.Count && Normalize(a[count]) == Normalize(b[count])) count++;
        return count;
    }

    private static void FindMovedFiles(List<ShortcutFinding> findings, ShortcutScanOptions options, CancellationToken token)
    {
        var broken = findings.Where(f => f.Has(ProblemKind.MissingTarget)).ToList();
        if (broken.Count == 0) return;
        var found = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var wanted = broken.Select(f => System.IO.Path.GetFileName(Expand(f.Link.Target)))
            .Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void Note(string file)
        {
            var name = System.IO.Path.GetFileName(file);
            if (!wanted.Contains(name) ||
                options.ExcludedParts.Any(p => file.Contains(p, StringComparison.OrdinalIgnoreCase))) return;
            if (!found.TryGetValue(name, out var set)) found[name] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(file);
        }
        void Search(string root, int depth)
        {
            foreach (var folder in Walk(root, depth, token))
                foreach (var file in SafeFiles(folder, "*")) Note(file);
        }

        foreach (var folder in options.SearchFolders) Search(folder, 5);
        // Near the old location: catches renamed version folders and moved subfolders.
        var broad = options.SearchFolders.Select(f => f.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in broken)
        {
            var old = Expand(finding.Link.Target);
            var parent = System.IO.Path.GetDirectoryName(old);
            for (var level = 0; level < 2 && parent != null && !Directory.Exists(parent); level++)
                parent = System.IO.Path.GetDirectoryName(parent);
            if (parent != null && parent.Length > 3 && !broad.Contains(parent) && Directory.Exists(parent) &&
                !parent.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) &&
                !parent.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
                Search(parent, 3);

            // The same path on another drive, for changed drive letters.
            if (options.TryOtherDrives && old.Length > 3 && old[1] == ':')
                foreach (var drive in DriveInfo.GetDrives())
                    if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady)
                    {
                        var moved = drive.Name + old[3..];
                        if (!moved.Equals(old, StringComparison.OrdinalIgnoreCase) && File.Exists(moved)) Note(moved);
                    }
        }

        foreach (var finding in broken)
        {
            var old = Expand(finding.Link.Target);
            if (!found.TryGetValue(System.IO.Path.GetFileName(old), out var set)) continue;
            finding.Candidates = set.Select(p => new Candidate(p, ParentMatch(old, p)))
                .OrderByDescending(c => c.Match).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            // Confident only when exactly one candidate also sits in a matching folder.
            var matching = finding.Candidates.Where(c => c.Match > 0).ToList();
            if (matching.Count > 0 && matching.Count(c => c.Match == matching[0].Match) == 1)
                finding.ConfidentTarget = matching[0].Path;
        }
    }

    private static bool LooksUninstalled(ShortcutFinding finding, IReadOnlyList<string> installs)
    {
        if (finding.Has(ProblemKind.Uninstalled) || !finding.Has(ProblemKind.MissingTarget) ||
            finding.Candidates.Count > 0) return false;
        var target = Expand(finding.Link.Target);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var folder = System.IO.Path.GetDirectoryName(target);
        return folder != null && Exists(folder) == false &&
               !installs.Any(i => (target + "\\").StartsWith(i + "\\", StringComparison.OrdinalIgnoreCase));
    }
}
