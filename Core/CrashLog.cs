using System.Reflection;

namespace PCOptimizer.Core;

/// <summary>Saves unexpected errors to a local file. Nothing is sent anywhere. Logging never throws.</summary>
public static class CrashLog
{
    private const long MaximumBytes = 1_000_000;
    private static readonly object Gate = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCOptimizerNet", "crash.log");

    /// <summary>False if the log could not be written.</summary>
    public static bool Write(string source, Exception? error, string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            var version = typeof(CrashLog).Assembly.GetName().Version;
            var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {source}\r\n" +
                        $"WinBlazer {version}, {Environment.OSVersion}, .NET {Environment.Version}\r\n" +
                        $"{error?.ToString() ?? "No error details were given."}\r\n\r\n";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // One older file is kept, so the log cannot grow without limit.
                if (File.Exists(path) && new FileInfo(path).Length > MaximumBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, entry);
            }
            return true;
        }
        catch (Exception) { return false; }   // a failing logger must not hide the original error
    }
}
