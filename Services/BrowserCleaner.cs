using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace PCOptimizer.Services;

public enum BrowserData { Cache, Cookies, History, Downloads, Sessions }

public sealed record BrowserProfile(string DataFolder, string CacheFolder);

public sealed record Browser(string Id, string Name, string Process, bool IsFirefox, IReadOnlyList<BrowserProfile> Profiles)
{
    public bool Found => Profiles.Count > 0;
}

public sealed record EraseResult(string Item, bool Ok, string Message);

/// <summary>
/// Removes selected browser data from standard profile locations. Never touches passwords,
/// bookmarks, extensions or autofill. Browsers must be closed; they are never force-closed.
/// </summary>
public static class BrowserCleaner
{
    public static IReadOnlyList<Browser> Detect(string? localAppData = null, string? roamingAppData = null)
    {
        var local = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Browser Chromium(string id, string name, string process, params string[] parts)
        {
            var userData = Path.Combine(parts.Prepend(local).ToArray());
            var profiles = Folders(userData)
                .Where(d => Path.GetFileName(d) == "Default" ||
                            Path.GetFileName(d).StartsWith("Profile ", StringComparison.Ordinal))
                .Select(d => new BrowserProfile(d, d)).ToList();
            return new Browser(id, name, process, false, profiles);
        }

        var firefox = Folders(Path.Combine(roaming, "Mozilla", "Firefox", "Profiles"))
            .Select(d => new BrowserProfile(d,
                Path.Combine(local, "Mozilla", "Firefox", "Profiles", Path.GetFileName(d))))
            .ToList();

        return new[]
        {
            Chromium("chrome", "Chrome", "chrome", "Google", "Chrome", "User Data"),
            Chromium("edge", "Edge", "msedge", "Microsoft", "Edge", "User Data"),
            new Browser("firefox", "Firefox", "firefox", true, firefox),
            Chromium("brave", "Brave", "brave", "BraveSoftware", "Brave-Browser", "User Data")
        };
    }

