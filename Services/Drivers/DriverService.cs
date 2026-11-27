using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PCOptimizer.Services.DriverTools;

public sealed record ToolOutput(int ExitCode, string Output);

public sealed record InstallResult(bool Ok, bool RebootRequired, string Message);

public sealed record BackupResult(bool Ok, string Folder, int Packages, string Message);

public interface IDriverUpdater
{
    /// <summary>The license text of an update that has not been accepted yet, or null.</summary>
    string? License(string updateId);
    InstallResult Install(string updateId, bool acceptLicense, Action<string> progress);
}

/// <summary>Reads drivers, and searches and installs them through the Windows Update Agent.</summary>
public sealed class WindowsDriverUpdater : IDriverUpdater
{
    private const string Query = "IsInstalled=0 and Type='Driver'";

    private static dynamic Session()
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", true)!)!;
        session.ClientApplicationID = "WinBlazer";
        return session;
    }

    /// <summary>Contacts Windows Update. Read-only.</summary>
    public List<OfferedUpdate> Search()
    {
        dynamic result = Session().CreateUpdateSearcher().Search(Query);
        // 2 succeeded, 3 succeeded with errors.
        if ((int)result.ResultCode is not (2 or 3)) throw new IOException("Windows Update search did not succeed.");
        var offers = new List<OfferedUpdate>();
        foreach (var update in result.Updates)
        {
            string hardware = update.DriverHardwareID ?? "";
            DateTime? date = null;
            try { date = (DateTime)update.DriverVerDate; }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { }
            offers.Add(new OfferedUpdate((string)update.Identity.UpdateID, (string)update.Title, hardware, date,
                                         (bool)update.EulaAccepted));
        }
        return offers;
    }

    private static dynamic Find(dynamic session, string updateId)
    {
        dynamic result = session.CreateUpdateSearcher().Search(Query);
        foreach (var update in result.Updates)
            if ((string)update.Identity.UpdateID == updateId) return update;
        throw new IOException("Windows Update no longer offers this driver. Check for updates again.");
    }

    public string? License(string updateId)
    {
        dynamic update = Find(Session(), updateId);
        return (bool)update.EulaAccepted ? null : (string)(update.EulaText ?? "No license text was provided.");
    }

    public InstallResult Install(string updateId, bool acceptLicense, Action<string> progress)
    {
        progress("Finding the update...");
        dynamic session = Session();
        dynamic update = Find(session, updateId);
        if (!(bool)update.EulaAccepted)
        {
            if (!acceptLicense) return new(false, false, "The license terms were not accepted.");
            update.AcceptEula();
        }
        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true)!)!;
        collection.Add(update);

        progress("Downloading from Windows Update...");
        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = collection;
        dynamic download = downloader.Download();
        if ((int)download.ResultCode is not (2 or 3) || !(bool)update.IsDownloaded)
            return new(false, false, $"The download failed (0x{(int)download.HResult:X8}).");

        progress("Installing. Do not unplug devices or shut down...");
        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = collection;
        dynamic installed = installer.Install();
        dynamic one = installed.GetUpdateResult(0);
        var code = (int)one.ResultCode;
        bool reboot = installed.RebootRequired;
        return code == 2
            ? new(true, reboot, "Installed." + (reboot ? " Restart the PC to finish." : ""))
            : new(false, reboot, $"Windows did not confirm a successful install (result {code}, 0x{(int)one.HResult:X8}).");
    }
}

public sealed class DriverService
{
    private readonly Func<string, string[], TimeSpan, ToolOutput> _run;

    public DriverService(Func<string, string[], TimeSpan, ToolOutput>? run = null) => _run = run ?? Run;

