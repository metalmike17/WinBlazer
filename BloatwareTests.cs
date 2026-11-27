using System.Text;
using System.Text.Json;
using PCOptimizer.Services.Bloatware;
using PCOptimizer.Services.DriverTools;

namespace PCOptimizer;

/// <summary>Uses a fake package system. No app is removed or installed.</summary>
public static class BloatwareTests
{
    private static AppPackage Package(string name, string kind = "Store", bool locked = false, bool framework = false,
        string location = "") => new(name, name + "_1.0_x64__abc", name + "_abc", location, kind, locked, framework);

    public static void Run(string work, Action<string, bool> check)
    {
        var folder = Directory.CreateDirectory(Path.Combine(work, "bloat")).FullName;
        var appFiles = Directory.CreateDirectory(Path.Combine(folder, "news-app", "sub")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(appFiles, "a.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(appFiles, "sub", "b.bin"), new byte[500]);
        var data = Directory.CreateDirectory(Path.Combine(folder, "data", "Microsoft.BingNews_abc"));
        File.WriteAllBytes(Path.Combine(data.FullName, "c.bin"), new byte[200]);

        var installed = new List<AppPackage>
        {
            Package("Microsoft.BingNews", location: appFiles),
            Package("Microsoft.MicrosoftSolitaireCollection"),
            Package("Microsoft.GetHelp", locked: true),
            Package("Microsoft.Getstarted", kind: "System"),
            Package("Microsoft.WindowsStore"), Package("Microsoft.SecHealthUI"),
            Package("Microsoft.DesktopAppInstaller"), Package("Microsoft.VCLibs.140.00", framework: true),
            Package("Some.Unknown.App")
        };
        var scripts = new List<string>();
        bool removalWorks = true, restoreWorks = true, running = false;
        ToolOutput Fake(string program, string[] args, TimeSpan limit)
        {
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
            if (script.Contains("Get-AppxPackage")) return new(0, JsonSerializer.Serialize(installed));
            scripts.Add(script);
            if (script.Contains("Remove-AppxPackage") && removalWorks)
                installed.RemoveAll(p => script.Contains($"'{p.FullName}'"));
            if (script.Contains("-RegisterByFamilyName") && restoreWorks)
                installed.Add(Package("Microsoft.BingNews", location: appFiles));
            return new(0, "");
        }
        var record = Path.Combine(folder, "removed-apps.json");
        var service = new AppxService(record, Fake, _ => running, Path.Combine(folder, "data"));

        var snapshot = service.Read();
        var listed = snapshot.Entries.Where(e => e.Installed).Select(e => e.App.PackageName).ToList();
        check("only removable catalog apps are listed", snapshot.StoreInstalled && listed.Count == 2 &&
              listed.Contains("Microsoft.BingNews") && !snapshot.Entries.Any(e => e.App.PackageName == "Microsoft.GetHelp"
                  || e.App.PackageName == "Microsoft.Getstarted"));
        string[] critical = { "Microsoft.WindowsStore", "Microsoft.SecHealthUI", "Microsoft.DesktopAppInstaller",
            "Microsoft.WindowsTerminal", "Microsoft.MicrosoftEdge", "Microsoft.MicrosoftEdge.Stable",
            "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.", "Microsoft.WindowsAppRuntime",
            "MicrosoftWindows.", "windows.immersivecontrolpanel", "Microsoft.GamingServices",
            "Microsoft.StorePurchaseApp", "Microsoft.HEVC", "Microsoft.Winget" };
        check("the catalog contains no critical package", !AppCatalog.All.Any(a =>
              critical.Any(c => a.PackageName.StartsWith(c, StringComparison.OrdinalIgnoreCase))));
        check("the preset leaves out Xbox, everyday tools and third-party apps", AppCatalog.All.Any(a => a.InPreset) &&
              !AppCatalog.All.Any(a => a.InPreset && a.Category is "Xbox" or "Everyday tools" or "Third-party"));
        check("names that could alter a script are refused", !AppxService.IsRemovable(
              Package("Microsoft.BingNews") with { FullName = "x'; Remove-Item C:\\ #" }));

        var news = snapshot.Entries.First(e => e.App.PackageName == "Microsoft.BingNews");
        service.Measure(news);
        check("app and data sizes are measured", news.AppBytes == 1500 && news.DataBytes == 200);

        running = true;
        check("a running app is not removed", !service.Remove(news).Ok && scripts.Count == 0);
        running = false;
        removalWorks = false;
        check("a removal that did not happen is reported and not recorded",
              !service.Remove(news).Ok && service.Records().Count == 0);
        removalWorks = true;
        scripts.Clear();
        var removed = service.Remove(news);
        check("an app is removed for the current user only and recorded", removed.Ok && service.Records().Count == 1 &&
              scripts.Single().Contains("Remove-AppxPackage -Package 'Microsoft.BingNews_1.0_x64__abc'") &&
              !scripts.Single().Contains("AllUsers") && !scripts.Single().Contains("Provisioned"));
        check("a removed app is offered for reinstall",
              service.Read().Entries.First(e => e.App.PackageName == "Microsoft.BingNews") is { Installed: false, Removed: not null });
        check("a stale entry is not acted on", !service.Remove(news).Ok);

        restoreWorks = false;
        check("a failed restore keeps the record",
              !service.Reregister("Microsoft.BingNews_abc", "News").Ok && service.Records().Count == 1);
        restoreWorks = true;
        check("a restored app is confirmed and its record cleared",
              service.Reregister("Microsoft.BingNews_abc", "News").Ok && service.Records().Count == 0);

        var blockedPath = Directory.CreateDirectory(Path.Combine(folder, "blocked.json")).FullName;
        var cannotSave = new AppxService(blockedPath, Fake, _ => false, Path.Combine(folder, "data"));
        scripts.Clear();
        var threw = false;
        try { cannotSave.Remove(news); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { threw = true; }
        check("nothing is removed if the record cannot be saved", threw && scripts.Count == 0);

        var app = AppCatalog.Find("Microsoft.BingNews")!;
        check("store links depend on whether the Store is installed",
              AppxService.StoreLink(app, "Microsoft.BingNews_abc", true) == "ms-windows-store://pdp/?PFN=Microsoft.BingNews_abc" &&
              AppxService.StoreLink(app, null, false).StartsWith("https://apps.microsoft.com/search?query="));
    }
}
