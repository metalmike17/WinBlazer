using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCOptimizer.Services.DriverTools;

public enum DriverHealth { Missing, Error, Outdated, Disabled, Working }

public sealed record OfferedUpdate(string UpdateId, string Title, string HardwareId, DateTime? Date, bool EulaAccepted);

public sealed record VendorResult(string Version, DateTime? Released, string PageUrl);

public sealed class DeviceDriver
{
    public required string Name { get; init; }
    public string Version { get; init; } = "";
    public DateTime? Date { get; init; }
    public string Provider { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Class { get; init; } = "";
    public string InfName { get; init; } = "";
    public int ProblemCode { get; init; }
    public List<string> HardwareIds { get; init; } = new();
    public List<string> CompatibleIds { get; init; } = new();
    /// <summary>Identical devices are shown once.</summary>
    public int Count { get; set; } = 1;

    public OfferedUpdate? WindowsUpdate { get; set; }
    /// <summary>Windows Update offers a driver that is not newer by date.</summary>
    public bool OfferIsNotNewer { get; set; }
    public VendorResult? Vendor { get; set; }
    public bool? VendorIsNewer { get; set; }

    /// <summary>Third-party packages are published as oemNN.inf; only these can be exported.</summary>
    public bool IsThirdParty => Regex.IsMatch(InfName, @"^oem\d+\.inf$", RegexOptions.IgnoreCase);

    public DriverHealth Health => ProblemCode switch
    {
        28 => DriverHealth.Missing,
        22 => DriverHealth.Disabled,
        not 0 => DriverHealth.Error,
        _ => VendorIsNewer == true || (WindowsUpdate != null && !OfferIsNotNewer)
            ? DriverHealth.Outdated : DriverHealth.Working
    };

    public static string ProblemText(int code) => code switch
    {
        0 => "",
        1 => "Not configured correctly (code 1)",
        10 => "The device cannot start (code 10)",
        12 => "Not enough free resources (code 12)",
        14 => "Needs a restart (code 14)",
        18 => "Driver needs reinstalling (code 18)",
        22 => "Disabled (code 22)",
        24 => "Not present or not working (code 24)",
        28 => "No driver installed (code 28)",
        31 => "Windows cannot load the driver (code 31)",
        39 => "Driver is damaged or missing (code 39)",
        43 => "Windows stopped the device after it reported problems (code 43). This can be hardware, cable or driver.",
        45 => "Not connected (code 45)",
        52 => "Driver signature could not be verified (code 52)",
        _ => $"Device Manager problem (code {code})"
    };
}

public static class DriverInventory
{
    /// <summary>Fixed script: no values are inserted into it.</summary>
    public const string Script = @"$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$drivers = @(Get-CimInstance Win32_PnPSignedDriver | ForEach-Object { [pscustomobject]@{
  DeviceID=$_.DeviceID; Name=$_.DeviceName; Version=$_.DriverVersion; Manufacturer=$_.Manufacturer
  Provider=$_.DriverProviderName; Inf=$_.InfName; Class=$_.DeviceClass
  Date=$(if($_.DriverDate){$_.DriverDate.ToUniversalTime().ToString('yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture)}) } })
$devices = @(Get-CimInstance Win32_PnPEntity | ForEach-Object { [pscustomobject]@{
  DeviceID=$_.DeviceID; Name=$_.Name; Code=[int]$_.ConfigManagerErrorCode; Class=$_.PNPClass
  Manufacturer=$_.Manufacturer; HardwareIds=@($_.HardwareID); CompatibleIds=@($_.CompatibleID) } })
ConvertTo-Json @{Drivers=$drivers; Devices=$devices} -Depth 4 -Compress";

    /// <summary>Combines present devices with their drivers. Problem devices are never hidden.</summary>
    public static List<DeviceDriver> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        static List<string> Texts(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToList()
                : new();

        var drivers = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in document.RootElement.GetProperty("Drivers").EnumerateArray())
            if (Text(d, "DeviceID") is { Length: > 0 } id) drivers[id] = d;

        var result = new List<DeviceDriver>();
        foreach (var device in document.RootElement.GetProperty("Devices").EnumerateArray())
        {
            var id = Text(device, "DeviceID");
            var code = device.TryGetProperty("Code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            var hasDriver = drivers.TryGetValue(id, out var driver) && Text(driver, "Version").Length > 0;
            if (!hasDriver && code == 0) continue;   // software entries without a driver
            string Pick(string fromDriver, string fromDevice) =>
                hasDriver && Text(driver, fromDriver).Length > 0 ? Text(driver, fromDriver) : Text(device, fromDevice);
            var name = Pick("Name", "Name");
            DateTime? date = hasDriver && DateTime.TryParseExact(Text(driver, "Date"), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            result.Add(new DeviceDriver
            {
                Name = name.Length > 0 ? name : $"Unnamed device ({id})",
                Version = hasDriver ? Text(driver, "Version") : "", Date = date,
                Provider = hasDriver ? Text(driver, "Provider") : "",
                Manufacturer = Pick("Manufacturer", "Manufacturer"), Class = Pick("Class", "Class"),
                InfName = hasDriver ? Text(driver, "Inf") : "", ProblemCode = code,
                HardwareIds = Texts(device, "HardwareIds"), CompatibleIds = Texts(device, "CompatibleIds")
            });
        }

        // Generic names such as "USB Composite Device" cover different hardware, so the id is part of the key.
        return result.GroupBy(d => (d.Name, d.Version, d.InfName, d.ProblemCode,
                                    (d.HardwareIds.FirstOrDefault() ?? d.Name).ToUpperInvariant())).Select(g =>
            {
                var first = g.First();
                first.Count = g.Count();
                return first;
            })
            .OrderBy(d => d.Health).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Attaches each offered update to the devices it targets. Exact hardware ids are preferred over
    /// compatible ids. Returns offers that match no listed device.
    /// </summary>
    public static List<OfferedUpdate> Match(IReadOnlyList<DeviceDriver> drivers, IReadOnlyList<OfferedUpdate> offers)
    {
        foreach (var d in drivers) (d.WindowsUpdate, d.OfferIsNotNewer) = (null, false);
        var unmatched = new List<OfferedUpdate>();
        foreach (var offer in offers.OrderByDescending(o => o.Date))
        {
            bool Has(List<string> ids) => ids.Contains(offer.HardwareId, StringComparer.OrdinalIgnoreCase);
            var matches = drivers.Where(d => Has(d.HardwareIds)).ToList();
            if (matches.Count == 0) matches = drivers.Where(d => Has(d.CompatibleIds)).ToList();
            if (offer.HardwareId.Length == 0 || matches.Count == 0) { unmatched.Add(offer); continue; }
            foreach (var d in matches.Where(d => d.WindowsUpdate == null))
            {
                d.WindowsUpdate = offer;
                d.OfferIsNotNewer = d.ProblemCode == 0 && d.Date != null && offer.Date != null &&
                                    offer.Date.Value.Date <= d.Date.Value.Date;
            }
        }
        return unmatched;
    }
}
