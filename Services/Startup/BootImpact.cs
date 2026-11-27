using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace PCOptimizer.Services.StartupTools;

public sealed record ProcessCost(double CpuMilliseconds, long DiskBytes);

public sealed record BootData(IReadOnlyDictionary<string, ProcessCost> Processes,
                              IReadOnlyDictionary<string, double> DelaySeconds, double? LastBootSeconds, string Source);

/// <summary>
/// Uses only measurements Windows already recorded. They are normally readable by administrators only.
/// Nothing is guessed: without data the impact is "Not measured".
/// </summary>
public static class BootImpact
{
    /// <summary>Thresholds Microsoft has documented for Task Manager's startup impact.</summary>
    public static string Rate(ProcessCost cost) =>
        cost.CpuMilliseconds > 1000 || cost.DiskBytes > 3 * 1048576 ? "High"
        : cost.CpuMilliseconds >= 300 || cost.DiskBytes >= 300 * 1024 ? "Medium" : "Low";

    public static Dictionary<string, ProcessCost> ParseStartupInfo(XDocument document)
    {
        var result = new Dictionary<string, ProcessCost>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in document.Descendants("Process"))
        {
            var name = (string?)process.Attribute("Name");
            if (string.IsNullOrEmpty(name) ||
                !double.TryParse((string?)process.Element("CpuUsage"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu) ||
                !long.TryParse((string?)process.Element("DiskUsage"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var disk))
                continue;
            result.TryGetValue(name, out var old);   // a program may start several processes
            result[name] = new ProcessCost(cpu / 1000 + (old?.CpuMilliseconds ?? 0), disk + (old?.DiskBytes ?? 0));
        }
        return result;
    }

    public static BootData Read()
    {
        var processes = new Dictionary<string, ProcessCost>(StringComparer.OrdinalIgnoreCase);
        var delays = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double? bootSeconds = null;
        var notes = new List<string>();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var folder = Path.Combine(Environment.SystemDirectory, "wdi", "LogFiles", "StartupInfo");
            var latest = new DirectoryInfo(folder).GetFiles(identity.User!.Value + "_StartupInfo*.xml")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest != null)
            {
                processes = ParseStartupInfo(XDocument.Load(latest.FullName));
                notes.Add($"startup measurements from {latest.LastWriteTime:g}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { }

        try
        {
            var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                                          "*[System[(EventID=100 or EventID=101)]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            DateTime? bootTime = null;
            for (var i = 0; i < 200; i++)
            {
                using var record = reader.ReadEvent();
                if (record == null) break;
                var data = XDocument.Parse(record.ToXml()).Descendants().Where(e => e.Name.LocalName == "Data")
                    .GroupBy(e => (string?)e.Attribute("Name") ?? "").ToDictionary(g => g.Key, g => g.First().Value);
                double? Number(string key) => data.TryGetValue(key, out var v) &&
                    double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n / 1000 : null;
                if (record.Id == 100 && bootSeconds == null)
                {
                    bootSeconds = Number("BootTime");
                    bootTime = record.TimeCreated;
                }
                else if (record.Id == 101 && data.TryGetValue("Name", out var name) &&
                         Number("DegradationTime") is { } delay && !delays.ContainsKey(name) &&
                         // Only delays Windows reported within the last 30 days.
                         record.TimeCreated > DateTime.Now.AddDays(-30))
                    delays[name] = delay;
            }
            if (bootSeconds != null) notes.Add($"last recorded boot {bootSeconds:N0} s on {bootTime:d}");
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or SecurityException
                                       or XmlException) { }

        return new BootData(processes, delays, bootSeconds,
            notes.Count > 0 ? "Windows data: " + string.Join("; ", notes) + "."
                            : "No Windows boot measurements could be read (usually needs administrator rights).");
    }

    public static string For(StartupEntry entry, BootData data)
    {
        if (entry.ProgramPath == null) return "Not measured";
        // A shared host program's totals cannot be assigned to one entry.
        if (entry.Publisher.StartsWith("Unknown (runs through", StringComparison.Ordinal)) return "Not measured";
        var text = data.Processes.TryGetValue(entry.ProgramPath, out var cost)
            ? $"{Rate(cost)} ({cost.CpuMilliseconds / 1000:N1} s CPU, {CleanupEngine.Size(cost.DiskBytes)} disk)"
            : "Not measured";
        if (data.DelaySeconds.TryGetValue(Path.GetFileName(entry.ProgramPath), out var delay))
            text += $". Windows reported about {delay:N1} s boot delay recently";
        return text;
    }
}
