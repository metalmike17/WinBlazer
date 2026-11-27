namespace PCOptimizer.Services.RepairTools;

public sealed record SmartAttribute(int Id, string Name, int Flags, int Current, int Worst, int? Threshold, long Raw)
{
    public bool BelowThreshold => Threshold is > 0 && Current <= Threshold;
    public bool PreFailure => (Flags & 1) != 0;
}

public sealed record NvmeHealth(int CriticalWarning, double? Temperature, int Spare, int SpareThreshold,
    int PercentUsed, decimal BytesRead, decimal BytesWritten, decimal PowerCycles, decimal PowerOnHours,
    decimal UnsafeShutdowns, decimal MediaErrors, decimal ErrorLogEntries);

public sealed class DriveHealth
{
    public int Number { get; init; }
    public string Model { get; init; } = "Unknown drive";
    public string Bus { get; init; } = "Unknown";
    public double? Temperature { get; set; }
    public NvmeHealth? Nvme { get; set; }
    public List<SmartAttribute> Attributes { get; set; } = new();
    public bool? PredictsFailure { get; set; }
    public long? ReallocatedSectors => Attributes.FirstOrDefault(a => a.Id == 5)?.Raw;
}

/// <summary>A null score means there is not enough data to judge.</summary>
public sealed record HealthScore(int? Score, string Label, List<string> Reasons);

public static class SmartParser
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Read error rate", [3] = "Spin-up time", [4] = "Start/stop count", [5] = "Reallocated sectors",
        [7] = "Seek error rate", [9] = "Power-on hours", [10] = "Spin retry count", [12] = "Power cycle count",
        [177] = "Wear leveling (vendor)", [179] = "Used reserved blocks (vendor)", [181] = "Program failures",
        [182] = "Erase failures", [184] = "End-to-end errors", [187] = "Reported uncorrectable errors",
        [188] = "Command timeouts", [190] = "Airflow temperature", [192] = "Unsafe power-off count",
        [193] = "Load cycle count", [194] = "Temperature", [196] = "Reallocation events",
        [197] = "Pending sectors", [198] = "Offline uncorrectable sectors", [199] = "Interface CRC errors",
        [231] = "SSD life (vendor)", [233] = "Media wear (vendor)", [241] = "Total written (vendor units)",
        [242] = "Total read (vendor units)"
    };

    /// <summary>The 512-byte ATA attribute and threshold tables: 30 entries of 12 bytes from offset 2.</summary>
    public static List<SmartAttribute> ParseAta(byte[] data, byte[]? thresholds)
    {
        var limits = new Dictionary<int, int>();
        if (thresholds is { Length: >= 362 })
            for (var i = 0; i < 30; i++)
                if (thresholds[2 + i * 12] != 0) limits[thresholds[2 + i * 12]] = thresholds[3 + i * 12];
        var result = new List<SmartAttribute>();
        if (data.Length < 362) return result;
        for (var i = 0; i < 30; i++)
        {
            var at = 2 + i * 12;
            int id = data[at];
            if (id == 0) continue;
            long raw = 0;
            for (var b = 5; b >= 0; b--) raw = (raw << 8) | data[at + 5 + b];
            result.Add(new SmartAttribute(id, Names.GetValueOrDefault(id, $"Vendor attribute {id}"),
                data[at + 1] | data[at + 2] << 8, data[at + 3], data[at + 4],
                limits.TryGetValue(id, out var limit) ? limit : null, raw));
        }
        return result;
    }

    public static double? AtaTemperature(IEnumerable<SmartAttribute> attributes)
    {
        var found = attributes.FirstOrDefault(a => a.Id == 194) ?? attributes.FirstOrDefault(a => a.Id == 190);
        var value = found == null ? 0 : (int)(found.Raw & 0xFF);   // upper bytes hold min and max on some drives
        return value is > 0 and < 120 ? value : null;
    }

    /// <summary>NVMe SMART / Health Information log page (02h).</summary>
    public static NvmeHealth? ParseNvme(byte[] log)
    {
        if (log.Length < 512 || log.Take(192).All(b => b == 0)) return null;
        decimal Number(int at)   // 16-byte counters; values beyond decimal range do not occur in practice
        {
            decimal value = 0;
            for (var i = 11; i >= 0; i--) value = value * 256 + log[at + i];
            return value;
        }
        var kelvin = log[1] | log[2] << 8;
        return new NvmeHealth(log[0], kelvin is > 200 and < 400 ? kelvin - 273 : null, log[3], log[4], log[5],
            Number(32) * 512000, Number(48) * 512000, Number(112), Number(128), Number(144), Number(160), Number(176));
    }

    /// <summary>
    /// A simple, explained rating of the reported warning signs. It is not a prediction: drives can fail
    /// with a perfect score.
    /// </summary>
    public static HealthScore Score(DriveHealth drive)
    {
        var reasons = new List<string>();
        if (drive.Nvme == null && drive.Attributes.Count == 0 && drive.PredictsFailure != true)
            return new(null, "Not enough data", new() { "SMART health data could not be read." });
        var score = 100;
        var cap = 100;
        void Deduct(int points, string why) { score -= points; reasons.Add($"−{points}: {why}"); }
        void Limit(int maximum, string why) { cap = Math.Min(cap, maximum); reasons.Add($"Limited to {maximum}: {why}"); }

        if (drive.PredictsFailure == true) Limit(10, "the drive predicts its own failure. Back up now.");
        if (drive.Nvme is { } n)
        {
            if (n.CriticalWarning != 0) Limit(30, $"NVMe critical warning (0x{n.CriticalWarning:X2}).");
            if (n.Spare < n.SpareThreshold) Limit(20, "spare capacity is below the drive's threshold.");
            if (n.MediaErrors > 0) Deduct((int)Math.Min(40, 15 + n.MediaErrors), $"{n.MediaErrors:N0} media errors.");
            if (n.PercentUsed >= 100) Limit(50, "rated endurance is used up.");
            if (n.PercentUsed > 0)
            {
                var wear = Math.Min(30, n.PercentUsed * 3 / 10);
                if (wear > 0) Deduct(wear, $"{n.PercentUsed}% of rated endurance used.");
            }
        }
        foreach (var a in drive.Attributes)
        {
            if (a.PreFailure && a.BelowThreshold) Limit(20, $"{a.Name} reached its failure threshold.");
            if (a.Raw <= 0) continue;
            if (a.Id == 5) Deduct((int)Math.Min(60, 15 + a.Raw / 5), $"{a.Raw:N0} reallocated sectors.");
            if (a.Id == 197) Deduct(25, $"{a.Raw:N0} pending sectors.");
            if (a.Id == 198) Deduct(25, $"{a.Raw:N0} offline uncorrectable sectors.");
            if (a.Id == 187) Deduct(15, $"{a.Raw:N0} reported uncorrectable errors.");
            if (a.Id == 199) Deduct(5, $"{a.Raw:N0} interface errors (often a cable; the count never resets).");
        }
        if (drive.Temperature >= 80) Deduct(20, $"very high temperature ({drive.Temperature:N0} °C).");
        else if (drive.Temperature >= 70) Deduct(10, $"high temperature ({drive.Temperature:N0} °C).");

        score = Math.Clamp(Math.Min(score, cap), 0, 100);
        if (reasons.Count == 0) reasons.Add("No warning signs in the data that was read.");
        return new(score, score >= 90 ? "Good" : score >= 70 ? "Fair" : score >= 40 ? "Caution" : "Poor", reasons);
    }
}
