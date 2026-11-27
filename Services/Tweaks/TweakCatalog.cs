using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer.Services.Tweaks;

/// <summary>
/// Rating rule: Safe is only for settings of your own account that need no administrator rights, use no
/// policy keys and are easy to see and undo.
/// </summary>
public static class TweakCatalog
{
    private const string Cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";
    private const string Advanced = Cv + @"Explorer\Advanced";
    private const string Content = Cv + "ContentDeliveryManager";
    private const string Desktop = @"Control Panel\Desktop";
    private const string Multimedia = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string Policies = @"SOFTWARE\Policies\Microsoft\";
    private const string Control = @"SYSTEM\CurrentControlSet\Control";
    private const string SignOut = "after sign-out";
    private const string Restart = "after restart";
    private const string Managed = " Uses a policy; Windows or the app may say it is managed.";

    private const string Sleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    private const string Usb = "2a737441-1930-4402-8d77-b2bebba308a3";

    private static RegistryValue Mine(string path, string name, object value) => new(Hive.CurrentUser, path, name, value);
    private static RegistryValue Machine(string path, string name, object value) => new(Hive.LocalMachine, path, name, value);

    private static Tweak Registry(TweakCategory category, TweakRisk risk, string id, string name, string description,
        string? effect, params RegistryValue[] values) => new RegistryTweak
    {
        Id = id, Category = category, Risk = risk, Name = name, Description = description, Effect = effect,
        Values = values,
        NeedsAdmin = values.Any(v => v.Hive == Hive.LocalMachine ||
                                     v.Path.StartsWith(@"SOFTWARE\Policies", StringComparison.OrdinalIgnoreCase))
    };

    private static Tweak Service(TweakCategory category, TweakRisk risk, string id, string service, string name,
        string description) => new ServiceTweak
    {
        Id = id, Category = category, Risk = risk, Name = name, Description = description, Service = service,
        NeedsAdmin = true, Effect = Restart
    };

    private static Tweak Power(TweakRisk risk, string id, string group, string setting, uint target, string name,
        string description) => new PowerTweak
    {
        Id = id, Category = TweakCategory.Power, Risk = risk, Name = name,
        Description = description + " Plugged-in value of the current power plan only.",
        Group = new Guid(group), Setting = new Guid(setting), Target = target
    };

