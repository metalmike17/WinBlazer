using System.Text;

namespace PCOptimizer.Services.RepairTools;

/// <summary>
/// Reads health data straight from physical drives. Only query and "read SMART" commands are sent.
/// ATA/SATA attributes need administrator rights; USB enclosures often do not pass them through.
/// </summary>
public static class SmartReader
{
    private const uint ReadWrite = 0xC0000000;   // required by the ATA pass-through, even for reading

    public static List<DriveHealth> ReadAll()
    {
        var result = new List<DriveHealth>();
        for (var number = 0; number < 32; number++)
            if (Read(number) is { } drive) result.Add(drive);
        return result;
    }

    private static DriveHealth? Read(int number)
    {
        var path = $@"\\.\PhysicalDrive{number}";
        using var query = StorageIo.Open(path);
        if (query.IsInvalid) return null;
        var descriptor = StorageIo.Property(query, 0, 1024);
        if (descriptor == null) return null;

        string Text(int offsetField)
        {
            var offset = BitConverter.ToInt32(descriptor, offsetField);
            if (offset <= 0 || offset >= descriptor.Length) return "";
            var end = Array.IndexOf(descriptor, (byte)0, offset);
            return Encoding.ASCII.GetString(descriptor, offset, (end < 0 ? descriptor.Length : end) - offset).Trim();
        }
        var bus = BitConverter.ToInt32(descriptor, 28);
        var model = $"{Text(12)} {Text(16)}".Trim();
        var drive = new DriveHealth
        {
            Number = number, Model = model == "" ? "Unknown drive" : model,
            Bus = bus switch { 3 => "ATA", 7 => "USB", 10 => "SAS", 11 => "SATA", 17 => "NVMe", 8 => "RAID", _ => $"Type {bus}" },
            Temperature = Temperature(query)
        };

        using var full = StorageIo.Open(path, ReadWrite);   // succeeds only when elevated
        var handle = full.IsInvalid ? query : full;
        if (bus == 17)
        {
            drive.Nvme = Nvme(handle);
            drive.Temperature = drive.Nvme?.Temperature ?? drive.Temperature;
        }
        else if (!full.IsInvalid && bus is 3 or 11 or 7)
        {
            if (Ata(full, number, 0xD0) is { } data)
            {
                drive.Attributes = SmartParser.ParseAta(data, Ata(full, number, 0xD1));
                drive.Temperature = SmartParser.AtaTemperature(drive.Attributes) ?? drive.Temperature;
            }
        }
        // IOCTL_STORAGE_PREDICT_FAILURE: the drive's own yes-or-no warning.
        if (!full.IsInvalid && StorageIo.Control(full, 0x002D1100, null, 516) is { } predict)
            drive.PredictsFailure = BitConverter.ToInt32(predict, 0) != 0;
        return drive;
    }

    private static double? Temperature(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        // Property 52: STORAGE_TEMPERATURE_DATA_DESCRIPTOR; the first sensor starts at offset 24.
        var data = StorageIo.Property(handle, 52, 512);
        if (data == null || BitConverter.ToUInt16(data, 12) == 0) return null;
        var celsius = BitConverter.ToInt16(data, 26);
        return celsius is > 0 and < 120 ? celsius : null;
    }

    private static NvmeHealth? Nvme(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        const int header = 8, protocol = 40, logSize = 512;
        // Property 50 is the device, 49 the adapter; drivers differ in which they answer.
        foreach (var property in new[] { 50, 49 })
        {
            var input = new byte[header + protocol + logSize];
            void Put(int at, int value) => BitConverter.GetBytes(value).CopyTo(input, at);
            Put(0, property);
            Put(8, 3);          // NVMe
            Put(12, 2);         // log page
            Put(16, 2);         // SMART / health log
            Put(24, protocol);  // data offset
            Put(28, logSize);
            if (StorageIo.Control(handle, StorageIo.QueryProperty, input, input.Length) is not { } output) continue;
            var offset = header + BitConverter.ToInt32(output, header + 16);
            if (offset < header + protocol || offset + logSize > output.Length) continue;
            if (SmartParser.ParseNvme(output.AsSpan(offset, logSize).ToArray()) is { } health) return health;
        }
        return null;
    }

    private static byte[]? Ata(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int number, byte feature)
    {
        // SENDCMDINPARAMS: buffer size, task file registers, then the drive number.
        var input = new byte[33];
        BitConverter.GetBytes(512).CopyTo(input, 0);
        input[4] = feature;   // D0 read attributes, D1 read thresholds
        input[5] = 1;
        input[6] = 1;
        input[7] = 0x4F;
        input[8] = 0xC2;
        input[9] = 0xA0;
        input[10] = 0xB0;     // SMART command
        input[12] = (byte)number;
        var output = StorageIo.Control(handle, 0x0007C088, input, 528);
        // Byte 4 is the driver error code.
        if (output == null || output[4] != 0) return null;
        var data = output.AsSpan(16, 512).ToArray();
        return data.Any(b => b != 0) ? data : null;
    }
}
