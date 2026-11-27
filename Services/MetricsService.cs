using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services;

/// <summary>A null value means the reading is not available.</summary>
public sealed record Metrics(double? Cpu, double? Ram, string RamDetail, double? Gpu, string GpuDetail,
                             double? Disk, double? NetworkBytesPerSecond, string NetworkDetail);

/// <summary>Call Sample from one background thread at a time.</summary>
public sealed class MetricsService : IDisposable
{
    private ulong _idle, _kernel, _user;
    private bool _haveCpu;
    private Dictionary<string, (long Down, long Up)> _net = new();
    private readonly Stopwatch _netClock = new();
    private IntPtr _query, _diskCounter;
    private readonly string? _nvidiaSmi;

    public MetricsService()
    {
        // Only trusted system locations are searched, never the current folder or PATH.
        _nvidiaSmi = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        }.FirstOrDefault(File.Exists);

        if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0 &&
            PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _diskCounter) == 0)
            PdhCollectQueryData(_query);
        else _diskCounter = IntPtr.Zero;
    }

    public Metrics Sample()
    {
        var (ram, ramDetail) = ReadRam();
        var (gpu, gpuDetail) = ReadGpu();
        var (net, netDetail) = ReadNetwork();
        return new Metrics(ReadCpu(), ram, ramDetail, gpu, gpuDetail, ReadDisk(), net, netDetail);
    }

    private double? ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var total = (kernel - _kernel) + (user - _user);   // kernel time includes idle time
        double? result = null;
        if (_haveCpu && total > 0) result = Math.Clamp(100.0 * (total - (double)(idle - _idle)) / total, 0, 100);
        (_idle, _kernel, _user, _haveCpu) = (idle, kernel, user, true);
        return result;
    }

    private static (double?, string) ReadRam()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref m)) return (null, "Unavailable");
        const double gb = 1024 * 1024 * 1024;
        var used = m.TotalPhys - m.AvailPhys;
        return (100.0 * used / m.TotalPhys, $"{used / gb:N1} of {m.TotalPhys / gb:N1} GB");
    }

    private (double?, string) ReadGpu()
    {
        if (_nvidiaSmi == null) return (null, "NVIDIA GPUs only");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(_nvidiaSmi,
                "--query-gpu=utilization.gpu,temperature.gpu --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { p.Kill(); return (null, "No response"); }
            var parts = read.Result.Split('\n')[0].Split(',');
            if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var use))
                return (null, "Unavailable");
            var detail = "First NVIDIA GPU";
            if (parts.Length > 1 && double.TryParse(parts[1].Trim(), NumberStyles.Float,
                                                    CultureInfo.InvariantCulture, out var temperature))
                detail = $"{temperature:N0} °C (first NVIDIA GPU)";
            return (use, detail);
        }
        catch (Exception) { return (null, "Unavailable"); }
    }

    private double? ReadDisk()
    {
        if (_diskCounter == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        const uint formatDouble = 0x00000200;
        if (PdhGetFormattedCounterValue(_diskCounter, formatDouble, IntPtr.Zero, out var v) != 0 || v.Status > 1)
            return null;
        return Math.Clamp(100 - v.Value, 0, 100);
    }

    private (double?, string) ReadNetwork()
    {
        try
        {
            var now = new Dictionary<string, (long, long)>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Adapters with a gateway only, which skips most virtual duplicates.
                if (n.OperationalStatus != OperationalStatus.Up ||
                    n.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    n.GetIPProperties().GatewayAddresses.Count == 0) continue;
                var s = n.GetIPStatistics();
                now[n.Id] = (s.BytesReceived, s.BytesSent);
            }
            var seconds = _netClock.Elapsed.TotalSeconds;
            _netClock.Restart();
            long down = 0, up = 0;
            foreach (var (id, v) in now)
                if (_net.TryGetValue(id, out var old))
                {
                    down += Math.Max(0, v.Item1 - old.Down);
                    up += Math.Max(0, v.Item2 - old.Up);
                }
            var first = _net.Count == 0;
            _net = now.ToDictionary(p => p.Key, p => (p.Value.Item1, p.Value.Item2));
            if (first || seconds <= 0) return (null, now.Count == 0 ? "No connection" : "Measuring...");
            return ((down + up) / seconds, $"↓ {Rate(down / seconds)}   ↑ {Rate(up / seconds)}");
        }
        catch (NetworkInformationException) { return (null, "Unavailable"); }
    }

    public static string Rate(double bytes) =>
        bytes >= 1048576 ? $"{bytes / 1048576:N1} MB/s" : $"{bytes / 1024:N0} KB/s";

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = _diskCounter = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public double Value; }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