    public static List<Tweak> Create()
    {
        const TweakRisk safe = TweakRisk.Safe, moderate = TweakRisk.Moderate, risky = TweakRisk.Risky;
        const TweakCategory perf = TweakCategory.Performance, net = TweakCategory.Network,
                            visual = TweakCategory.Visual, privacy = TweakCategory.Privacy;
        return new List<Tweak>
        {
            // ---- Performance ----
            Registry(perf, moderate, "background-apps", "Turn off Store app background activity",
                "Some apps may stop updating or notifying. Windows 11 may ignore this for some apps.", SignOut,
                Mine(Cv + "BackgroundAccessApplications", "GlobalUserDisabled", 1),
                Mine(Cv + "Search", "BackgroundAppGlobalToggle", 0)),
            Service(perf, moderate, "sysmain", "SysMain", "Disable SysMain (Superfetch)",
                "SysMain preloads commonly used data. On most PCs disabling it does not help and can slow app starts."),
            Service(perf, risky, "search-index", "WSearch", "Disable Windows Search indexing",
                "Start, Explorer and mail searches become slower or incomplete."),
            Registry(perf, moderate, "priority-separation", "Favor foreground program priority",
                "Sets Win32PrioritySeparation to 38. Close to normal desktop behavior; expect no clear difference.", Restart,
                Machine(Control + @"\PriorityControl", "Win32PrioritySeparation", 38)),
            Registry(perf, moderate, "system-responsiveness", "Multimedia scheduler: reserve less for background work",
                "Sets SystemResponsiveness to 10. Any gaming benefit is usually too small to measure.", Restart,
                Machine(Multimedia, "SystemResponsiveness", 10)),
            Registry(perf, moderate, "games-profile", "Multimedia scheduler: raise the Games profile",
                "Affects only software that registers with this scheduler profile. Benefit is unproven.", Restart,
                Machine(Multimedia + @"\Tasks\Games", "GPU Priority", 8),
                Machine(Multimedia + @"\Tasks\Games", "Priority", 6),
                Machine(Multimedia + @"\Tasks\Games", "Scheduling Category", "High")),
            Registry(perf, moderate, "startup-delay", "Remove the startup app delay",
                "Startup apps begin sooner, but may compete with each other right after sign-in.", SignOut,
                Mine(Cv + @"Explorer\Serialize", "StartupDelayInMSec", 0)),
            Registry(perf, safe, "menu-delay", "Faster classic menus",
                "Shortens the delay before submenus open in classic menus to 100 ms.", SignOut,
                Mine(Desktop, "MenuShowDelay", "100")),
            Registry(perf, safe, "windows-game-mode", "Turn on Windows Game Mode",
                "Windows' own setting that gives games priority over some background activity.", null,
                Mine(@"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled", 1)),
            Registry(perf, moderate, "game-dvr", "Turn off Game DVR capture",
                "Stops background game recording. You lose Game Bar recording clips.", "when a game next starts",
                Mine(@"System\GameConfigStore", "GameDVR_Enabled", 0),
                Mine(Cv + "GameDVR", "AppCaptureEnabled", 0)),
            Registry(perf, moderate, "gpu-scheduling", "Turn on hardware-accelerated GPU scheduling",
                "Needs a supported GPU and driver. It can help or hurt depending on the game.", Restart,
                Machine(Control + @"\GraphicsDrivers", "HwSchMode", 2)),
            Registry(perf, moderate, "edge-startup-boost", "Turn off Edge startup boost",
                "Edge no longer preloads at sign-in, so its first launch may be slower." + Managed, null,
                Machine(Policies + "Edge", "StartupBoostEnabled", 0)),
            Registry(perf, moderate, "edge-background", "Stop Edge running after it is closed",
                "Edge extensions and web apps stop working in the background." + Managed, null,
                Machine(Policies + "Edge", "BackgroundModeEnabled", 0)),
            Registry(perf, risky, "app-shutdown-wait", "Wait less for apps at shutdown",
                "Apps get 5 seconds to close. A slow app may lose unsaved work.", SignOut,
                Mine(Desktop, "WaitToKillAppTimeout", "5000")),
            Registry(perf, risky, "service-shutdown-wait", "Wait less for services at shutdown",
                "Services get 2 seconds to stop. Databases or sync tools may not finish writing.", Restart,
                Machine(Control, "WaitToKillServiceTimeout", "2000")),

            // ---- Network ----
            new NagleTweak
            {
                Id = "nagle", Category = net, Risk = moderate, NeedsAdmin = true, Effect = Restart,
                Name = "TCP: send small packets immediately",
                Description = "Sets TcpAckFrequency and TCPNoDelay on adapters in use. Most games are unaffected " +
                              "(many use UDP), and network overhead can rise."
            },
            Registry(net, moderate, "network-throttling", "Remove multimedia network throttling",
                "Lifts the packet limit used while media plays. Can cause audio glitches; rarely helps.", Restart,
                Machine(Multimedia, "NetworkThrottlingIndex", -1)),
            Registry(net, moderate, "smb-throttling", "Turn off file sharing bandwidth throttling",
                "May help large copies over slow, high-delay links. No effect on internet browsing.", Restart,
                Machine(@"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "DisableBandwidthThrottling", 1)),
            new DnsTweak
            {
                Id = "dns-cloudflare", Category = net, Risk = risky, NeedsAdmin = true,
                Name = "DNS: use Cloudflare (1.1.1.1)",
                Description = "Changes IPv4 DNS on physical adapters in use. Can break work, school, router or " +
                              "content-filter names. Cloudflare receives your DNS lookups. IPv6 DNS is not changed."
            },
            Registry(net, moderate, "dns-negative-cache", "DNS: do not cache failed lookups",
                "A fixed website is retried sooner. Causes more repeat lookups.", Restart,
                Machine(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxNegativeCacheTtl", 0)),
            Registry(net, moderate, "llmnr", "DNS: turn off LLMNR",
                "A security measure. Some devices on your local network may no longer be found by name." + Managed, Restart,
                Machine(Policies + @"Windows NT\DNSClient", "EnableMulticast", 0)),
            Registry(net, moderate, "delivery-p2p", "Stop sharing Windows updates with other PCs",
                "Updates download directly, without peer sharing. May use more internet data." + Managed, null,
                Machine(Policies + @"Windows\DeliveryOptimization", "DODownloadMode", 0)),
            Registry(net, moderate, "remote-assistance", "Turn off Remote Assistance",
                "Helpers can no longer connect through Windows Remote Assistance.", null,
                Machine(Control + @"\Remote Assistance", "fAllowToGetHelp", 0)),

            // ---- Power ----
            new HibernateTweak
            {
                Id = "hibernation", Category = TweakCategory.Power, Risk = moderate, NeedsAdmin = true,
                Name = "Turn off hibernation",
                Description = "Frees the hibernation file's disk space. Hibernate and Fast Startup stop working; open " +
                              "work cannot be saved if power runs out during sleep."
            },
            Registry(TweakCategory.Power, moderate, "fast-startup", "Turn off Fast Startup",
                "Shut down fully resets Windows, which can fix driver problems. Booting may be slower.", "at next shutdown",
                Machine(Control + @"\Session Manager\Power", "HiberbootEnabled", 0)),
            Registry(TweakCategory.Power, risky, "power-throttling", "Turn off Windows power throttling",
                "More heat and power use. Also limits Efficiency mode, including this app's Background App Limiter.", Restart,
                Machine(Control + @"\Power\PowerThrottling", "PowerThrottlingOff", 1)),
            Power(moderate, "usb-suspend", Usb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0,
                "USB: turn off selective suspend", "Can stop some USB devices from disconnecting; uses more power."),
            Power(moderate, "usb3-link", Usb, "d4e98f31-5ffe-4ce1-be31-1b38b384c009", 0,
                "USB 3: turn off link power saving", "For USB 3 devices that drop out; uses more power."),
            Power(moderate, "pcie-link", "501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5", 0,
                "PCI Express: turn off link power saving", "Uses more power and may add heat."),
            Power(moderate, "disk-timeout", "0012ee47-9041-4b5d-9b77-535fba8b1442", "6738e2c4-e8a5-4a42-b16a-e040e769756e", 0,
                "Never turn off hard disks", "Avoids spin-up pauses; uses more power."),
            Power(moderate, "sleep-timeout", Sleep, "29f6c1db-86da-48c5-9fdb-f2b67b1f44da", 0,
                "Never sleep automatically", "The PC stays awake until you act; uses much more power when idle."),
            Power(moderate, "hybrid-sleep", Sleep, "94ac6d29-73ce-41a6-809f-6363ba21b47e", 0,
                "Turn off hybrid sleep", "Open work is no longer also saved to disk when sleeping."),
            Power(moderate, "wake-timers", Sleep, "bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d", 0,
                "Turn off wake timers", "Scheduled tasks, including maintenance, will not wake the PC."),
            Power(moderate, "wireless-power", "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1", "12bbebe6-58d6-4636-95bb-3217ef867c1a", 0,
                "Wi-Fi: maximum performance", "May help an unstable Wi-Fi link; uses more power."),
            Power(risky, "cpu-minimum", "54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c", 100,
                "Keep processor minimum state at 100%", "More heat, fan noise and power use, usually for little gain."),

            // ---- Visual effects ----
            Registry(visual, safe, "minimize-animation", "Turn off minimize and maximize animation",
                "Windows open and close without the sliding effect.", SignOut,
                Mine(Desktop + @"\WindowMetrics", "MinAnimate", "0")),
            Registry(visual, safe, "taskbar-animation", "Turn off taskbar animations",
                "Removes taskbar motion effects.", SignOut, Mine(Advanced, "TaskbarAnimations", 0)),
            Registry(visual, safe, "transparency", "Turn off transparency",
                "Solid taskbar, Start menu and window backgrounds.", SignOut,
                Mine(Cv + @"Themes\Personalize", "EnableTransparency", 0)),
            Registry(visual, safe, "aero-peek", "Turn off Aero Peek",
                "Hovering the taskbar corner no longer previews the desktop.", SignOut,
                Mine(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0)),
            Registry(visual, safe, "saved-thumbnails", "Do not keep saved taskbar thumbnails",
                "Frees a little memory; some previews may redraw.", SignOut,
                Mine(@"Software\Microsoft\Windows\DWM", "AlwaysHibernateThumbnails", 0)),
            Registry(visual, safe, "selection-effect", "Plain selection rectangle",
                "Removes the translucent selection box in file lists.", SignOut, Mine(Advanced, "ListviewAlphaSelect", 0)),
            Registry(visual, safe, "label-shadows", "Turn off desktop label shadows",
                "Desktop icon names lose their drop shadow.", SignOut, Mine(Advanced, "ListviewShadow", 0)),
            Registry(visual, safe, "drag-outline", "Show only an outline while dragging windows",
                "Window contents are hidden while you drag.", SignOut, Mine(Desktop, "DragFullWindows", "0")),
            Registry(visual, moderate, "signin-blur", "Turn off sign-in screen blur",
                "The sign-in background is shown sharp." + Managed, SignOut,
                Machine(Policies + @"Windows\System", "DisableAcrylicBackgroundOnLogon", 1)),

            // ---- Privacy ----
            Registry(privacy, safe, "advertising-id", "Turn off the advertising ID",
                "Apps cannot use your Windows advertising ID. You still see ads.", null,
                Mine(Cv + "AdvertisingInfo", "Enabled", 0)),
            Registry(privacy, safe, "tailored-experiences", "Turn off tailored experiences",
                "Diagnostic data is not used to personalize tips and ads.", null,
                Mine(Cv + "Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)),
            Registry(privacy, safe, "start-suggestions", "Turn off Start menu suggestions",
                "Hides suggested apps where Windows honors this setting.", SignOut,
                Mine(Content, "SystemPaneSuggestionsEnabled", 0),
                Mine(Content, "SubscribedContent-338388Enabled", 0)),
            Registry(privacy, safe, "start-recommendations", "Turn off Start tips and app promotions",
                "Reduces recommendations for tips, shortcuts and new apps.", SignOut,
                Mine(Advanced, "Start_IrisRecommendations", 0)),
            Registry(privacy, safe, "windows-tips", "Turn off Windows tips",
                "Stops tips and suggestions while using Windows.", SignOut,
                Mine(Content, "SoftLandingEnabled", 0), Mine(Content, "SubscribedContent-338389Enabled", 0)),
            Registry(privacy, safe, "settings-suggestions", "Turn off suggestions in Settings",
                "Hides suggested content in the Settings app.", null,
                Mine(Content, "SubscribedContent-338393Enabled", 0),
                Mine(Content, "SubscribedContent-353694Enabled", 0),
                Mine(Content, "SubscribedContent-353696Enabled", 0)),
            Registry(privacy, safe, "welcome-experience", "Turn off the post-update welcome screen",
                "Skips the 'what's new' screen after updates.", null,
                Mine(Content, "SubscribedContent-310093Enabled", 0)),
            Registry(privacy, safe, "silent-apps", "Stop automatic suggested app installs",
                "Asks Windows not to add promoted apps on its own.", null,
                Mine(Content, "SilentInstalledAppsEnabled", 0)),
            Registry(privacy, safe, "lockscreen-tips", "Turn off lock screen tips",
                "Hides facts and tips on the lock screen.", null,
                Mine(Content, "RotatingLockScreenOverlayEnabled", 0),
                Mine(Content, "SubscribedContent-338387Enabled", 0)),
            Registry(privacy, safe, "explorer-ads", "Turn off File Explorer provider ads",
                "Hides sync provider promotions. Some sync notices may also be hidden.", SignOut,
                Mine(Advanced, "ShowSyncProviderNotifications", 0)),
            Registry(privacy, safe, "finish-setup", "Turn off 'finish setting up' reminders",
                "Stops full-screen setup suggestions.", null,
                Mine(Cv + "UserProfileEngagement", "ScoobeSystemSettingEnabled", 0)),
            Registry(privacy, safe, "feedback-prompts", "Turn off feedback prompts",
                "Windows stops asking for feedback.", null,
                Mine(@"SOFTWARE\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0)),
            Registry(privacy, safe, "typing-collection", "Limit typing and inking personalization",
                "Stops collecting typing and handwriting samples. Personal suggestions may get worse.", SignOut,
                Mine(@"SOFTWARE\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                Mine(@"SOFTWARE\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1)),
            Registry(privacy, safe, "language-list", "Hide your language list from websites",
                "Websites may choose a less suitable language.", null,
                Mine(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1)),
            Registry(privacy, moderate, "app-tracking", "Turn off app launch tracking",
                "Start and search lose 'most used' and some recent results.", SignOut,
                Mine(Advanced, "Start_TrackProgs", 0)),
            Registry(privacy, moderate, "telemetry-policy", "Telemetry: request the lowest level",
                "Level 0 applies to Enterprise, Education and IoT editions; others still send required data. " +
                "Can affect Insider and management features." + Managed, Restart,
                Machine(Policies + @"Windows\DataCollection", "AllowTelemetry", 0)),
            Service(privacy, moderate, "diagtrack", "DiagTrack", "Telemetry: disable the DiagTrack service",
                "Stops the main diagnostics service. Some feedback and troubleshooting features may not work."),
            Registry(privacy, moderate, "activity-history", "Turn off activity history",
                "Apps stop publishing and uploading activity history." + Managed, Restart,
                Machine(Policies + @"Windows\System", "PublishUserActivities", 0),
                Machine(Policies + @"Windows\System", "UploadUserActivities", 0)),
            Registry(privacy, moderate, "web-search", "Turn off web suggestions in Start search",
                "Start search stops showing web suggestions." + Managed, SignOut,
                Mine(Policies + @"Windows\Explorer", "DisableSearchBoxSuggestions", 1)),
        };
    }
}