    private static IEnumerable<string> Folders(string parent)
    {
        try { return Directory.Exists(parent) ? Directory.GetDirectories(parent) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public static bool IsRunning(string process)
    {
        var found = Process.GetProcessesByName(process);
        foreach (var p in found) p.Dispose();
        return found.Length > 0;
    }

    /// <summary>Files that are removed whole. History databases are edited instead.</summary>
    private static IEnumerable<CleanTarget> Targets(Browser browser, BrowserProfile profile, BrowserData type)
    {
        string D(params string[] p) => Path.Combine(p.Prepend(profile.DataFolder).ToArray());
        CleanTarget One(string folder, string name) => new(folder, name, false);

        if (browser.IsFirefox)
            return type switch
            {
                BrowserData.Cache => new[] { new CleanTarget(Path.Combine(profile.CacheFolder, "cache2")) },
                BrowserData.Cookies => new[] { "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm" }
                    .Select(n => One(profile.DataFolder, n)),
                BrowserData.Sessions => new[]
                    { One(profile.DataFolder, "sessionstore.jsonlz4"), new CleanTarget(D("sessionstore-backups")) },
                _ => Array.Empty<CleanTarget>()
            };

        return type switch
        {
            BrowserData.Cache => new[] { "Cache", "Code Cache", "GPUCache" }.Select(n => new CleanTarget(D(n))),
            BrowserData.Cookies => new[] { "Cookies", "Cookies-journal" }
                .SelectMany(n => new[] { One(D("Network"), n), One(profile.DataFolder, n) }),
            // Other files built from browsing history.
            BrowserData.History => new[]
                {
                    "Visited Links", "Top Sites", "Top Sites-journal", "Shortcuts", "Shortcuts-journal",
                    "Network Action Predictor", "Network Action Predictor-journal", "History Provider Cache"
                }.Select(n => One(profile.DataFolder, n)),
            BrowserData.Sessions => new[] { "Current Session", "Current Tabs", "Last Session", "Last Tabs" }
                .Select(n => One(profile.DataFolder, n)).Append(new CleanTarget(D("Sessions"))),
            _ => Array.Empty<CleanTarget>()
        };
    }

    public static IReadOnlyList<EraseResult> Clear(Browser browser, IReadOnlyCollection<BrowserData> types,
                                                   Func<string, bool>? isRunning = null)
    {
        isRunning ??= IsRunning;
        var results = new List<EraseResult>();
        foreach (var type in types.Distinct().OrderBy(t => t))
        {
            var label = $"{browser.Name}: {Label(type)}";
            if (isRunning(browser.Process))
            {
                results.Add(new(label, false,
                    $"Not cleared. {browser.Process} is running (it may be running in the background)."));
                continue;
            }
            long removed = 0, skipped = 0;
            var errors = new List<string>();
            foreach (var profile in browser.Profiles)
            {
                try
                {
                    // A database failure throws, which also skips this profile's related files.
                    if (type is BrowserData.History or BrowserData.Downloads)
                    {
                        var database = Path.Combine(profile.DataFolder, browser.IsFirefox ? "places.sqlite" : "History");
                        if (File.Exists(database))
                        {
                            if (!CleanupEngine.IsSafePath(database, profile.DataFolder))
                                throw new IOException("The history database is a link, so it was left alone.");
                            removed += browser.IsFirefox ? EditFirefox(database, type) : EditChromium(database, type);
                        }
                    }
                    var category = new CleanCategory(browser.Id, label, "", Targets(browser, profile, type).ToList())
                        { BlockingProcesses = new[] { browser.Process } };
                    var scan = CleanupEngine.Scan(category, 1);
                    if (scan.Incomplete) errors.Add($"{Path.GetFileName(profile.DataFolder)}: some folders were not read.");
                    if (scan.Files.Count == 0) continue;
                    var result = CleanupEngine.Clean(scan, 1, false, isRunning);
                    removed += result.Deleted;
                    skipped += result.Skipped;
                    if (result.Message != null) errors.Add(result.Message);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException
                                               or InvalidDataException)
                {
                    errors.Add($"{Path.GetFileName(profile.DataFolder)}: {ex.Message}");
                }
            }
            var text = $"Removed {removed:N0} record(s) or file(s)" + (skipped > 0 ? $", skipped {skipped:N0}" : "") + ".";
            if (errors.Count > 0) text += " " + string.Join(" ", errors);
            results.Add(new(label, errors.Count == 0 && skipped == 0, text));
        }
        return results;
    }

    public static string Label(BrowserData type) => type switch
    {
        BrowserData.Cache => "Cache",
        BrowserData.Cookies => "Cookies",
        BrowserData.History => "Browsing history",
        BrowserData.Downloads => "Download history",
        _ => "Saved sessions"
    };

    // ---- History databases. Each edit is one transaction: all of it happens, or none of it. ----

    private static long EditChromium(string database, BrowserData type)
    {
        var history = new[]
        {
            "keyword_search_terms", "visit_source", "segment_usage", "segments", "content_annotations",
            "context_annotations", "clusters_and_visits", "cluster_keywords", "cluster_visit_duplicates",
            "clusters", "visited_links", "visits", "urls"
        };
        var downloads = new[] { "downloads_slices", "downloads_url_chains", "downloads" };
        return Edit(database, (tables, run) =>
        {
            var required = type == BrowserData.History ? new[] { "urls", "visits" } : new[] { "downloads" };
            Require(tables, required);
            long count = 0;
            foreach (var table in (type == BrowserData.History ? history : downloads).Where(tables.Contains))
            {
                var rows = run($"DELETE FROM \"{table}\"");
                if (required.Contains(table)) count += rows;
            }
            return count;
        });
    }

    /// <summary>places.sqlite also holds bookmarks, so only history records are removed.</summary>
    private static long EditFirefox(string database, BrowserData type) => Edit(database, (tables, run) =>
    {
        Require(tables, "moz_places", "moz_historyvisits", "moz_bookmarks");
        const int downloadVisit = 7;
        var annotations = tables.Contains("moz_annos") && tables.Contains("moz_anno_attributes");
        const string downloadAttributes = "(SELECT id FROM moz_anno_attributes WHERE name LIKE 'downloads/%')";
        // A page can be removed only if nothing (visit, bookmark, keyword) still refers to it.
        const string unused =
            "foreign_count = 0 AND id NOT IN (SELECT fk FROM moz_bookmarks WHERE fk IS NOT NULL) " +
            "AND id NOT IN (SELECT place_id FROM moz_historyvisits WHERE place_id IS NOT NULL)";

        run("CREATE TEMP TABLE affected (id INTEGER PRIMARY KEY)");
        long count;
        if (type == BrowserData.Downloads)
        {
            run($"INSERT OR IGNORE INTO affected SELECT place_id FROM moz_historyvisits WHERE visit_type = {downloadVisit}");
            count = 0;
            if (annotations)
            {
                run($"INSERT OR IGNORE INTO affected SELECT place_id FROM moz_annos WHERE anno_attribute_id IN {downloadAttributes}");
                count += run($"DELETE FROM moz_annos WHERE anno_attribute_id IN {downloadAttributes}");
            }
            count += run($"DELETE FROM moz_historyvisits WHERE visit_type = {downloadVisit}");
        }
        else
        {
            run("INSERT OR IGNORE INTO affected SELECT id FROM moz_places WHERE visit_count > 0 " +
                $"OR id IN (SELECT place_id FROM moz_historyvisits WHERE visit_type <> {downloadVisit})");
            count = run($"DELETE FROM moz_historyvisits WHERE visit_type <> {downloadVisit}");
            if (tables.Contains("moz_inputhistory")) run("DELETE FROM moz_inputhistory");
            if (tables.Contains("moz_places_metadata")) run("DELETE FROM moz_places_metadata");
            // Pages that are also downloads stay until download history is cleared.
            if (annotations)
                run($"DELETE FROM affected WHERE id IN (SELECT place_id FROM moz_annos WHERE anno_attribute_id IN {downloadAttributes})");
        }
        if (annotations)
            run($"DELETE FROM moz_annos WHERE place_id IN (SELECT id FROM moz_places WHERE id IN (SELECT id FROM affected) AND {unused})");
        run($"DELETE FROM moz_places WHERE id IN (SELECT id FROM affected) AND {unused}");
        run("UPDATE moz_places SET visit_count = 0, last_visit_date = NULL WHERE id IN (SELECT id FROM affected) " +
            "AND id NOT IN (SELECT place_id FROM moz_historyvisits WHERE place_id IS NOT NULL)");
        run("DROP TABLE affected");
        return count;
    });

    private static void Require(HashSet<string> tables, params string[] names)
    {
        var missing = names.Where(n => !tables.Contains(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException("Unrecognized database layout, so nothing was changed.");
    }

    private static long Edit(string database, Func<HashSet<string>, Func<string, int>, long> change)
    {
        var settings = new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadWrite,   // never creates a file
            Pooling = false, ForeignKeys = false, DefaultTimeout = 3
        };
        using var connection = new SqliteConnection(settings.ToString());
        connection.Open();
        void Execute(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        Execute("PRAGMA secure_delete = ON");   // overwrite removed records inside the database
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        long result;
        using (var transaction = connection.BeginTransaction())
        {
            result = change(tables, sql =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                return command.ExecuteNonQuery();
            });
            transaction.Commit();
        }
        try { Execute("VACUUM"); Execute("PRAGMA wal_checkpoint(TRUNCATE)"); }
        catch (SqliteException) { }   // the records are already removed; compacting is optional
        return result;
    }
}
