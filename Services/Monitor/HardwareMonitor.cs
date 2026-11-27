using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services.Monitor;

/// <summary>Memory in MB. Null values were not reported.</summary>
public sealed record GpuReading(string Name, double? Load, double? MemoryUsedMb, double? MemoryTotalMb, double? Temperature);

public sealed record DiskReading(string Name, double Read, double Write);

/// <summary>Null or empty values are not available (rates need two samples).</summary>
public sealed record HardwareSample(double[] Cores, GpuReading? Gpu, long MemoryTotal, long MemoryUsed,
    List<DiskReading> Disks, double? Download, double? Upload);

/// <summary>Call Sample from one thread at a time.</summary>
public sealed class HardwareMonitor : IDisposable
{
    private IntPtr _query;
    private readonly IntPtr _cores, _reads, _writes;
    private readonly string? _nvidiaSmi;
    private Dictionary<string, (long Down, long Up)> _net = new();
    private readonly Stopwatch _netClock = new();

    public HardwareMonitor()
    {
        _nvidiaSmi = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        }.FirstOrDefault(File.Exists);
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) return;
        IntPtr Add(string path) => PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;
        _cores = Add(@"\Processor Information(*)\% Processor Time");
        _reads = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _writes = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        PdhCollectQueryData(_query);
    }

    public HardwareSample Sample()
    {
        var collected = _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;
        var cores = collected ? OrderCores(Values(_cores)) : Array.Empty<double>();
        var disks = new List<DiskReading>();
        if (collected)
        {
            static double Zero(double v) => double.IsNaN(v) ? 0 : v;
            var writes = Values(_writes).ToDictionary(v => v.Name, v => Zero(v.Value));
            disks = Values(_reads).Where(v => v.Name != "_Total")
                .Select(v => new DiskReading(v.Name, Zero(v.Value), writes.GetValueOrDefault(v.Name)))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var haveMemory = GlobalMemoryStatusEx(ref memory);
        var (down, up) = ReadNetwork();
        return new HardwareSample(cores, ReadGpu(), haveMemory ? (long)memory.TotalPhys : 0,
            haveMemory ? (long)(memory.TotalPhys - memory.AvailPhys) : 0, disks, down, up);
    }

    /// <summary>Instances are named "group,core"; totals are left out.</summary>
    public static double[] OrderCores(IEnumerable<(string Name, double Value)> values)
    {
        var cores = new List<(int Group, int Core, double Value)>();
        foreach (var (name, value) in values)
        {
            var parts = name.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out var group) && int.TryParse(parts[1], out var core))
                cores.Add((group, core, Math.Clamp(value, 0, 100)));
        }
        return cores.OrderBy(c => c.Group).ThenBy(c => c.Core).Select(c => c.Value).ToArray();
    }

    public static GpuReading? ParseGpu(string line)
    {
        var parts = line.Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length != 5 || parts[0].Length == 0) return null;
        double? Number(int i) =>
            double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        return new GpuReading(parts[0], Number(1), Number(2), Number(3), Number(4));
    }

    private GpuReading? ReadGpu()
    {
        if (_nvidiaSmi == null) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_nvidiaSmi,
                "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { p.Kill(); return null; }   // our own query tool
            return ParseGpu(read.Result.Split('\n')[0]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { return null; }
    }

    private (double?, double?) ReadNetwork()
    {
        try
        {
            var now = new Dictionary<string, (long Down, long Up)>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Adapters with a gateway only, so virtual adapters are not counted twice.
                if (n.OperationalStatus != OperationalStatus.Up ||
                    n.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    n.GetIPProperties().GatewayAddresses.Count == 0) continue;
                var s = n.GetIPStatistics();
                now[n.Id] = (s.BytesReceived, s.BytesSent);
            }
            var seconds = _netClock.Elapsed.TotalSeconds;
            _netClock.Restart();
            long down = 0, up = 0;
            var matched = false;
            foreach (var (id, v) in now)
                if (_net.TryGetValue(id, out var old))
                {
                    matched = true;
                    down += Math.Max(0, v.Down - old.Down);
                    up += Math.Max(0, v.Up - old.Up);
                }
            _net = now;
            return matched && seconds > 0 ? (down / seconds, up / seconds) : (null, null);
        }
        catch (NetworkInformationException) { return (null, null); }
    }

    private static List<(string Name, double Value)> Values(IntPtr counter)
    {
        var result = new List<(string, double)>();
        if (counter == IntPtr.Zero) return result;
        const uint formatDouble = 0x200, noCap = 0x8000, moreData = 0x800007D2;
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, formatDouble | noCap, ref size, out _, IntPtr.Zero) != moreData)
            return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, formatDouble | noCap, ref size, out var count, buffer) != 0)
                return result;
            var itemSize = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * itemSize);
                // An invalid reading keeps its place as NaN, so cores never shift position.
                result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Status <= 1 ? item.Value : double.NaN));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public IntPtr Name; public uint Status; public double Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
