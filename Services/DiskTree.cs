namespace PCOptimizer.Services;

public sealed class DiskNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsFolder { get; init; }
    public long Size { get; set; }
    public DiskNode? Parent { get; init; }
    public List<DiskNode> Children { get; } = new();
}

public sealed record DiskTreeResult(DiskNode Root, long Files, long UnreadableFolders, long SkippedLinks);

/// <summary>Read-only folder size scan. Sizes are file lengths, not space used on disk.</summary>
public static class DiskTree
{
    private const int FilesKeptPerFolder = 40;   // the rest are grouped to limit memory use

    public static DiskTreeResult Scan(string path, IProgress<long>? progress, CancellationToken token)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Folder not found: " + full);
        var root = new DiskNode { Name = full, Path = full, IsFolder = true };
        var folders = new List<DiskNode> { root };   // parents always come before their children
        long files = 0, unreadable = 0, links = 0;

        for (var i = 0; i < folders.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var folder = folders[i];
            var here = new List<(string Name, string Path, long Size)>();
            try
            {
                foreach (var entry in new DirectoryInfo(folder.Path).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry is DirectoryInfo)
                    { links++; continue; }
                    if (entry is DirectoryInfo dir)
                    {
                        var child = new DiskNode { Name = dir.Name, Path = dir.FullName, IsFolder = true, Parent = folder };
                        folder.Children.Add(child);
                        folders.Add(child);
                    }
                    else if (entry is FileInfo file)
                    {
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { links++; continue; }
                        here.Add((file.Name, file.FullName, file.Length));
                        if (++files % 2000 == 0) progress?.Report(files);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }

            here.Sort((a, b) => b.Size.CompareTo(a.Size));
            foreach (var f in here.Take(FilesKeptPerFolder))
                folder.Children.Add(new DiskNode { Name = f.Name, Path = f.Path, Size = f.Size, Parent = folder });
            if (here.Count > FilesKeptPerFolder)
                folder.Children.Add(new DiskNode
                {
                    Name = $"({here.Count - FilesKeptPerFolder:N0} smaller files)", Path = folder.Path, Parent = folder,
                    Size = here.Skip(FilesKeptPerFolder).Sum(f => f.Size)
                });
        }

        // Walking backwards means every child is totalled before its parent.
        for (var i = folders.Count - 1; i >= 0; i--)
        {
            folders[i].Size = folders[i].Children.Sum(c => c.Size);
            folders[i].Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
        return new DiskTreeResult(root, files, unreadable, links);
    }
}
