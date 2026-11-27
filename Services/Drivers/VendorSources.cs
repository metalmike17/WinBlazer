using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PCOptimizer.Services.DriverTools;

/// <summary>
/// NVIDIA's website lookup (not a documented public API, so it may change). Only the graphics card model
/// and Windows version are sent. Nothing is downloaded or installed from it.
/// </summary>
public static class NvidiaLookup
{
    private const string Products = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";

    public static bool IsNvidiaDisplay(DeviceDriver d) => d.Class.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) &&
        d.HardwareIds.Any(h => h.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase));

    /// <summary>Windows version 32.0.16.2140 is NVIDIA version 621.40.</summary>
    public static string? MarketingVersion(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length != 4 || parts[2].Length == 0 || parts[3].Length != 4 ||
            !(parts[2] + parts[3]).All(char.IsAsciiDigit)) return null;
        var digits = parts[2][^1] + parts[3];
        return $"{digits[..3]}.{digits[3..]}";
    }

    public static bool? IsNewer(string latest, string installed) =>
        System.Version.TryParse(latest, out var a) && System.Version.TryParse(installed, out var b) ? a > b : null;

    /// <summary>Product and series numbers for an exactly matching model name.</summary>
    public static (int Series, int Product)? FindProduct(string xml, string deviceName)
    {
        static string Plain(string name) => Regex.Replace(name, @"^NVIDIA\s+", "", RegexOptions.IgnoreCase).Trim();
        foreach (var value in XDocument.Parse(xml).Descendants("LookupValue"))
            if (Plain((string?)value.Element("Name") ?? "").Equals(Plain(deviceName), StringComparison.OrdinalIgnoreCase) &&
                int.TryParse((string?)value.Attribute("ParentID"), out var series) &&
                int.TryParse((string?)value.Element("Value"), out var product))
                return (series, product);
        return null;
    }

    public static VendorResult? ParseResult(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("IDS", out var list) || list.ValueKind != JsonValueKind.Array ||
            list.GetArrayLength() == 0 || !list[0].TryGetProperty("downloadInfo", out var info)) return null;
        string Text(string name) => info.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        var version = Text("Version");
        if (!System.Version.TryParse(version, out _)) return null;
        DateTime? released = DateTime.TryParseExact(Text("ReleaseDateTime"), "ddd MMM dd, yyyy",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        var page = Text("DetailsURL");
        return new VendorResult(version, released, VendorPages.IsOfficial(page) ? page : VendorPages.Nvidia);
    }

    public static async Task<VendorResult> LatestAsync(DeviceDriver gpu, CancellationToken token = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 20_000_000 };
        var products = await client.GetStringAsync(Products, token);
        var (series, product) = FindProduct(products, gpu.Name)
            ?? throw new IOException($"NVIDIA's list has no exact match for \"{gpu.Name}\".");
        // 135 is Windows 11 and 57 is 64-bit Windows 10. WHQL "Game Ready" (DCH) drivers only.
        var system = Environment.OSVersion.Version.Build >= 22000 ? 135 : 57;
        var url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                  $"?func=DriverManualLookup&psid={series}&pfid={product}&osID={system}&languageCode=1033" +
                  "&isWHQL=1&dch=1&sort1=0&numberOfResults=1";
        return ParseResult(await client.GetStringAsync(url, token))
               ?? throw new IOException("NVIDIA did not return a driver.");
    }
}

/// <summary>Official download pages, for vendors with no lookup this app can use.</summary>
public static class VendorPages
{
    public const string Nvidia = "https://www.nvidia.com/en-us/drivers/";

    private static readonly (string Word, string Url)[] Pages =
    {
        ("nvidia", Nvidia),
        ("advanced micro devices", "https://www.amd.com/en/support/download/drivers.html"),
        ("amd", "https://www.amd.com/en/support/download/drivers.html"),
        ("intel", "https://www.intel.com/content/www/us/en/support/detect.html"),
        ("realtek", "https://www.realtek.com/Download/Index"),
        ("steelseries", "https://steelseries.com/gg"),
        ("logitech", "https://support.logi.com/")
    };

    public static string? For(DeviceDriver driver) => Pages.FirstOrDefault(p => Regex.IsMatch(
        driver.Provider + " " + driver.Manufacturer, $@"\b{p.Word}\b", RegexOptions.IgnoreCase)).Url;

    public static bool IsOfficial(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        new[] { "nvidia.com", "amd.com", "intel.com", "realtek.com", "steelseries.com", "logi.com" }
            .Any(host => uri.Host == host || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
}