    public static ToolOutput Run(string program, string[] arguments, TimeSpan limit)
    {
        var start = new ProcessStartInfo(program)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (program.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            start.StandardOutputEncoding = Encoding.UTF8;
            PCOptimizer.Core.SystemPowerShell.Isolate(start);
        }
        foreach (var a in arguments) start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)limit.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                throw new IOException($"{Path.GetFileName(program)} did not finish in time.");
            }
            process.WaitForExit();
            return new(process.ExitCode, output.Result.Length > 0 ? output.Result : error.Result);
        }
        catch (Win32Exception ex) { throw new IOException(ex.Message); }
    }

    private static string PnpUtil => Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    public List<DeviceDriver> ReadInstalled()
    {
        var result = _run(PCOptimizer.Core.SystemPowerShell.Program,
            PCOptimizer.Core.SystemPowerShell.Arguments(DriverInventory.Script), TimeSpan.FromMinutes(3));
        if (result.ExitCode != 0) throw new IOException("Drivers could not be read: " + result.Output.Trim());
        return DriverInventory.Parse(result.Output);
    }

    private static string Csv(string value)
    {
        // A leading =, +, - or @ would be run as a formula by spreadsheet programs.
        if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string InventoryCsv(IEnumerable<DeviceDriver> drivers) =>
        "Device,Count,Version,Date,Provider,Class,INF,Health\r\n" + string.Concat(drivers.Select(d => string.Join(",",
            Csv(d.Name), d.Count, Csv(d.Version), d.Date?.ToString("yyyy-MM-dd") ?? "", Csv(d.Provider),
            Csv(d.Class), Csv(d.InfName), d.Health) + "\r\n"));

    /// <summary>
    /// Exports every third-party driver package into a new folder. Drivers built into Windows cannot be
    /// exported; they come with Windows itself. Needs administrator rights.
    /// </summary>
    public BackupResult Backup(string parentFolder, IReadOnlyList<DeviceDriver> inventory, bool isAdmin)
    {
        if (!isAdmin) return new(false, "", 0, "Exporting drivers needs administrator rights.");
        var folder = Path.Combine(parentFolder, $"DriverBackup-{DateTime.Now:yyyy-MM-dd-HHmmss}");
        if (Directory.Exists(folder)) return new(false, folder, 0, "The backup folder already exists. Try again.");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "installed-drivers.csv"), InventoryCsv(inventory), Encoding.UTF8);
        var result = _run(PnpUtil, new[] { "/export-driver", "*", folder }, TimeSpan.FromMinutes(30));
        var packages = Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories).Length;
        File.WriteAllText(Path.Combine(folder, "export-log.txt"), result.Output);
        var ok = result.ExitCode == 0 && packages > 0;
        File.WriteAllText(Path.Combine(folder, "HOW-TO-RESTORE.txt"),
            (ok ? "" : "WARNING: THIS BACKUP IS INCOMPLETE.\r\n\r\n") +
            $"Third-party driver packages exported: {packages}\r\n" +
            "Drivers built into Windows are not included.\r\n\r\n" +
            "One device: in Device Manager choose Update driver, Browse my computer, and select this folder.\r\n" +
            "All packages (administrator Command Prompt, run from this folder):\r\n" +
            "  pnputil /add-driver \"*.inf\" /subdirs /install\r\n" +
            "Restore only onto the same PC and Windows version where possible.\r\n");
        return new(ok, folder, packages, ok ? $"Exported {packages} driver package(s)."
            : $"The export did not complete (exit code {result.ExitCode}, {packages} package(s) copied). See export-log.txt.");
    }

    /// <summary>
    /// Saves the current driver first (when it can be exported), then installs. If that save fails,
    /// nothing is installed.
    /// </summary>
    public InstallResult Update(DeviceDriver driver, IDriverUpdater updater, string safetyFolder, bool isAdmin,
                                bool acceptLicense, Action<string> progress)
    {
        if (!isAdmin) return new(false, false, "Installing drivers needs administrator rights.");
        if (driver.WindowsUpdate is not { } offer) return new(false, false, "Windows Update offers no driver for this device.");
        if (driver.IsThirdParty)
        {
            progress("Saving the current driver first...");
            var folder = Path.Combine(safetyFolder,
                $"{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileNameWithoutExtension(driver.InfName)}");
            Directory.CreateDirectory(folder);
            var saved = _run(PnpUtil, new[] { "/export-driver", driver.InfName, folder }, TimeSpan.FromMinutes(10));
            if (saved.ExitCode != 0 || Directory.GetFiles(folder, "*.inf", SearchOption.AllDirectories).Length == 0)
                return new(false, false, "The current driver could not be saved, so nothing was installed.");
        }
        return updater.Install(offer.UpdateId, acceptLicense, progress);
    }
}
