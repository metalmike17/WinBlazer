using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace PCOptimizer.Services;

public enum AutoFrequency { Daily, Weekly, Monthly }

public sealed record AutoSchedule(AutoFrequency Frequency, TimeOnly Time, DayOfWeek WeekDay, int MonthDay,
                                  CleanMode Mode, bool IncludeRecycleBin)
{
    public string Arguments => $"--auto-clean {Mode.ToString().ToLowerInvariant()}" +
        (Mode == CleanMode.Quick && IncludeRecycleBin ? " --include-recycle-bin" : "");
}

/// <summary>Accepts only the exact arguments this app creates. Anything else cleans nothing.</summary>
public static class AutoCleanArguments
{
    public static bool TryParse(string[] args, out CleanMode mode, out bool recycle)
    {
        (mode, recycle) = (CleanMode.Quick, false);
        if (args.Length is < 2 or > 3 || args[0] != "--auto-clean") return false;
        if (args[1] == "deep") mode = CleanMode.Deep;
        else if (args[1] != "quick") return false;
        if (args.Length == 3)
        {
            if (args[2] != "--include-recycle-bin" || mode != CleanMode.Quick) return false;
            recycle = true;
        }
        return true;
    }
}

/// <summary>Manages one task for the current user, always without elevated rights.</summary>
public sealed class TaskSchedulerService
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly string _taskName;
    private readonly string _program;

    public TaskSchedulerService(string? taskName = null, string? program = null)
    {
        // The user name keeps two accounts on one PC from sharing a task.
        _taskName = taskName ?? $"PCOptimizer Auto Clean ({Environment.UserName})";
        _program = program ?? Environment.ProcessPath!;
    }

    public static XDocument BuildXml(AutoSchedule schedule, string program, string userSid, DateTime start)
    {
        XElement E(string name, params object[] content) => new(Ns + name, content);
        var when = schedule.Frequency switch
        {
            AutoFrequency.Daily => E("ScheduleByDay", E("DaysInterval", 1)),
            AutoFrequency.Weekly => E("ScheduleByWeek", E("DaysOfWeek", E(schedule.WeekDay.ToString())),
                                      E("WeeksInterval", 1)),
            _ => E("ScheduleByMonth",
                   E("DaysOfMonth", E("Day", Math.Clamp(schedule.MonthDay, 1, 28))),   // exists in every month
                   E("Months", CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Where(m => m != "")
                       .Select(m => E(m)).ToArray<object>()))
        };
        var boundary = start.Date + schedule.Time.ToTimeSpan();
        return new XDocument(new XDeclaration("1.0", "UTF-16", null),
            E("Task", new XAttribute("version", "1.2"),
                E("RegistrationInfo", E("Description", "WinBlazer automatic cleanup.")),
                E("Triggers", E("CalendarTrigger",
                    E("StartBoundary", boundary.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
                    E("Enabled", "true"), when)),
                E("Principals", E("Principal", new XAttribute("id", "Author"),
                    E("UserId", userSid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
                E("Settings",
                    E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                    E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("Enabled", "true"),
                    E("ExecutionTimeLimit", "PT1H")),
                E("Actions", new XAttribute("Context", "Author"),
                    E("Exec", E("Command", program), E("Arguments", schedule.Arguments)))));
    }

    public void Save(AutoSchedule schedule, DateTime? start = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        // If today's time has passed, start tomorrow so saving never triggers an immediate run.
        var first = start ?? (TimeOnly.FromDateTime(DateTime.Now) < schedule.Time ? DateTime.Today : DateTime.Today.AddDays(1));
        var xml = BuildXml(schedule, _program, identity.User!.Value, first);
        var file = Path.Combine(Path.GetTempPath(), $"pcopt-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, xml.Declaration + Environment.NewLine + xml, Encoding.Unicode);
            var (code, _, error) = Schtasks("/Create", "/TN", _taskName, "/XML", file, "/F");
            if (code != 0) throw new InvalidOperationException("Task Scheduler refused the task. " + error.Trim());
        }
        finally { File.Delete(file); }
    }

    /// <summary>The task's definition, or null if there is no task.</summary>
    public XDocument? Read()
    {
        var (code, output, _) = Schtasks("/Query", "/TN", _taskName, "/XML");
        if (code != 0) return null;
        return XDocument.Parse(output.Trim());
    }

    public static string Describe(XDocument task)
    {
        string? Value(string name) => task.Descendants(Ns + name).FirstOrDefault()?.Value;
        var how = task.Descendants(Ns + "ScheduleByDay").Any() ? "Daily"
            : task.Descendants(Ns + "ScheduleByWeek").Any()
                ? "Weekly on " + task.Descendants(Ns + "DaysOfWeek").First().Elements().FirstOrDefault()?.Name.LocalName
            : task.Descendants(Ns + "ScheduleByMonth").Any() ? "Monthly on day " + Value("Day")
            : "Custom schedule";
        var time = DateTime.TryParse(Value("StartBoundary"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("HH:mm") : "?";
        var arguments = Value("Arguments") ?? "";
        var mode = arguments.Contains("deep") ? "Deep Clean" : "Quick Clean";
        if (arguments.Contains("--include-recycle-bin")) mode += " including Recycle Bin";
        var off = task.Descendants(Ns + "Settings").Elements(Ns + "Enabled").FirstOrDefault()?.Value == "false";
        return $"{how} at {time}: {mode}." + (off ? " (Disabled in Task Scheduler.)" : "");
    }

    /// <summary>True if the task starts a different copy of the program (for example after it was moved).</summary>
    public bool PointsElsewhere(XDocument task) => !string.Equals(
        task.Descendants(Ns + "Command").FirstOrDefault()?.Value, _program, StringComparison.OrdinalIgnoreCase);

    public void Delete()
    {
        if (Read() == null) return;
        var (code, _, error) = Schtasks("/Delete", "/TN", _taskName, "/F");
        if (code != 0) throw new InvalidOperationException("Could not remove the task. " + error.Trim());
    }

    private static (int Code, string Output, string Error) Schtasks(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            throw new InvalidOperationException("Task Scheduler did not respond.");
        }
        return (process.ExitCode, output.Result, error.Result);
    }
}
