using System.Text;
using PCOptimizer.Services;
using PCOptimizer.Services.RepairTools;

namespace PCOptimizer;

/// <summary>No repair tool is ever started. The runner is tested with harmless system programs.</summary>
public static class RepairTests
{
    private static VolumeInfo Volume(MediaKind media, bool system = false, string format = "NTFS") =>
        new('D', "", format, 100, 50, media, 1, system);

    public static async Task RunAsync(string work, Action<string, bool> check, List<string> log)
    {
        string Args(RepairJob job) => string.Join(' ', job.Arguments);
        check("SSDs get TRIM only", Args(RepairJobs.Optimize(Volume(MediaKind.Ssd))) == "D: /L /U /V");
        check("hard disks are defragmented, unknown types left to Windows",
              Args(RepairJobs.Optimize(Volume(MediaKind.Hdd))) == "D: /D /U /V" &&
              Args(RepairJobs.Optimize(Volume(MediaKind.Unknown))) == "D: /O /U /V");
        var fixSystem = RepairJobs.FixDisk(Volume(MediaKind.Ssd, true));
        var fixOther = RepairJobs.FixDisk(Volume(MediaKind.Ssd));
        check("fix never forces a dismount", Args(fixOther) == "D: /f" && fixOther.Input!.StartsWith("N") &&
              fixSystem.Input!.StartsWith("Y"));
        check("only read-only jobs can be cancelled",
              RepairJobs.CheckDisk(Volume(MediaKind.Ssd)).CanCancel && RepairJobs.Analyze(Volume(MediaKind.Ssd)).CanCancel &&
              !new[] { RepairJobs.Sfc(), RepairJobs.Dism(), fixOther, RepairJobs.Optimize(Volume(MediaKind.Hdd)) }
                  .Any(j => j.CanCancel));

        check("progress is read from each tool's output",
              RepairJobs.Progress(RepairKind.Sfc, "Verification 45% complete.") == 45 &&
              RepairJobs.Progress(RepairKind.Dism, "[=====      62.3%      ]") == 62.3 &&
              RepairJobs.Progress(RepairKind.CheckDisk, "Progress: 5 of 10 done; Stage: 50%; Total: 17%; ETA: 0:01") == 17 &&
              RepairJobs.Progress(RepairKind.Optimize, "Retrim:  80% complete...") == 80 &&
              RepairJobs.Progress(RepairKind.Sfc, "Beginning system scan.") == null);

        RepairResult Read(RepairJob job, int code, string text) => RepairJobs.Interpret(job, code, text);
        check("SFC results are interpreted", new[]
        {
            Read(RepairJobs.Sfc(), 0, "Windows Resource Protection did not find any integrity violations.").Verdict == Verdict.Success,
            Read(RepairJobs.Sfc(), 0, "found corrupt files and successfully repaired them.").Verdict == Verdict.Success,
            Read(RepairJobs.Sfc(), 1, "found corrupt files but was unable to fix some of them.").Verdict == Verdict.Attention,
            Read(RepairJobs.Sfc(), 0, "something unfamiliar").Verdict == Verdict.Attention
        }.All(x => x));
        check("DISM results are interpreted",
              Read(RepairJobs.Dism(), 0, "The restore operation completed successfully.").Verdict == Verdict.Success &&
              Read(RepairJobs.Dism(), 3010, "").Verdict == Verdict.Attention &&
              Read(RepairJobs.Dism(), 87, "").Verdict == Verdict.Failed);
        var scheduled = Read(fixSystem, 3, "This volume will be checked the next time the system restarts.");
        check("a scheduled fix is not reported as done",
              scheduled.Verdict == Verdict.Attention && scheduled.Summary.Contains("has not run yet") &&
              Read(fixOther, 3, "Chkdsk cannot run.").Verdict == Verdict.Attention);

        await RunnerAsync(work, check);
        Smart(check);

        var volumes = DriveCatalog.Volumes();
        log.Add("Volumes: " + string.Join(" | ", volumes.Select(v => v.Describe() + $" disk {v.DiskNumber}")));
        check("the Windows drive is found", volumes.Any(v => v.IsSystem && v.DiskNumber != null));
        var drives = SmartReader.ReadAll();
        foreach (var d in drives)
        {
            var score = SmartParser.Score(d);
            log.Add($"  Disk {d.Number}: {d.Model} | {d.Bus} | temp {d.Temperature?.ToString() ?? "-"} | " +
                    $"nvme {(d.Nvme == null ? "no" : $"yes used {d.Nvme.PercentUsed}% spare {d.Nvme.Spare}% errors {d.Nvme.MediaErrors} hours {d.Nvme.PowerOnHours}")} | " +
                    $"ata {d.Attributes.Count} | predict {d.PredictsFailure?.ToString() ?? "-"} | score {score.Score?.ToString() ?? "none"}");
        }
        check("physical drives are listed", drives.Count > 0);
    }

