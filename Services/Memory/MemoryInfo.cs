using System.Runtime.InteropServices;

namespace PCOptimizer.Services.MemoryTools;

/// <summary>Bytes. InUse + Modified + Standby + Free equals Total.</summary>
public sealed record MemorySnapshot(DateTime TimeUtc, long Total, long InUse, long Modified, long Standby, long Free)
{
    /// <summary>What programs can use right away: standby cache can be reused instantly.</summary>
    public long Available => Standby + Free;
}

/// <summary>Reads memory lists from Windows performance counters. No administrator rights needed.</summary>
public sealed class MemoryReader : IDisposable
{
    private static readonly string[] Paths =
    {
        @"\Memory\Modified Page List Bytes", @"\Memory\Free & Zero Page List Bytes",
        @"\Memory\Standby Cache Core Bytes", @"\Memory\Standby Cache Normal Priority Bytes",
        @"\Memory\Standby Cache Reserve Bytes"
    };

    private readonly IntPtr[] _counters = new IntPtr[Paths.Length];
    private IntPtr _query;

    public MemoryReader()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        for (var i = 0; i < Paths.Length; i++)
            if (PdhAddEnglishCounter(_query, Paths[i], IntPtr.Zero, out _counters[i]) != 0) { Dispose(); return; }
    }

    /// <summary>Null if Windows did not provide every figure.</summary>
    public MemorySnapshot? Read()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        var values = new long[Paths.Length];
        for (var i = 0; i < Paths.Length; i++)
        {
            const uint asLarge = 0x00000400;
            if (PdhGetFormattedCounterValue(_counters[i], asLarge, IntPtr.Zero, out var v) != 0 || v.Status > 1)
                return null;
            values[i] = v.Value;
        }
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;
        var total = (long)status.TotalPhys;
        long modified = values[0], free = values[1], standby = values[2] + values[3] + values[4];
        return new MemorySnapshot(DateTime.UtcNow, total, Math.Max(0, total - modified - standby - free),
                                  modified, standby, free);
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue { public uint Status; public long Value; }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
