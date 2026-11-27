using PCOptimizer.Core;
using PCOptimizer.Services.Bloatware;
using PCOptimizer.Services.Tweaks;

namespace PCOptimizer.Services;

public sealed record SearchEntry(string Title, string Subtitle, string ModuleId, string? Section = null,
                                 string Keywords = "");

/// <summary>Searches pages, their features, settings, tweaks and removable apps. It never runs anything.</summary>
public static class SearchIndex
{
    private static readonly SearchEntry[] Features =
    {
        new("Optimization score", "Dashboard", "dashboard", null, "health rating fix"),
        new("Per-core CPU, GPU and VRAM charts", "System Monitor", "system-monitor", "performance", "usage temperature graphics"),
        new("Disk and network throughput", "System Monitor", "system-monitor", "performance", "speed read write io"),
        new("Process Manager", "System Monitor", "system-monitor", "processes", "end task kill running memory usage"),
        new("Activate Game Mode", "Game Mode", "game-mode", null, "gaming fps power plan gamedvr game bar"),
        new("One-Click Optimize", "System Tweaks", "system-tweaks", null, "speed safe"),
        new("Disk Analyzer", "Disk Space Cleaner", "disk-cleaner", "analyzer", "treemap large folders files space"),
        new("Recycle Bin and temporary files", "Disk Space Cleaner", "disk-cleaner", "cleaner", "junk windows.old"),
        new("SFC, DISM, chkdsk and defrag", "Disk Repair", "disk-repair", "tools", "scannow trim optimize repair"),
        new("Drive Health (SMART)", "Disk Repair", "disk-repair", "health", "temperature sectors ssd hdd"),
        new("Check for driver updates", "Driver Manager", "driver-manager", null, "outdated nvidia missing"),
        new("Driver Backup", "Driver Manager", "driver-manager", null, "export restore"),
        new("Quick Clean and Deep Clean", "Temp & Cache", "temp-cache-cleaner", null, "junk cache logs"),
        new("Automatic cleaning schedule", "Temp & Cache", "temp-cache-cleaner", null, "task scheduler auto"),
        new("Disable startup programs", "Startup Manager", "startup-manager", null, "boot autorun tasks"),
        new("Browser history, cache and cookies", "Browser Cleaner", "browser-cleaner", "data", "chrome edge firefox brave privacy"),
        new("Tracks Eraser", "Browser Cleaner", "browser-cleaner", "eraser", "privacy recent clipboard"),
        new("Debloat Preset", "Bloatware Remover", "bloatware-remover", null, "uninstall preinstalled apps debload"),
        new("Ping test", "Network Tools", "network-tools", "Ping", "latency jitter packet loss"),
        new("DNS speed test and switching", "Network Tools", "network-tools", "DNS", "cloudflare google quad9 opendns adguard"),
        new("TCP/IP settings and Winsock reset", "Network Tools", "network-tools", "TCP/IP", "offload autotuning"),
        new("Download speed test", "Network Tools", "network-tools", "Speed", "internet bandwidth"),
        new("Network info: IP, gateway, DNS", "Network Tools", "network-tools", "Ping", "address adapter"),
        new("Smart Standby Cleaner", "Memory Optimizer", "memory-optimizer", null, "ram purge threshold"),
        new("Background App Limiter", "Memory Optimizer", "memory-optimizer", null, "priority efficiency mode"),
        new("Broken shortcuts", "Shortcut Fixer", "shortcut-fixer", null, "lnk start menu desktop"),
        new("Registry backup and restore", "Registry Cleaner", "registry-cleaner", null, "reg undo"),
        new("Past actions and space freed", "History", "history", null, "log timestamps"),
        new("Show or hide modules", "Settings", "settings", null, "sidebar"),
        new("Automatic scans", "Settings", "settings", null, "schedule daily weekly"),
        new("Minimum age of temp files", "Settings", "settings", null, "cleanup preferences days"),
        new("Minimize to the system tray", "Settings", "settings", null, "behavior close"),
        new("Notifications", "Settings", "settings", null, "toast notify after cleanup scan")
    };

    public static List<SearchEntry> Build(ModuleRegistry modules)
    {
        var entries = modules.All.Select(m => new SearchEntry(m.Title, m.Description, m.Id)).ToList();
        var ids = modules.All.Select(m => m.Id).ToHashSet();
        entries.AddRange(Features);
        entries.AddRange(TweakCatalog.Create().Select(t =>
            new SearchEntry(t.Name, "Tweak • " + t.Category, "system-tweaks", t.Id, t.Description)));
        entries.AddRange(AppCatalog.All.Select(a =>
            new SearchEntry(a.Title, "Removable app", "bloatware-remover", null, "uninstall " + a.PackageName)));
        return entries.Where(e => ids.Contains(e.ModuleId)).ToList();
    }

    /// <summary>Every word must appear. Title matches rank above description and keyword matches.</summary>
    public static List<SearchEntry> Find(IReadOnlyList<SearchEntry> entries, string query, int limit = 12)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return entries.Where(e => e.Section == null && e.Keywords == "").Take(20).ToList();
        int Rank(SearchEntry e)
        {
            var total = 0;
            foreach (var word in words)
            {
                const StringComparison any = StringComparison.OrdinalIgnoreCase;
                if (e.Title.StartsWith(word, any)) total += 100;
                else if (e.Title.Contains(" " + word, any)) total += 60;
                else if (e.Title.Contains(word, any)) total += 40;
                else if (e.Subtitle.Contains(word, any)) total += 15;
                else if (e.Keywords.Contains(word, any)) total += 10;
                else return 0;
            }
            return total + (e.Section == null && e.Keywords == "" ? 5 : 0);   // a page itself wins ties
        }
        return entries.Select(e => (Entry: e, Rank: Rank(e))).Where(x => x.Rank > 0)
            .OrderByDescending(x => x.Rank).ThenBy(x => x.Entry.Title.Length).Take(limit)
            .Select(x => x.Entry).ToList();
    }
}
