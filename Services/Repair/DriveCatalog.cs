using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCOptimizer.Services.RepairTools;

public enum MediaKind { Unknown, Ssd, Hdd }

public sealed record VolumeInfo(char Letter, string Label, string FileSystem, long Size, long Free,
                                MediaKind Media, int? DiskNumber, bool IsSystem)
{
    public string Describe() => $"{Letter}:  {(Label == "" ? "" : Label + "  ")}({FileSystem}, " +
        $"{CleanupEngine.Size(Free)} free of {CleanupEngine.Size(Size)}, " +
        $"{(Media == MediaKind.Ssd ? "SSD" : Media == MediaKind.Hdd ? "hard disk" : "type unknown")}" +
        $"{(IsSystem ? ", Windows drive" : "")})";
}

/// <summary>Low-level storage queries. Handles opened with no access rights need no elevation.</summary>
internal static class StorageIo
{
    public const uint QueryProperty = 0x002D1400;

    public static SafeFileHandle Open(string path, uint access = 0) =>
        CreateFile(path, access, 3 /* share read and write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero);

    public static byte[]? Control(SafeFileHandle handle, uint code, byte[]? input, int outputSize)
    {
        var output = new byte[outputSize];
        return DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out _, IntPtr.Zero)
            ? output : null;
    }

    /// <summary>STORAGE_PROPERTY_QUERY for a standard query of one property.</summary>
    public static byte[]? Property(SafeFileHandle handle, int property, int outputSize)
    {
        var input = new byte[12];
        BitConverter.GetBytes(property).CopyTo(input, 0);
        return Control(handle, QueryProperty, input, outputSize);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize,
        byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

public static class DriveCatalog
{
    public static List<VolumeInfo> Volumes()
    {
        var system = char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)![0]);
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                var letter = char.ToUpperInvariant(drive.Name[0]);
                var (media, disk) = Inspect(letter);
                result.Add(new VolumeInfo(letter, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize,
                    drive.TotalFreeSpace, media, disk, letter == system));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static (MediaKind, int?) Inspect(char letter)
    {
        using var handle = StorageIo.Open($@"\\.\{letter}:");
        if (handle.IsInvalid) return (MediaKind.Unknown, null);
        // VOLUME_DISK_EXTENTS: count, padding, then the disk number. Spanned volumes fail with this size.
        var extents = StorageIo.Control(handle, 0x00560000, null, 32);
        int? disk = extents != null && BitConverter.ToInt32(extents, 0) == 1 ? BitConverter.ToInt32(extents, 8) : null;
        // Property 7: seek penalty. A drive without one is solid state.
        var seek = StorageIo.Property(handle, 7, 12);
        var media = seek == null || BitConverter.ToInt32(seek, 4) < 9 ? MediaKind.Unknown
            : seek[8] != 0 ? MediaKind.Hdd : MediaKind.Ssd;
        return (media, disk);
    }
}
