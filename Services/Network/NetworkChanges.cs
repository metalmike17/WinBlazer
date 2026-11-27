using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCOptimizer.Services.DriverTools;

namespace PCOptimizer.Services.Network;

public sealed record NetResult(bool Ok, string Message);

public sealed record OffloadProperty(string Keyword, string Name, string Value, string Display,
    string[] ValidValues, string[] ValidNames);

public sealed record TcpState(string AutoTuning, string Rss, string Rsc, List<OffloadProperty> Properties);

public sealed class SavedDnsSetting
{
    public string AdapterId { get; set; } = "";
    public string V4 { get; set; } = "";
    public string V6 { get; set; } = "";
}

/// <summary>Settings as they were before this module first changed them.</summary>
public sealed class NetworkBackup
{
    public List<SavedDnsSetting> Dns { get; set; } = new();
    /// <summary>Keys: "autotuning", "rss", "rsc", or "adapter|{id}|{keyword}".</summary>
    public Dictionary<string, string> Tcp { get; set; } = new();
}

public sealed class NetworkChanges
{
    public static readonly string[] AutoTuningLevels = { "Normal", "Disabled", "HighlyRestricted", "Restricted", "Experimental" };

    private static readonly string[] OffloadKeywords =
    {
        "*LsoV2IPv4", "*LsoV2IPv6", "*RscIPv4", "*RscIPv6", "*IPChecksumOffloadIPv4", "*TCPChecksumOffloadIPv4",
        "*TCPChecksumOffloadIPv6", "*UDPChecksumOffloadIPv4", "*UDPChecksumOffloadIPv6", "*InterruptModeration",
        "*FlowControl", "*RSS"
    };

    private readonly Func<string, string[], TimeSpan, ToolOutput> _run;
    private readonly Func<string, (string V4, string V6)> _staticDns;
    private readonly string _folder;
    private readonly bool _isAdmin;
    private readonly PCOptimizer.Core.ProtectedFolder? _protected;

    /// <summary>The app passes a protected folder for originals. Without one they go in the data folder (tests).</summary>
    public NetworkChanges(string dataFolder, bool isAdmin, Func<string, string[], TimeSpan, ToolOutput>? run = null,
        Func<string, (string, string)>? staticDns = null, PCOptimizer.Core.ProtectedFolder? protectedFolder = null)
    {
        _protected = protectedFolder;
        _folder = dataFolder;
        _isAdmin = isAdmin;
        _run = run ?? DriverService.Run;
        _staticDns = staticDns ?? ReadStaticDns;
    }

    private string BackupPath => Path.Combine(_protected?.FullPath ?? _folder, "network-backup.json");

