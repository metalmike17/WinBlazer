using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PCOptimizer.Services.RepairTools;

public enum RepairKind { Sfc, Dism, CheckDisk, FixDisk, Analyze, Optimize }
public enum Verdict { Success, Attention, Failed, Cancelled }

public sealed record RepairResult(Verdict Verdict, string Summary);

/// <summary>A command to run. Only read-only jobs may be cancelled.</summary>
public sealed record RepairJob(RepairKind Kind, string Title, string Program, IReadOnlyList<string> Arguments,
                               Encoding Encoding, string? Input = null, bool CanCancel = false,
                               VolumeInfo? Volume = null);

public static class RepairJobs
{
    static RepairJobs() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The code page console tools use when their output is redirected.</summary>
    private static Encoding Oem => Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

    private static string System32(string name) => Path.Combine(Environment.SystemDirectory, name);

    public static RepairJob Sfc() => new(RepairKind.Sfc, "System File Checker (sfc /scannow)",
        System32("sfc.exe"), new[] { "/scannow" }, Encoding.Unicode);   // sfc writes UTF-16

    public static RepairJob Dism() => new(RepairKind.Dism, "DISM RestoreHealth", System32("dism.exe"),
        new[] { "/English", "/Online", "/Cleanup-Image", "/RestoreHealth" }, Oem);

    private static string Target(VolumeInfo volume) =>
        char.IsAsciiLetterUpper(volume.Letter) ? volume.Letter + ":"
            : throw new ArgumentException("Not a drive letter.");

    public static bool SupportsOnlineCheck(VolumeInfo v) => v.FileSystem == "NTFS";
    public static bool SupportsFix(VolumeInfo v) => v.FileSystem is "NTFS" or "FAT32" or "exFAT" or "FAT";

    public static RepairJob CheckDisk(VolumeInfo v) => SupportsOnlineCheck(v)
        ? new(RepairKind.CheckDisk, $"Check disk {v.Letter}: (online scan)", System32("chkdsk.exe"),
              new[] { Target(v), "/scan" }, Oem, CanCancel: true, Volume: v)
        : throw new InvalidOperationException("The online scan needs an NTFS drive.");

    /// <summary>
    /// chkdsk /f. On the Windows drive it can only be scheduled for the next restart ("Y"). On another
    /// drive in use, it is not force-dismounted ("N"), and is scheduled instead if Windows offers that ("Y").
    /// </summary>
    public static RepairJob FixDisk(VolumeInfo v) => SupportsFix(v)
        ? new(RepairKind.FixDisk, $"Fix disk {v.Letter}: (chkdsk /f)", System32("chkdsk.exe"),
              new[] { Target(v), "/f" }, Oem, v.IsSystem ? "Y\r\n" : "N\r\nY\r\n", Volume: v)
        : throw new InvalidOperationException("chkdsk does not support this file system.");

    public static RepairJob Analyze(VolumeInfo v) => new(RepairKind.Analyze, $"Analyze {v.Letter}:",
        System32("defrag.exe"), new[] { Target(v), "/A", "/U", "/V" }, Oem, CanCancel: true, Volume: v);

    /// <summary>SSDs get TRIM (/L) only, never defragmentation. Unknown types let Windows decide (/O).</summary>
    public static RepairJob Optimize(VolumeInfo v)
    {
        var (name, arguments) = v.Media switch
        {
            MediaKind.Ssd => ("TRIM", new[] { Target(v), "/L", "/U", "/V" }),
            MediaKind.Hdd => ("Defragment", new[] { Target(v), "/D", "/U", "/V" }),
            _ => ("Optimize", new[] { Target(v), "/O", "/U", "/V" })
        };
        return new(RepairKind.Optimize, $"{name} {v.Letter}:", System32("defrag.exe"), arguments, Oem, Volume: v);
    }

    // ---- Reading output ----

    private static readonly Regex Percent = new(@"(\d{1,3}(?:[.,]\d+)?)\s*(?:%|percent)", RegexOptions.IgnoreCase);
    private static readonly Regex Total = new(@"Total:\s*(\d{1,3})%", RegexOptions.IgnoreCase);

    public static double? Progress(RepairKind kind, string line)
    {
        // chkdsk shows both stage and total progress; the total is the useful one.
        var match = kind is RepairKind.CheckDisk or RepairKind.FixDisk && Total.IsMatch(line)
            ? Total.Match(line) : Percent.Match(line);
        return match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value <= 100 ? value : null;
    }

    public static bool IsProgressOnly(string line) =>
        Regex.IsMatch(line, @"^\s*(\[[= \d.%]+\]|Verification \d+% complete\.|Progress: \d+ of \d+.*)\s*$");

    /// <summary>
    /// Interprets English output; exit codes are used where the tool documents them. When unsure the
    /// result says so rather than reporting success.
    /// </summary>
    public static RepairResult Interpret(RepairJob job, int exitCode, string output)
    {
        bool Has(string text) => output.Contains(text, StringComparison.OrdinalIgnoreCase);
        RepairResult Ok(string s) => new(Verdict.Success, s);
        RepairResult Look(string s) => new(Verdict.Attention, s);
        RepairResult Bad(string s) => new(Verdict.Failed, s);
        var unclear = $"The result could not be recognized (exit code {exitCode}). Read the log.";

        switch (job.Kind)
        {
            case RepairKind.Sfc:
                if (Has("did not find any integrity violations")) return Ok("No integrity violations found.");
                if (Has("unable to fix some of them"))
                    return Look("Corrupt files remain that SFC could not fix. Run DISM, restart, then SFC again. Details: CBS.log.");
                if (Has("successfully repaired them")) return Ok("Corrupt files were found and repaired. Restart the PC.");
                if (Has("system repair pending")) return Look("A repair is pending. Restart, then run SFC again.");
                if (Has("could not perform the requested operation")) return Bad("SFC could not run the check.");
                return exitCode == 0 ? Look(unclear) : Bad(unclear);

            case RepairKind.Dism:
                if (exitCode == 3010) return Look("DISM finished. Restart the PC to complete it.");
                if (exitCode == 0 && Has("restore operation completed successfully"))
                    return Ok("The component store restore completed successfully. Run SFC next.");
                if (Has("0x800f081f") || exitCode == unchecked((int)0x800f081f))
                    return Bad("DISM could not find repair source files (0x800f081f). Check the internet connection and Windows Update.");
                return exitCode == 0 ? Look(unclear)
                    : Bad($"DISM failed (exit code 0x{exitCode:X8}). See the log and Windows\\Logs\\DISM\\dism.log.");

            case RepairKind.CheckDisk:
                if (exitCode == 0) return Ok("No problems needing action were reported.");
                return Look($"chkdsk reported something to review (exit code {exitCode}). Read the log; " +
                            "use Fix if it reports errors.");

            case RepairKind.FixDisk:
                if (Has("will be checked the next time") || Has("scheduled on the next restart"))
                    return Look("The check and fix is SCHEDULED for the next restart. It has not run yet. " +
                                "Restart when you can; do not power off while it runs.");
                if (exitCode == 0) return Ok("No errors were found.");
                if (exitCode == 1) return Ok("Errors were found and fixed.");
                return Look($"chkdsk did not confirm a completed fix (exit code {exitCode}). Read the log.");

            default:
                return exitCode == 0 && !Has("(0x8")
                    ? Ok(job.Kind == RepairKind.Analyze ? "Analysis finished. See the log for the report."
                                                        : "Optimization finished.")
                    : Bad($"defrag did not complete (exit code 0x{exitCode:X8}). Read the log.");
        }
    }
}
