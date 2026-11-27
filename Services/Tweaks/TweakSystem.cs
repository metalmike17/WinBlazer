using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services.Tweaks;

public sealed record Adapter(string Id, string Name);

/// <summary>Changes made outside the registry, so tests can use a fake.</summary>
public interface ITweakSystem
{
    Guid ActivePlan();
    uint? ReadPower(Guid plan, Guid group, Guid setting);
    void WritePower(Guid plan, Guid group, Guid setting, uint value);
    void SetServiceStart(string service, ServiceStart start);
    void SetHibernation(bool on);
    IReadOnlyList<Adapter> ActiveAdapters();
    /// <summary>An empty list means automatic (from the router).</summary>
    void SetDns(string adapterId, IReadOnlyList<string> servers);
}

public sealed class WindowsTweakSystem : ITweakSystem
{
    /// <summary>Runs a program from the Windows system folder.</summary>
    public static string Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, program))
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments) start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(); throw new IOException(program + " did not respond."); }
            if (process.ExitCode != 0)
                throw new IOException($"{program} failed: {(output.Result + " " + error.Result).Trim()}");
            return output.Result;
        }
        catch (Win32Exception ex) { throw new IOException(ex.Message); }
    }

    public Guid ActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0)
            throw new IOException("The active power plan could not be read.");
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }

    public uint? ReadPower(Guid plan, Guid group, Guid setting) =>
        PowerReadACValueIndex(IntPtr.Zero, ref plan, ref group, ref setting, out var value) == 0 ? value : null;

    public void WritePower(Guid plan, Guid group, Guid setting, uint value)
    {
        var status = PowerWriteACValueIndex(IntPtr.Zero, ref plan, ref group, ref setting, value);
        if (status == 0 && ActivePlan() == plan) status = PowerSetActiveScheme(IntPtr.Zero, ref plan);
        if (status != 0) throw new IOException($"Windows refused the power setting (error {status}).");
    }

    public void SetServiceStart(string service, ServiceStart start) => Run("sc.exe", "config", service, "start=",
        start.Start == 4 ? "disabled" : start.Start == 3 ? "demand" : start.Delayed ? "delayed-auto" : "auto");

    public void SetHibernation(bool on) => Run("powercfg.exe", "/hibernate", on ? "on" : "off");

    private static readonly string[] VirtualWords =
        { "virtual", "vpn", "warp", "wireguard", "tap-", "wintun", "hyper-v", "vmware", "loopback", "bluetooth" };

    private static IEnumerable<NetworkInterface> Physical() => NetworkInterface.GetAllNetworkInterfaces().Where(n =>
        n.OperationalStatus == OperationalStatus.Up &&
        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
        !VirtualWords.Any(w => (n.Name + " " + n.Description).Contains(w, StringComparison.OrdinalIgnoreCase)) &&
        n.GetIPProperties().GatewayAddresses.Count > 0);

    public IReadOnlyList<Adapter> ActiveAdapters()
    {
        try { return Physical().Select(n => new Adapter(n.Id, n.Name)).ToList(); }
        catch (NetworkInformationException) { return Array.Empty<Adapter>(); }
    }

    public void SetDns(string adapterId, IReadOnlyList<string> servers)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("The network adapter is not connected right now.");
        var name = "name=" + adapter.GetIPProperties().GetIPv4Properties().Index;
        if (servers.Count == 0)
        {
            Run("netsh.exe", "interface", "ipv4", "set", "dnsservers", name, "source=dhcp");
            return;
        }
        Run("netsh.exe", "interface", "ipv4", "set", "dnsservers", name, "source=static",
            "address=" + servers[0], "validate=no");
        for (var i = 1; i < servers.Count; i++)
            Run("netsh.exe", "interface", "ipv4", "add", "dnsservers", name, "address=" + servers[i],
                "index=" + (i + 1), "validate=no");
    }

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
