using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using PCOptimizer.Services;

namespace PCOptimizer;

/// <summary>Uses fake browser profiles, a fake Recent folder and a throwaway registry key.</summary>
public static class BrowserTests
{
    private static void Sql(string database, string sql)
    {
        using var c = new SqliteConnection($"Data Source={database};Pooling=False");
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Number(string database, string sql)
    {
        using var c = new SqliteConnection($"Data Source={database};Pooling=False");
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Make(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "data");
        return path;
    }

    public static void Run(string work, Action<string, bool> check)
    {
        var local = Path.Combine(work, "browsers", "local");
        var roaming = Path.Combine(work, "browsers", "roaming");

        // ---- Chromium ----
        var chrome = Path.Combine(local, "Google", "Chrome", "User Data", "Default");
        var cache = Make(chrome, "Cache", "Cache_Data", "f_1");
        var cookies = Make(chrome, "Network", "Cookies");
        var session = Make(chrome, "Sessions", "Session_1");
        var logins = Make(chrome, "Login Data");
        var bookmarks = Make(chrome, "Bookmarks");
        var autofill = Make(chrome, "Web Data");
        var history = Path.Combine(chrome, "History");
        Sql(history, "CREATE TABLE urls(id INTEGER); CREATE TABLE visits(id INTEGER); CREATE TABLE downloads(id INTEGER);" +
                     "CREATE TABLE downloads_url_chains(id INTEGER); INSERT INTO urls VALUES(1); INSERT INTO visits VALUES(1);" +
                     "INSERT INTO downloads VALUES(1); INSERT INTO downloads_url_chains VALUES(1);");

        // ---- Firefox ----
        var fox = Path.Combine(roaming, "Mozilla", "Firefox", "Profiles", "abc.default");
        Directory.CreateDirectory(fox);
        var places = Path.Combine(fox, "places.sqlite");
        // Pages: 1 bookmarked and visited, 2 plain visit, 3 download, 4 bookmarked only.
        Sql(places,
            "CREATE TABLE moz_places(id INTEGER PRIMARY KEY, visit_count INTEGER, last_visit_date INTEGER, foreign_count INTEGER);" +
            "CREATE TABLE moz_historyvisits(id INTEGER PRIMARY KEY, place_id INTEGER, visit_type INTEGER);" +
            "CREATE TABLE moz_bookmarks(id INTEGER PRIMARY KEY, fk INTEGER);" +
            "CREATE TABLE moz_anno_attributes(id INTEGER PRIMARY KEY, name TEXT);" +
            "CREATE TABLE moz_annos(id INTEGER PRIMARY KEY, place_id INTEGER, anno_attribute_id INTEGER);" +
            "INSERT INTO moz_places VALUES(1,1,5,1),(2,1,5,0),(3,0,5,0),(4,0,NULL,1);" +
            "INSERT INTO moz_historyvisits VALUES(1,1,1),(2,2,1),(3,3,7);" +
            "INSERT INTO moz_bookmarks VALUES(1,1),(2,4);" +
            "INSERT INTO moz_anno_attributes VALUES(1,'downloads/destinationFileURI');" +
            "INSERT INTO moz_annos VALUES(1,3,1);");
        var foxLogins = Make(fox, "logins.json");

        var found = BrowserCleaner.Detect(local, roaming);
        var c = found.First(b => b.Id == "chrome");
        var f = found.First(b => b.Id == "firefox");
        check("fake profiles are detected", c.Found && f.Found && !found.First(b => b.Id == "edge").Found);

        BrowserCleaner.Clear(c, Enum.GetValues<BrowserData>(), _ => true);
        check("a running browser is left untouched",
              File.Exists(cookies) && File.Exists(cache) && Number(history, "SELECT COUNT(*) FROM urls") == 1);

        BrowserCleaner.Clear(c, new[] { BrowserData.Downloads }, _ => false);
        check("Chromium: clearing downloads keeps browsing history",
              Number(history, "SELECT COUNT(*) FROM downloads") == 0 &&
              Number(history, "SELECT COUNT(*) FROM urls") == 1 && File.Exists(cookies));

        Sql(history, "INSERT INTO downloads VALUES(2)");
        BrowserCleaner.Clear(c, new[] { BrowserData.History }, _ => false);
        check("Chromium: clearing history keeps downloads",
              Number(history, "SELECT COUNT(*) FROM urls") + Number(history, "SELECT COUNT(*) FROM visits") == 0 &&
              Number(history, "SELECT COUNT(*) FROM downloads") == 1);

        var chromeResults = BrowserCleaner.Clear(c,
            new[] { BrowserData.Cache, BrowserData.Cookies, BrowserData.Sessions }, _ => false);
        check("Chromium: cache, cookies and sessions are removed",
              chromeResults.All(r => r.Ok) && !File.Exists(cache) && !File.Exists(cookies) && !File.Exists(session));
        check("Chromium: passwords, bookmarks and autofill remain",
              File.Exists(logins) && File.Exists(bookmarks) && File.Exists(autofill));

        BrowserCleaner.Clear(f, new[] { BrowserData.History }, _ => false);
        check("Firefox: history cleared, bookmarks and downloads kept",
              Number(places, "SELECT COUNT(*) FROM moz_bookmarks") == 2 &&
              Number(places, "SELECT COUNT(*) FROM moz_places WHERE id IN (1,3,4)") == 3 &&
              Number(places, "SELECT COUNT(*) FROM moz_places WHERE id = 2") == 0 &&
              Number(places, "SELECT COUNT(*) FROM moz_historyvisits WHERE visit_type <> 7") == 0 &&
              Number(places, "SELECT COUNT(*) FROM moz_historyvisits WHERE visit_type = 7") == 1);
        BrowserCleaner.Clear(f, new[] { BrowserData.Downloads }, _ => false);
        check("Firefox: downloads cleared, bookmarks kept",
              Number(places, "SELECT COUNT(*) FROM moz_annos") == 0 &&
              Number(places, "SELECT COUNT(*) FROM moz_historyvisits") == 0 &&
              Number(places, "SELECT COUNT(*) FROM moz_places") == 2 &&
              Number(places, "SELECT COUNT(*) FROM moz_bookmarks") == 2 && File.Exists(foxLogins));

        Sql(history, "DROP TABLE urls");
        var visited = Make(chrome, "Visited Links");
        var odd = BrowserCleaner.Clear(c, new[] { BrowserData.History }, _ => false);
        check("an unrecognized database is not changed", !odd[0].Ok && File.Exists(visited));

        // ---- Windows traces ----
        var recent = Path.Combine(work, "recent");
        var link = Make(recent, "doc.lnk");
        var notLink = Make(recent, "keep.txt");
        var jump = Make(recent, "AutomaticDestinations", "1111.automaticDestinations-ms");
        var pinned = Make(recent, "AutomaticDestinations", WindowsTraces.QuickAccessFile);
        var testRoot = @"Software\PCOptimizerSelfTest-" + Guid.NewGuid().ToString("N");
        try
        {
            const string explorer = @"\Software\Microsoft\Windows\CurrentVersion\Explorer\";
            using (var mru = Registry.CurrentUser.CreateSubKey(testRoot + explorer + "RunMRU")) mru.SetValue("a", "cmd");
            using (var other = Registry.CurrentUser.CreateSubKey(testRoot + explorer + "Advanced")) other.SetValue("keep", 1);
            using (var usage = Registry.CurrentUser.CreateSubKey(testRoot + explorer + @"UserAssist\{x}\Count")) usage.SetValue("n", 1);
            using (var parent = Registry.CurrentUser.CreateSubKey(testRoot + explorer + @"UserAssist\{x}")) parent.SetValue("Version", 5);

            var traces = new WindowsTraces(recent, testRoot + @"\");
            check("trace counts are correct", traces.Count(TraceKind.RecentFiles) == 1 &&
                  traces.Count(TraceKind.JumpLists) == 1 && traces.Count(TraceKind.MruLists) == 1 &&
                  traces.Count(TraceKind.AppUsage) == 1);
            traces.Erase(TraceKind.RecentFiles);
            check("only recent shortcuts are removed", !File.Exists(link) && File.Exists(notLink) && File.Exists(jump));
            traces.Erase(TraceKind.JumpLists);
            check("jump lists removed, Quick Access pins kept", !File.Exists(jump) && File.Exists(pinned));
            traces.Erase(TraceKind.MruLists);
            traces.Erase(TraceKind.AppUsage);
            using var kept = Registry.CurrentUser.OpenSubKey(testRoot + explorer + "Advanced");
            using var version = Registry.CurrentUser.OpenSubKey(testRoot + explorer + @"UserAssist\{x}");
            check("registry lists emptied, other settings kept", traces.Count(TraceKind.MruLists) == 0 &&
                  traces.Count(TraceKind.AppUsage) == 0 && kept?.GetValue("keep") != null &&
                  version?.GetValue("Version") != null);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false); }
    }
}
