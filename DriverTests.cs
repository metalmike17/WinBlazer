using PCOptimizer.Services.DriverTools;

namespace PCOptimizer;

/// <summary>Backup and update use a fake tool runner and installer. No driver is exported or installed.</summary>
public static class DriverTests
{
    private sealed class FakeUpdater : IDriverUpdater
    {
        public int Installs;
        public string? License(string id) => null;
        public InstallResult Install(string id, bool accept, Action<string> progress)
        {
            Installs++;
            return new(true, false, "fake");
        }
    }

    public static void Run(string work, Action<string, bool> check)
    {
        const string json = """
        {"Drivers":[
          {"DeviceID":"A","Name":"Card","Version":"32.0.16.2140","Provider":"NVIDIA","Inf":"oem5.inf","Class":"DISPLAY","Date":"2026-11-20"},
          {"DeviceID":"B","Name":"Pad","Version":"1.0","Provider":"Microsoft","Inf":"input.inf","Class":"HIDCLASS","Date":"2006-06-21"},
          {"DeviceID":"B2","Name":"Pad","Version":"1.0","Provider":"Microsoft","Inf":"input.inf","Class":"HIDCLASS","Date":"2006-06-21"},
          {"DeviceID":"Z","Name":"Not present","Version":"1.0","Inf":"x.inf"}],
         "Devices":[
          {"DeviceID":"A","Name":"Card","Code":0,"Class":"Display","HardwareIds":["PCI\\VEN_10DE&DEV_1"],"CompatibleIds":[]},
          {"DeviceID":"B","Name":"Pad","Code":0,"HardwareIds":["USB\\VID_1&PID_2"],"CompatibleIds":["USB\\Class_03"]},
          {"DeviceID":"B2","Name":"Pad","Code":0,"HardwareIds":["USB\\VID_1&PID_2"],"CompatibleIds":[]},
          {"DeviceID":"M","Name":null,"Code":28,"HardwareIds":[],"CompatibleIds":[]},
          {"DeviceID":"E","Name":"Broken USB","Code":43,"HardwareIds":["USB\\E"],"CompatibleIds":[]},
          {"DeviceID":"D","Name":"Off","Code":22,"HardwareIds":[],"CompatibleIds":[]},
          {"DeviceID":"S","Name":"Software entry","Code":0,"HardwareIds":[],"CompatibleIds":[]}]}
        """;
        var list = DriverInventory.Parse(json);
        DeviceDriver Get(string name) => list.First(d => d.Name.StartsWith(name));
        check("drivers are listed with versions, dates and health", list.Count == 5 && Get("Pad").Count == 2 &&
              Get("Card").Date == new DateTime(2026, 11, 20) && Get("Unnamed").Health == DriverHealth.Missing &&
              Get("Broken").Health == DriverHealth.Error && Get("Off").Health == DriverHealth.Disabled);
        check("an old date alone is not outdated", Get("Pad").Health == DriverHealth.Working);
        var generic = DriverInventory.Parse("""
        {"Drivers":[{"DeviceID":"1","Name":"USB Composite Device","Version":"1.0","Inf":"usb.inf"},
                    {"DeviceID":"2","Name":"USB Composite Device","Version":"1.0","Inf":"usb.inf"}],
         "Devices":[{"DeviceID":"1","Code":0,"HardwareIds":["USB\\VID_AAAA&PID_1"],"CompatibleIds":[]},
                    {"DeviceID":"2","Code":0,"HardwareIds":["USB\\VID_1038&PID_160E"],"CompatibleIds":[]}]}
        """);
        var lost = DriverInventory.Match(generic, new[]
            { new OfferedUpdate("k", "Keyboard", @"usb\vid_1038&pid_160e", null, true) });
        check("different hardware with the same generic name stays matchable", generic.Count == 2 && lost.Count == 0);
        check("only oem packages count as third-party", Get("Card").IsThirdParty && !Get("Pad").IsThirdParty);

        var unmatched = DriverInventory.Match(list, new[]
        {
            new OfferedUpdate("1", "Newer pad", @"usb\vid_1&pid_2", new DateTime(2026, 1, 1), true),
            new OfferedUpdate("2", "Older card", @"pci\ven_10de&dev_1", new DateTime(2025, 1, 1), true),
            new OfferedUpdate("3", "Nothing", @"usb\unknown", new DateTime(2026, 1, 1), true),
            new OfferedUpdate("4", "Fix for USB", @"usb\e", new DateTime(2000, 1, 1), true)
        });
        check("Windows Update offers are matched by hardware id", Get("Pad").Health == DriverHealth.Outdated &&
              unmatched.Single().Title == "Nothing" && Get("Broken").WindowsUpdate != null &&
              Get("Broken").Health == DriverHealth.Error && !Get("Broken").OfferIsNotNewer);
        check("an offer that is not newer is not flagged",
              Get("Card").OfferIsNotNewer && Get("Card").Health == DriverHealth.Working);

        check("NVIDIA versions are converted and compared",
              NvidiaLookup.MarketingVersion("32.0.16.2140") == "621.40" &&
              NvidiaLookup.MarketingVersion("31.0.15.3623") == "536.23" &&
              NvidiaLookup.MarketingVersion("bad") == null &&
              NvidiaLookup.IsNewer("621.40", "621.40") == false && NvidiaLookup.IsNewer("622.10", "621.40") == true);
        const string xml = "<LookupValueSearch><LookupValues>" +
            "<LookupValue ParentID=\"131\"><Name>NVIDIA GeForce RTX 5060 Ti</Name><Value>1076</Value></LookupValue>" +
            "<LookupValue ParentID=\"131\"><Name>NVIDIA GeForce RTX 5060</Name><Value>1078</Value></LookupValue>" +
            "</LookupValues></LookupValueSearch>";
        check("the graphics card must match exactly",
              NvidiaLookup.FindProduct(xml, "NVIDIA GeForce RTX 5060") == (131, 1078) &&
              NvidiaLookup.FindProduct(xml, "NVIDIA GeForce RTX 506") == null);
        var parsed = NvidiaLookup.ParseResult("""
        {"Success":"1","IDS":[{"downloadInfo":{"Version":"621.40","ReleaseDateTime":"Tue Nov 24, 2026","DetailsURL":"https://evil.example/x"}}]}
        """);
        check("NVIDIA results are read and foreign links replaced", parsed is { Version: "621.40" } &&
              parsed.Released == new DateTime(2026, 11, 24) && parsed.PageUrl == VendorPages.Nvidia);
        check("only official vendor links are allowed", VendorPages.IsOfficial("https://www.nvidia.com/a") &&
              !VendorPages.IsOfficial("http://www.nvidia.com/a") && !VendorPages.IsOfficial("https://nvidia.com.evil.example/") &&
              !VendorPages.IsOfficial("file:///c:/x.exe"));

        var calls = new List<string>();
        var succeed = true;
        var service = new DriverService((program, args, _) =>
        {
            calls.Add(Path.GetFileName(program) + " " + string.Join(' ', args));
            if (succeed) File.WriteAllText(Path.Combine(args[^1], "fake.inf"), "x");
            return new ToolOutput(succeed ? 0 : 1, "output");
        });
        var parent = Directory.CreateDirectory(Path.Combine(work, "driver-backups")).FullName;
        check("backup needs administrator rights", !service.Backup(parent, list, false).Ok && calls.Count == 0);
        var good = service.Backup(parent, list, true);
        check("backup exports all packages and writes an inventory", good.Ok && good.Packages == 1 &&
              calls[0].StartsWith("pnputil.exe /export-driver * ") &&
              File.ReadAllText(Path.Combine(good.Folder, "installed-drivers.csv")).Contains("Broken USB"));
        succeed = false;
        Thread.Sleep(1100);   // folder names are per second
        var bad = service.Backup(parent, list, true);
        check("a failed export is reported as incomplete", !bad.Ok &&
              File.ReadAllText(Path.Combine(bad.Folder, "HOW-TO-RESTORE.txt")).StartsWith("WARNING"));
        check("spreadsheet formulas are neutralized in the inventory",
              DriverService.InventoryCsv(new[] { new DeviceDriver { Name = "=cmd" } }).Contains("\"'=cmd\""));

        var updater = new FakeUpdater();
        var safety = Path.Combine(work, "safety");
        var third = new DeviceDriver { Name = "T", InfName = "oem9.inf",
            WindowsUpdate = new OfferedUpdate("9", "t", "x", null, true) };
        var blocked = service.Update(third, updater, safety, true, false, _ => { });
        check("nothing is installed if the current driver cannot be saved", !blocked.Ok && updater.Installs == 0);
        succeed = true;
        calls.Clear();
        var done = service.Update(third, updater, safety, true, false, _ => { });
        check("the current driver is saved before installing",
              done.Ok && updater.Installs == 1 && calls.Single().Contains("/export-driver oem9.inf"));
        check("updates need administrator rights",
              !service.Update(third, updater, safety, false, false, _ => { }).Ok && updater.Installs == 1);
    }
}