    private static async Task RunnerAsync(string work, Action<string, bool> check)
    {
        var runner = new RepairRunner(Path.Combine(work, "repair-logs"));
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        RepairJob Fake(string command, bool canCancel = false, string? input = null) => new(RepairKind.Analyze,
            "Test " + command, cmd, new[] { "/d", "/c", command }, Encoding.ASCII, input, canCancel);

        var ok = await runner.RunAsync(Fake("echo first line& echo 40% complete"));
        var path = runner.LogPath!;
        check("the runner reports output, progress and a log", ok.Verdict == Verdict.Success && runner.Percent == 40 &&
              runner.LogText.Contains("first line") && File.ReadAllText(path).Contains("Result: Success"));
        check("a failing tool is reported as failed", (await runner.RunAsync(Fake("exit /b 5"))).Verdict == Verdict.Failed);
        await runner.RunAsync(Fake("set /p answer=& call echo got %answer%", input: "Y\r\n"));
        check("answers are passed to the tool", runner.LogText.Contains("got"));

        var wait = $"{Path.Combine(Environment.SystemDirectory, "ping.exe")} -n 4 127.0.0.1";
        var locked = runner.RunAsync(Fake(wait));
        var secondRefused = false;
        try { await runner.RunAsync(Fake("echo no")); }
        catch (InvalidOperationException) { secondRefused = true; }
        runner.Cancel();   // must have no effect on a job that cannot be cancelled
        var finished = await locked;
        check("one tool at a time, and repairs ignore cancel", secondRefused && finished.Verdict == Verdict.Success);
        var stoppable = runner.RunAsync(Fake(wait.Replace("-n 4", "-n 30"), canCancel: true));
        await Task.Delay(700);
        runner.Cancel();
        var done = await Task.WhenAny(stoppable, Task.Delay(8000)) == stoppable;
        check("a read-only job can be cancelled", done && (await stoppable).Verdict == Verdict.Cancelled);

        var blocker = Path.Combine(work, "repair-blocker");
        File.WriteAllText(blocker, "x");
        var marker = Path.Combine(work, "must-not-exist.txt");
        var unable = new RepairRunner(Path.Combine(blocker, "logs"));
        var refused = await unable.RunAsync(Fake($"echo x> \"{marker}\""));
        check("no log means the tool is not started",
              refused.Verdict == Verdict.Failed && !File.Exists(marker) && !unable.IsRunning);
    }

    private static void Smart(Action<string, bool> check)
    {
        var data = new byte[512];
        var limits = new byte[512];
        void Attribute(int slot, byte id, byte flags, byte current, byte limit, long raw)
        {
            var at = 2 + slot * 12;
            (data[at], data[at + 1], data[at + 3], data[at + 4]) = (id, flags, current, current);
            for (var i = 0; i < 6; i++) data[at + 5 + i] = (byte)(raw >> (8 * i));
            (limits[at], limits[at + 1]) = (id, limit);
        }
        Attribute(0, 5, 1, 100, 10, 24);
        Attribute(1, 194, 0, 64, 0, 0x0000_2D00_0000_0024);
        var attributes = SmartParser.ParseAta(data, limits);
        check("SATA attributes are decoded", attributes.Count == 2 && attributes[0].Raw == 24 &&
              attributes[0].Threshold == 10 && SmartParser.AtaTemperature(attributes) == 36);

        var healthy = new byte[512];
        (healthy[1], healthy[2], healthy[3], healthy[4], healthy[5], healthy[128]) = (0x36, 0x01, 100, 10, 2, 200);
        var nvme = SmartParser.ParseNvme(healthy)!;
        check("NVMe health log is decoded", nvme.Temperature == 37 && nvme.Spare == 100 && nvme.PercentUsed == 2 &&
              nvme.PowerOnHours == 200 && SmartParser.ParseNvme(new byte[512]) == null);

        int? Score(Action<DriveHealth> set) { var d = new DriveHealth(); set(d); return SmartParser.Score(d).Score; }
        check("health scores follow the warning signs",
              Score(d => d.Nvme = nvme) == 100 &&
              Score(d => d.Attributes = attributes) == 81 &&
              Score(d => d.Nvme = nvme with { CriticalWarning = 1 }) == 30 &&
              Score(d => d.PredictsFailure = true) == 10);
        check("no data means no score", Score(_ => { }) == null && Score(d => d.Temperature = 40) == null &&
              Score(d => d.PredictsFailure = false) == null);
    }
}