    /// <summary>Empty means the address comes automatically from the network.</summary>
    public static (string V4, string V6) ReadStaticDns(string adapterId)
    {
        if (!Guid.TryParse(adapterId, out var id)) return ("", "");
        string Read(string stack)
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{stack}\Parameters\Interfaces\{id:B}");
            return key?.GetValue("NameServer") as string ?? "";
        }
        return (Read("Tcpip"), Read("Tcpip6"));
    }

    public NetworkBackup LoadBackup()
    {
        if (_protected is { IsUsable: false } || !File.Exists(BackupPath)) return new();
        // A damaged file throws, so original settings are never silently overwritten.
        try { return JsonSerializer.Deserialize<NetworkBackup>(File.ReadAllText(BackupPath)) ?? new(); }
        catch (JsonException ex) { throw new IOException($"The network backup is damaged: {BackupPath}. {ex.Message}"); }
    }

    private void SaveBackup(NetworkBackup backup)
    {
        if (_protected != null) _protected.PrepareForWriting();
        else Directory.CreateDirectory(_folder);
        File.WriteAllText(BackupPath + ".tmp",
            JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(BackupPath + ".tmp", BackupPath, true);
    }

    private ToolOutput PowerShell(string script) => _run(PCOptimizer.Core.SystemPowerShell.Program,
        PCOptimizer.Core.SystemPowerShell.Arguments(
            "$ProgressPreference='SilentlyContinue'\n$ErrorActionPreference='Stop'\n" +
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8\n" + script),
        TimeSpan.FromMinutes(2));

    private ToolOutput Netsh(params string[] arguments) =>
        _run(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments, TimeSpan.FromMinutes(2));

    // ---- DNS ----

    private static List<string> Addresses(string text) => text.Split(new[] { ',', ' ', ';' },
            StringSplitOptions.RemoveEmptyEntries)
        .Select(a => IPAddress.TryParse(a, out var ip) ? ip.ToString()
            : throw new InvalidDataException("A saved DNS address is not valid.")).ToList();

    private static string Quote(IEnumerable<string> addresses) => string.Join(",", addresses.Select(a => $"'{a}'"));

    public NetResult SetDns(AdapterInfo adapter, DnsProvider provider)
    {
        if (!_isAdmin) return new(false, "Changing DNS needs administrator rights.");
        // IPv6 is changed too when it is in use, or lookups could keep going to the old server.
        var useV6 = adapter.HasGlobalIPv6 || adapter.Dns.Any(d => d.Contains(':'));
        var servers = provider.V4.Concat(useV6 ? provider.V6 : Array.Empty<string>()).ToList();

        var backup = LoadBackup();
        if (!backup.Dns.Any(d => d.AdapterId == adapter.Id))
        {
            var (v4, v6) = _staticDns(adapter.Id);
            backup.Dns.Add(new SavedDnsSetting { AdapterId = adapter.Id, V4 = v4, V6 = v6 });
            SaveBackup(backup);
        }

        var result = PowerShell($"Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} " +
                                $"-ServerAddresses @({Quote(servers)})\nClear-DnsClientCache");
        var (now4, now6) = _staticDns(adapter.Id);
        var set = Addresses(now4 + "," + now6);
        return Addresses(string.Join(",", servers)).All(set.Contains)
            ? new(true, $"DNS on {adapter.Name} is now {provider.Name}{(useV6 ? " (IPv4 and IPv6)" : " (IPv4)")}.")
            : new(false, "Windows did not apply the DNS change. " + result.Output.Trim());
    }

    public bool HasDnsBackup(string adapterId) => LoadBackup().Dns.Any(d => d.AdapterId == adapterId);

    /// <summary>The saved addresses, to show before they are restored. Null if nothing is saved.</summary>
    public string? DescribeDnsBackup(string adapterId)
    {
        if (LoadBackup().Dns.FirstOrDefault(d => d.AdapterId == adapterId) is not { } saved) return null;
        static string Show(string servers) => servers.Length == 0 ? "automatic" : string.Join(", ", Addresses(servers));
        return $"IPv4: {Show(saved.V4)}\nIPv6: {Show(saved.V6)}";
    }

    public NetResult RestoreDns(AdapterInfo adapter)
    {
        if (!_isAdmin) return new(false, "Changing DNS needs administrator rights.");
        var backup = LoadBackup();
        var saved = backup.Dns.FirstOrDefault(d => d.AdapterId == adapter.Id);
        if (saved == null) return new(false, "There is no saved DNS setting for this adapter.");
        var servers = Addresses(saved.V4).Concat(Addresses(saved.V6)).ToList();
        var script = $"Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} -ResetServerAddresses\n" +
                     (servers.Count > 0 ? $"Set-DnsClientServerAddress -InterfaceIndex {adapter.Index} " +
                                          $"-ServerAddresses @({Quote(servers)})\n" : "") + "Clear-DnsClientCache";
        var result = PowerShell(script);
        var (v4, v6) = _staticDns(adapter.Id);
        if (!Addresses(v4).SequenceEqual(Addresses(saved.V4)) || !Addresses(v6).SequenceEqual(Addresses(saved.V6)))
            return new(false, "DNS was not fully restored; the saved setting is kept. " + result.Output.Trim());
        backup.Dns.Remove(saved);
        SaveBackup(backup);
        return new(true, servers.Count == 0 ? "DNS is automatic again."
            : "Your previous DNS servers were restored.");
    }

    // ---- TCP/IP ----

    public TcpState ReadTcp(AdapterInfo adapter)
    {
        var script = "$g = Get-NetOffloadGlobalSetting\n" +
            $"$n = [Management.Automation.WildcardPattern]::Escape((Get-NetAdapter -InterfaceIndex {adapter.Index}).Name)\n" +
            "$p = @(Get-NetAdapterAdvancedProperty -Name $n -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{\n" +
            " Keyword=$_.RegistryKeyword; Name=$_.DisplayName; Value=[string](@($_.RegistryValue)[0]); Display=$_.DisplayValue\n" +
            " ValidValues=@($_.ValidRegistryValues | ForEach-Object {[string]$_}); ValidNames=@($_.ValidDisplayValues | ForEach-Object {[string]$_}) } })\n" +
            "ConvertTo-Json -Depth 4 -Compress -InputObject ([pscustomobject]@{\n" +
            " AutoTuning=[string](Get-NetTCPSetting -SettingName Internet).AutoTuningLevelLocal\n" +
            " Rss=[string]$g.ReceiveSideScaling; Rsc=[string]$g.ReceiveSegmentCoalescing; Properties=$p })";
        var result = PowerShell(script);
        if (result.ExitCode != 0) throw new IOException("TCP/IP settings could not be read: " + result.Output.Trim());
        var state = JsonSerializer.Deserialize<TcpState>(result.Output)!;
        return state with
        {
            Properties = state.Properties.Where(p => OffloadKeywords.Contains(p.Keyword, StringComparer.OrdinalIgnoreCase)
                && p.ValidValues.Length > 1 && p.ValidValues.Length == p.ValidNames.Length).ToList()
        };
    }

    private static string? Current(TcpState state, string key) => key switch
    {
        "autotuning" => state.AutoTuning,
        "rss" => state.Rss,
        "rsc" => state.Rsc,
        _ => state.Properties.FirstOrDefault(p => p.Keyword == key.Split('|')[^1])?.Value
    };

    public static string AdapterKey(AdapterInfo adapter, string keyword) => $"adapter|{adapter.Id}|{keyword}";

    private bool Allowed(TcpState state, string key, string value) => key switch
    {
        "autotuning" => AutoTuningLevels.Contains(value),
        "rss" or "rsc" => value is "Enabled" or "Disabled",
        _ => state.Properties.Any(p => key.EndsWith("|" + p.Keyword) && p.ValidValues.Contains(value) &&
                                       Regex.IsMatch(value, @"\A\d+\z"))
    };

    private ToolOutput Apply(AdapterInfo adapter, string key, string value) => key switch
    {
        "autotuning" => Netsh("int", "tcp", "set", "global", "autotuninglevel=" + value.ToLowerInvariant()),
        "rss" => PowerShell($"Set-NetOffloadGlobalSetting -ReceiveSideScaling {value}"),
        "rsc" => PowerShell($"Set-NetOffloadGlobalSetting -ReceiveSegmentCoalescing {value}"),
        _ => PowerShell(
            $"$n = [Management.Automation.WildcardPattern]::Escape((Get-NetAdapter -InterfaceIndex {adapter.Index}).Name)\n" +
            $"Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '{key.Split('|')[^1]}' -RegistryValue {value}")
    };

    /// <summary>Saves the original value before the first change, then confirms the new one took effect.</summary>
    public NetResult SetTcp(AdapterInfo adapter, string key, string value)
    {
        if (!_isAdmin) return new(false, "Changing TCP/IP settings needs administrator rights.");
        var state = ReadTcp(adapter);
        var old = Current(state, key);
        if (old == null || !Allowed(state, key, value)) return new(false, "That setting or value is not available.");
        if (old == value) return new(true, "Already set.");
        var backup = LoadBackup();
        if (backup.Tcp.TryAdd(key, old)) SaveBackup(backup);
        var result = Apply(adapter, key, value);
        return Current(ReadTcp(adapter), key) == value ? new(true, "Setting changed.")
            : new(false, "Windows did not apply the change. " + result.Output.Trim());
    }

    /// <summary>Restores global settings and those of this adapter.</summary>
    public NetResult RestoreTcp(AdapterInfo adapter)
    {
        if (!_isAdmin) return new(false, "Changing TCP/IP settings needs administrator rights.");
        var backup = LoadBackup();
        var keys = backup.Tcp.Keys.Where(k => !k.StartsWith("adapter|") || k.StartsWith($"adapter|{adapter.Id}|")).ToList();
        if (keys.Count == 0) return new(false, "There are no saved TCP/IP settings for this adapter.");
        int restored = 0;
        foreach (var key in keys)
        {
            var value = backup.Tcp[key];
            var state = ReadTcp(adapter);
            if (!Allowed(state, key, value)) continue;
            if (Current(state, key) != value) Apply(adapter, key, value);
            if (Current(ReadTcp(adapter), key) != value) continue;
            backup.Tcp.Remove(key);
            SaveBackup(backup);
            restored++;
        }
        return new(restored == keys.Count, $"Restored {restored} of {keys.Count} setting(s)." +
            (restored == keys.Count ? "" : " The others remain saved."));
    }

    // ---- Winsock ----

    /// <summary>Saves the current Winsock list for reference first. A reset cannot be undone from it.</summary>
    public NetResult ResetWinsock()
    {
        if (!_isAdmin) return new(false, "A Winsock reset needs administrator rights.");
        var catalog = Netsh("winsock", "show", "catalog");
        if (catalog.ExitCode != 0 || catalog.Output.Trim().Length == 0)
            return new(false, "The current Winsock list could not be saved, so nothing was reset.");
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, $"winsock-before-reset-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(file, catalog.Output);
        var result = Netsh("winsock", "reset");
        return result.ExitCode == 0
            ? new(true, $"Winsock was reset. Restart the PC to finish. The earlier list was saved to {file}")
            : new(false, "The Winsock reset failed. " + result.Output.Trim());
    }
}
