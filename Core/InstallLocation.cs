namespace PCOptimizer.Core;

/// <summary>
/// Administrator rights are only requested for a copy in Program Files, where changing the program already
/// needs administrator rights. Anywhere else, another program could swap the files before approval.
/// </summary>
public static class InstallLocation
{
    public static bool IsProtected(string? programPath) => IsProtected(programPath,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    public static bool IsProtected(string? programPath, string programFiles)
    {
        if (string.IsNullOrEmpty(programPath) || programFiles.Length == 0) return false;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFiles));
            var full = Path.GetFullPath(programPath);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            // A link inside Program Files could point somewhere a normal user can change.
            for (var current = full; current != null && current.Length > root.Length;
                 current = Path.GetDirectoryName(current))
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
