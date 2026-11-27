using System.Diagnostics;
using System.Threading;

namespace PCOptimizer.Core;

/// <summary>
/// Allows one open window per Windows session. The first copy holds a named handle for as long as it runs.
/// Windowless modes (auto-clean, Game Mode recovery, self-test) do not take part.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Passed to the administrator copy, so it waits for the old copy to close.</summary>
    public const string RestartArgument = "--restart";
    private const string DefaultName = @"Local\WinBlazer.Window";

    private readonly Mutex _marker;
    private readonly EventWaitHandle _show;
    private RegisteredWaitHandle? _listening;

    private SingleInstance(Mutex marker, EventWaitHandle show) => (_marker, _show) = (marker, show);

    /// <summary>Null if another copy is still open after waiting.</summary>
    public static SingleInstance? TryAcquire(TimeSpan wait, string name = DefaultName)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // Only existence matters, so the mutex is never owned and any thread may dispose it.
                var marker = new Mutex(false, name, out var createdNew);
                if (createdNew)
                    return new SingleInstance(marker,
                        new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show"));
                marker.Dispose();
            }
            // An administrator copy's handles may refuse access to a normal one. It still counts as open.
            catch (UnauthorizedAccessException) { }
            if (waited.Elapsed >= wait) return null;
            Thread.Sleep(200);
        }
    }

    /// <summary>Calls show (on a pool thread) each time another copy asks for this one.</summary>
    public void Listen(Action show) =>
        _listening = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => show(), null, Timeout.Infinite, false);

    /// <summary>False if the open copy could not be reached, for example because it runs as administrator.</summary>
    public static bool AskOpenCopyToShow(string name = DefaultName)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(name + ".Show", out var show)) return false;
            using (show) return show.Set();
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Starts an administrator copy. Throws Win32Exception if the permission prompt is cancelled, or if this
    /// copy is not installed in Program Files.
    /// </summary>
    public static void StartElevatedCopy()
    {
        if (!InstallLocation.IsProtected(Environment.ProcessPath))
        {
            System.Windows.MessageBox.Show(
                "For your safety, only the installed copy of WinBlazer can restart as administrator. This copy is " +
                "in a folder that other programs can change.\n\nInstall it with scripts\\Install.ps1, then open " +
                "WinBlazer from the Start menu or desktop.", "WinBlazer");
            throw new System.ComponentModel.Win32Exception("This copy is not installed in Program Files.");
        }
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, RestartArgument)
        { UseShellExecute = true, Verb = "runas" });
    }

    public void Dispose()
    {
        _listening?.Unregister(null);
        _show.Dispose();
        _marker.Dispose();
    }
}
