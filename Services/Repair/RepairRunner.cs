using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PCOptimizer.Services.RepairTools;

/// <summary>
/// Runs one job at a time, streaming its output to the caller and a log file. Events arrive on a
/// background thread.
/// </summary>
public sealed class RepairRunner
{
    private readonly object _gate = new();
    private readonly StringBuilder _visible = new();
    private CancellationTokenSource? _cancel;

    public string LogFolder { get; }
    public RepairJob? Current { get; private set; }
    public double? Percent { get; private set; }
    public string LastLine { get; private set; } = "";
    public DateTime Started { get; private set; }
    public string? LogPath { get; private set; }
    public bool IsRunning => Current != null;
    public Dictionary<string, (DateTime When, RepairResult Result)> Results { get; } = new();
    public event Action? Changed;

    public RepairRunner(string logFolder) => LogFolder = logFolder;

    /// <summary>The most recent output lines (the log file has all of them).</summary>
    public string LogText { get { lock (_gate) return _visible.ToString(); } }

    public void Cancel()
    {
        if (Current?.CanCancel == true) _cancel?.Cancel();
    }

    public async Task<RepairResult> RunAsync(RepairJob job)
    {
        lock (_gate)
        {
            if (Current != null) throw new InvalidOperationException("Another tool is already running.");
            Current = job;
            _visible.Clear();
        }
        (Percent, LastLine, Started) = (null, "Starting...", DateTime.Now);
        _cancel = new CancellationTokenSource();
        StreamWriter? writer = null;
        try
        {
            Directory.CreateDirectory(LogFolder);
            LogPath = Path.Combine(LogFolder, $"{Started:yyyyMMdd-HHmmss-fff}-{job.Kind}.log");
            // If the log cannot be created, the tool is not started.
            writer = new StreamWriter(LogPath, false, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine($"{job.Title}  {Started:u}");
            writer.WriteLine($"Command: {job.Program} {string.Join(' ', job.Arguments)}");
            Changed?.Invoke();
            var result = await ExecuteAsync(job, writer, _cancel.Token);
            writer.WriteLine($"Result: {result.Verdict}: {result.Summary}");
            Results[job.Title] = (DateTime.Now, result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            var result = new RepairResult(Verdict.Failed, ex.Message);
            Results[job.Title] = (DateTime.Now, result);
            return result;
        }
        finally
        {
            writer?.Dispose();
            _cancel.Dispose();
            _cancel = null;
            Current = null;
            Changed?.Invoke();
        }
    }

    private async Task<RepairResult> ExecuteAsync(RepairJob job, StreamWriter writer, CancellationToken token)
    {
        var start = new ProcessStartInfo(job.Program)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = job.Encoding, StandardErrorEncoding = job.Encoding
        };
        foreach (var argument in job.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var all = new StringBuilder();

        void Add(string line)
        {
            line = line.Replace("\0", "").TrimEnd();
            if (line.Length == 0) return;
            lock (_gate)
            {
                if (RepairJobs.Progress(job.Kind, line) is { } percent) Percent = percent;
                LastLine = line;
                if (!RepairJobs.IsProgressOnly(line))
                {
                    all.AppendLine(line);
                    try { writer.WriteLine(line); }
                    catch (IOException) { }   // a full disk must not stop reading the tool's output
                    _visible.AppendLine(line);
                    if (_visible.Length > 60000) _visible.Remove(0, _visible.Length - 40000);
                }
            }
            Changed?.Invoke();
        }

        // Progress is redrawn with carriage returns, so both line endings are treated as breaks.
        async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[512];
            var line = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] is '\r' or '\n') { Add(line.ToString()); line.Clear(); }
                    else line.Append(buffer[i]);
                }
            Add(line.ToString());
        }

        var output = PumpAsync(process.StandardOutput);
        var error = PumpAsync(process.StandardError);
        try
        {
            if (job.Input != null) await process.StandardInput.WriteAsync(job.Input);
            process.StandardInput.Close();
        }
        catch (IOException) { }   // the tool exited without asking anything

        var cancelled = false;
        // Only read-only jobs are ever given a way to be stopped.
        using (token.Register(() =>
               {
                   if (!job.CanCancel) return;
                   try { process.Kill(entireProcessTree: true); cancelled = true; }
                   catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
               }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
        }
        await Task.WhenAll(output, error);
        if (cancelled) return new(Verdict.Cancelled, "Cancelled.");
        writer.WriteLine($"Exit code: {process.ExitCode}");
        string text;
        lock (_gate) text = all.ToString();
        return RepairJobs.Interpret(job, process.ExitCode, text);
    }
}
