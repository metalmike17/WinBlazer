using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using PCOptimizer.Core;
using PCOptimizer.Services.DriverTools;

namespace PCOptimizer;

/// <summary>The decoy module only sits in the test folder and does nothing but exist.</summary>
public static class SecurityTests
{
    public static void Run(string work, Action<string, bool> check)
    {
        var modules = Path.Combine(work, "decoy-modules");
        var decoy = Directory.CreateDirectory(Path.Combine(modules, "WbDecoy")).FullName;
        File.WriteAllText(Path.Combine(decoy, "WbDecoy.psm1"), "function Get-WbDecoy { 'decoy' }");

        const string probe = "if (Get-Command Get-WbDecoy -ErrorAction SilentlyContinue) { 'LOADED' } else { 'BLOCKED' }";
        string Probe(string script)
        {
            var start = new ProcessStartInfo(SystemPowerShell.Program)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            // The new process is pointed at the decoy on purpose.
            start.Environment["PSModulePath"] = modules;
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                         Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
                start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000)) { process.Kill(); return "timeout"; }
            return output.Result.Trim();
        }
        // The first result shows the decoy really would be found, so the second one means something.
        check("PowerShell would load a stand-in module without the restriction", Probe(probe) == "LOADED");
        check("PowerShell run by the app does not load it", Probe(SystemPowerShell.FirstLine + probe) == "BLOCKED");

        var needed = new[]
        {
            "Get-AppxPackage", "Add-AppxPackage", "Remove-AppxPackage", "Get-CimInstance", "ConvertTo-Json",
            "Set-DnsClientServerAddress", "Clear-DnsClientCache", "Get-NetAdapter", "Get-NetAdapterAdvancedProperty",
            "Set-NetAdapterAdvancedProperty", "Get-NetOffloadGlobalSetting", "Set-NetOffloadGlobalSetting",
            "Get-NetTCPSetting", "Delete-DeliveryOptimizationCache"
        };
        // Only looks the commands up; none of them is run.
        var found = DriverService.Run(SystemPowerShell.Program, SystemPowerShell.Arguments(
            "$ProgressPreference='SilentlyContinue'\nforeach ($n in '" + string.Join("','", needed) +
            "') { if (-not (Get-Command $n -ErrorAction SilentlyContinue)) { \"missing $n\" } }\n'done'"),
            TimeSpan.FromMinutes(2));
        check("every PowerShell command the app uses is still found", found.ExitCode == 0 && found.Output.Trim() == "done");

        var inherited = new ProcessStartInfo("unused.exe") { UseShellExecute = false };
        inherited.Environment["COR_ENABLE_PROFILING"] = "1";
        inherited.Environment["COR_PROFILER_PATH"] = "x.dll";
        inherited.Environment["APPDOMAIN_MANAGER_ASM"] = "x";
        SystemPowerShell.Isolate(inherited);
        check("code-loading settings are not passed on",
              !inherited.Environment.Keys.Any(k => k.StartsWith("COR_") || k.StartsWith("APPDOMAIN_")) &&
              inherited.Environment["PSModulePath"]!.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase));

        var rules = ProtectedFolder.Security().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        const FileSystemRights changes = FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        check("normal users may read the protected folder but not change it", rules.Any(r =>
                  r.IdentityReference is SecurityIdentifier s && s.IsWellKnown(WellKnownSidType.BuiltinUsersSid) &&
                  (r.FileSystemRights & changes) == 0 && (r.FileSystemRights & FileSystemRights.ReadData) != 0) &&
              rules.Count == 3);

        // A folder a normal user made (as this one is) must never be trusted.
        var planted = new ProtectedFolder(Directory.CreateDirectory(Path.Combine(work, "planted-folder")).FullName);
        var refused = false;
        try { planted.PrepareForWriting(); }
        catch (IOException) { refused = true; }
        check("a folder that others can change is not trusted", refused && !planted.IsUsable);
    }

    /// <summary>
    /// Run last: the protection cannot be turned off again in this process. Uses only folders made here. A
    /// junction made without administrator rights is what an attacker could create.
    /// </summary>
    public static void RunRedirection(string work, Action<string, bool> check)
    {
        var area = Directory.CreateDirectory(Path.Combine(work, "redirect")).FullName;
        var cleaned = Directory.CreateDirectory(Path.Combine(area, "cleaned")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(area, "outside")).FullName;
        var victim = Path.Combine(outside, "report.txt");
        File.WriteAllText(victim, "keep");
        var link = Path.Combine(cleaned, "sub");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(a);
        using (var process = Process.Start(start)!) process.WaitForExit();
        var through = Path.Combine(link, "report.txt");
        try
        {
            // If this fails, the checks below would prove nothing.
            check("a folder link made without administrator rights is followed by default", File.Exists(through));
            check("redirect protection turns on", RedirectionGuard.TryEnable() && RedirectionGuard.IsOn);
            bool Blocked(Action action)
            {
                try { action(); return false; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
            }
            check("then a delete through the link is blocked",
                  Blocked(() => new FileInfo(through).Delete()) && File.Exists(victim));
            check("and so is a write through the link",
                  Blocked(() => File.WriteAllText(through, "changed")) && File.ReadAllText(victim) == "keep" &&
                  Blocked(() => File.WriteAllText(Path.Combine(link, "new.txt"), "x")) &&
                  !File.Exists(Path.Combine(outside, "new.txt")));
            var ordinary = Path.Combine(cleaned, "ordinary.txt");
            File.WriteAllText(ordinary, "x");
            File.Delete(ordinary);
            check("ordinary files still work", !File.Exists(ordinary));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }   // removes the link only
    }

    /// <summary>Stands in for the real protected folder, which needs administrator rights to create.</summary>
    public static ProtectedFolder Stand(string path) =>
        new(path, folder => folder.Exists, folder => folder.Create());
}
