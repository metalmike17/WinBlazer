using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCOptimizer.Services.GameTools;

/// <summary>Folder is a game's install folder, or the full path of a single custom program.</summary>
public sealed record GameInfo(string Id, string Name, string Store, string Folder);

public sealed record ProcessInfo(int Id, string Path);

public sealed record RunningGame(GameInfo Game, IReadOnlyList<int> ProcessIds);

/// <summary>Finds installed games from each launcher's own records. Read-only.</summary>
public static class GameLibrary
{
    /// <summary>Steam items that are tools, not games. Others can be unticked on the page.</summary>
    private static readonly string[] NotGames =
        { "steamworks", "steamvr", "wallpaper engine", "soundpad", "lossless scaling", "proton", "steam linux" };

    /// <summary>Helper programs that do not mean a game is being played.</summary>
    private static readonly string[] Helpers =
        { "crash", "unins", "setup", "redist", "launcher", "easyanticheat", "beservice", "installer", "prereq", "updater" };

    public static List<GameInfo> Scan(IEnumerable<string> customPaths)
    {
        var games = new List<GameInfo>();
        Try(() => Steam(games));
        Try(() => Epic(games, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                           "Epic", "EpicGamesLauncher", "Data", "Manifests")));
        Try(() => Gog(games));
        Try(() => Xbox(games));
        games.AddRange(Custom(customPaths));
        return games.Where(g => g.Store == "Custom" || IsUsableFolder(g.Folder))
            .GroupBy(g => g.Folder, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or JsonException or ArgumentException) { }
    }

    /// <summary>A drive root or Windows folder would match unrelated programs.</summary>
    public static bool IsUsableFolder(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\")) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (full.Length <= 3) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var broad = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        return !(full + "\\").StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase) &&
               !broad.Contains(full, StringComparer.OrdinalIgnoreCase);
    }

    public static List<string> ParseSteamLibraries(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"")
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    public static GameInfo? ParseSteamManifest(string acf, string library)
    {
        string? Field(string key)
        {
            var match = Regex.Match(acf, $"\"{key}\"\\s+\"((?:\\\\.|[^\"\\\\])*)\"");
            return match.Success ? match.Groups[1].Value : null;
        }
        var (id, name, folder) = (Field("appid"), Field("name"), Field("installdir"));
        if (id == null || name == null || string.IsNullOrWhiteSpace(folder) || folder is "." or ".." ||
            folder.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 ||
            NotGames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) return null;
        return new GameInfo("steam:" + id, name, "Steam", Path.Combine(library, "steamapps", "common", folder));
    }

    private static void Steam(List<GameInfo> games)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string raw) return;
        var steam = Path.GetFullPath(raw);
        var libraries = new List<string> { steam };
        var list = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(list)) libraries.AddRange(ParseSteamLibraries(File.ReadAllText(list)));
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            if (library.StartsWith(@"\\") || !Directory.Exists(apps)) continue;
            foreach (var file in Directory.GetFiles(apps, "appmanifest_*.acf"))
                Try(() =>
                {
                    if (ParseSteamManifest(File.ReadAllText(file), Path.GetFullPath(library)) is { } game &&
                        Directory.Exists(game.Folder)) games.Add(game);
                });
        }
    }

    public static void Epic(List<GameInfo> games, string manifests)
    {
        if (!Directory.Exists(manifests)) return;
        foreach (var file in Directory.GetFiles(manifests, "*.item"))
            Try(() =>
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                var root = json.RootElement;
                string? Text(string name) =>
                    root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                // Engines and plugins are listed too; only entries in the "games" category count.
                if (root.TryGetProperty("AppCategories", out var categories) &&
                    categories.ValueKind == JsonValueKind.Array &&
                    !categories.EnumerateArray().Any(c => c.GetString() == "games")) return;
                if (Text("InstallLocation") is { } folder && Directory.Exists(folder) &&
                    Text("DisplayName") is { } name)
                    games.Add(new GameInfo("epic:" + (Text("AppName") ?? name), name, "Epic", Path.GetFullPath(folder)));
            });
    }

    private static void Gog(List<GameInfo> games)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root == null) return;
        foreach (var id in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(id);
            if (key?.GetValue("path") is string folder && Directory.Exists(folder))
                games.Add(new GameInfo("gog:" + id, key.GetValue("gameName") as string ?? id, "GOG",
                                       Path.GetFullPath(folder)));
        }
    }

    private static void Xbox(List<GameInfo> games)
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var folders = new List<string> { Path.Combine(drive.Name, "XboxGames") };
            // .GamingRoot names a custom Xbox library folder: 8 header bytes, then UTF-16 text.
            Try(() =>
            {
                var marker = Path.Combine(drive.Name, ".GamingRoot");
                if (!File.Exists(marker) || new FileInfo(marker).Length is < 10 or > 1024) return;
                var bytes = File.ReadAllBytes(marker);
                var relative = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).TrimEnd('\0');
                if (relative.Length > 0 && !relative.Contains("..") && !Path.IsPathRooted(relative))
                    folders.Add(Path.Combine(drive.Name, relative));
            });
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
                Try(() =>
                {
                    foreach (var game in Directory.GetDirectories(folder))
                        if (Directory.Exists(Path.Combine(game, "Content")))
                            games.Add(new GameInfo("xbox:" + Path.GetFileName(game), Path.GetFileName(game), "Xbox",
                                                   Path.Combine(game, "Content")));
                });
        }
    }

    public static IEnumerable<GameInfo> Custom(IEnumerable<string> paths) =>
        paths.Where(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.IsPathFullyQualified(p) : IsUsableFolder(p))
            .Select(p => new GameInfo("custom:" + p.ToLowerInvariant(),
                Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(p)), "Custom",
                Path.TrimEndingDirectorySeparator(p)));

    public static List<RunningGame> Match(IReadOnlyList<GameInfo> games, IReadOnlyList<ProcessInfo> processes)
    {
        var result = new List<RunningGame>();
        foreach (var game in games)
        {
            var single = game.Folder.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var ids = processes.Where(p => single
                    ? p.Path.Equals(game.Folder, StringComparison.OrdinalIgnoreCase)
                    : p.Path.StartsWith(game.Folder + "\\", StringComparison.OrdinalIgnoreCase) &&
                      !Helpers.Any(h => Path.GetFileName(p.Path).Contains(h, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Id).ToList();
            if (ids.Count > 0) result.Add(new RunningGame(game, ids));
        }
        return result;
    }

    /// <summary>Programs in this sign-in session. Needs only limited access to each one.</summary>
    public static List<ProcessInfo> RunningProcesses()
    {
        var result = new List<ProcessInfo>();
        using var self = Process.GetCurrentProcess();
        var buffer = new StringBuilder(1024);
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                if (process.SessionId != self.SessionId) continue;
                var handle = OpenProcess(0x1000, false, process.Id);
                if (handle == IntPtr.Zero) continue;
                var size = buffer.Capacity;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                    result.Add(new ProcessInfo(process.Id, buffer.ToString()));
                CloseHandle(handle);
            }
        return result;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
