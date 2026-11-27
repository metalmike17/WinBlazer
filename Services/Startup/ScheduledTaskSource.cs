using System.Runtime.InteropServices;

namespace PCOptimizer.Services.StartupTools;

/// <summary>
/// Uses the Task Scheduler COM interface. Without administrator rights Windows does not show every task.
/// </summary>
public sealed class ScheduledTaskSource : ITaskSource
{
    private const int IncludeHidden = 1, BootTrigger = 8, LogonTrigger = 9, ExecAction = 0;

    public int Unreadable { get; private set; }

    private static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        return service;
    }

    public IReadOnlyList<StartupEntry> Read()
    {
        Unreadable = 0;
        var result = new List<StartupEntry>();
        dynamic service = Connect();
        var folders = new Stack<object>();
        folders.Push(service.GetFolder("\\"));
        while (folders.Count > 0)
        {
            dynamic folder = folders.Pop();
            try { foreach (var child in folder.GetFolders(0)) folders.Push((object)child); }
            catch (Exception ex) when (IsAccessProblem(ex)) { Unreadable++; }
            try
            {
                foreach (var task in folder.GetTasks(IncludeHidden))
                {
                    try
                    {
                        StartupEntry? entry = ToEntry(task);
                        if (entry != null) result.Add(entry);
                    }
                    catch (Exception ex) when (IsAccessProblem(ex)) { Unreadable++; }
                }
            }
            catch (Exception ex) when (IsAccessProblem(ex)) { Unreadable++; }
        }
        return result;
    }

    private static StartupEntry? ToEntry(dynamic task)
    {
        dynamic definition = task.Definition;
        var starts = new List<string>();
        foreach (var trigger in definition.Triggers)
        {
            if (!(bool)trigger.Enabled) continue;
            int type = trigger.Type;
            if (type == BootTrigger) starts.Add("at boot");
            if (type == LogonTrigger) starts.Add("at sign-in");
        }
        if (starts.Count == 0) return null;

        var commands = new List<string>();
        string? program = null;
        foreach (var action in definition.Actions)
        {
            if ((int)action.Type == ExecAction)
            {
                string path = action.Path ?? "";
                string arguments = action.Arguments ?? "";
                program ??= StartupCatalog.ResolveProgram("\"" + path.Trim('"') + "\"");
                commands.Add($"{path} {arguments}".Trim());
            }
            else commands.Add("Built-in task action (no command line)");
        }

        string taskPath = task.Path;
        var windows = taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);
        return new StartupEntry
        {
            Id = "task|" + taskPath, Name = (string)task.Name, Source = StartupSource.ScheduledTask,
            Location = $"Scheduled task {taskPath} ({string.Join(", ", starts.Distinct())})",
            Command = commands.Count == 0 ? "(no action)" : string.Join("  ;  ", commands),
            ProgramPath = program, Enabled = (bool)task.Enabled, Hidden = (bool)definition.Settings.Hidden,
            TaskPath = taskPath,
            LockReason = windows ? "Windows or Microsoft task: view only." : null
        };
    }

    public void SetEnabled(string taskPath, bool enabled)
    {
        try
        {
            dynamic task = Connect().GetFolder("\\").GetTask(taskPath);
            task.Enabled = enabled;
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            throw new IOException("Task Scheduler refused the change. Administrator rights may be needed.", ex);
        }
    }

    private static bool IsAccessProblem(Exception ex) =>
        ex is COMException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException;
}
