namespace PCOptimizer.Services.Bloatware;

/// <summary>One removable app. <paramref name="InPreset"/> marks the Debloat Preset.</summary>
public sealed record CatalogApp(string PackageName, string Title, string Category, bool InPreset, string Note = "");

/// <summary>
/// The only apps this module can ever list or remove. Anything not named here (Windows components,
/// runtimes, the Store, security, winget, unknown apps) is never shown.
/// </summary>
public static class AppCatalog
{
    private const string News = "News and web", Fun = "Games and media", Talk = "Communication",
        Work = "Productivity", Help = "Help and extras", Xbox = "Xbox", Tools = "Everyday tools",
        Other = "Third-party";

    public static readonly IReadOnlyList<CatalogApp> All = new CatalogApp[]
    {
        new("Microsoft.BingNews", "News", News, true),
        new("Microsoft.BingWeather", "Weather", News, true),
        new("Microsoft.BingSearch", "Bing Search", News, true),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", Fun, true, "Locally saved progress may be lost."),
        new("Clipchamp.Clipchamp", "Clipchamp video editor", Fun, true, "Back up projects stored only on this PC."),
        new("Microsoft.ZuneVideo", "Movies & TV", Fun, true),
        new("Microsoft.ZuneMusic", "Media Player / Groove Music", Fun, false, "This is the music and video player in Windows 11."),
        new("Microsoft.MixedReality.Portal", "Mixed Reality Portal", Fun, true),
        new("Microsoft.Microsoft3DViewer", "3D Viewer", Fun, true),
        new("MSTeams", "Microsoft Teams", Talk, true),
        new("MicrosoftTeams", "Microsoft Teams (older personal)", Talk, true),
        new("Microsoft.SkypeApp", "Skype", Talk, true),
        new("Microsoft.People", "People", Talk, true),
        new("microsoft.windowscommunicationsapps", "Mail and Calendar", Talk, false, "Check your mail is on the server first."),
        new("Microsoft.OutlookForWindows", "Outlook (new)", Talk, false, "You may use this for email."),
        new("Microsoft.YourPhone", "Phone Link", Talk, false, "Needed to link a phone to this PC."),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 hub", Work, true, "This is the launcher, not the Office programs."),
        new("Microsoft.Todos", "Microsoft To Do", Work, true),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", Work, true),
        new("Microsoft.Office.OneNote", "OneNote (Store version)", Work, false, "Sync your notebooks first."),
        new("Microsoft.MicrosoftStickyNotes", "Sticky Notes", Work, false, "Unsynced notes are deleted."),
        new("Microsoft.Windows.DevHome", "Dev Home", Work, true),
        new("Microsoft.Copilot", "Copilot", Help, true),
        new("Microsoft.549981C3F5F10", "Cortana", Help, true),
        new("Microsoft.GetHelp", "Get Help", Help, true, "Some Windows troubleshooters open in this app."),
        new("Microsoft.Getstarted", "Tips", Help, true),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub", Help, true),
        new("Microsoft.WindowsMaps", "Maps", Help, true),
        new("MicrosoftCorporationII.MicrosoftFamily", "Family", Help, false),
        new("MicrosoftCorporationII.QuickAssist", "Quick Assist", Help, false, "Used for remote help."),
        new("Microsoft.GamingApp", "Xbox app", Xbox, false, "Needed for PC Game Pass."),
        new("Microsoft.XboxApp", "Xbox Console Companion", Xbox, false),
        new("Microsoft.XboxGamingOverlay", "Xbox Game Bar", Xbox, false, "Some games and capture shortcuts use it."),
        new("Microsoft.XboxGameOverlay", "Xbox Game Overlay plugin", Xbox, false),
        new("Microsoft.XboxSpeechToTextOverlay", "Xbox speech to text", Xbox, false),
        new("Microsoft.XboxIdentityProvider", "Xbox sign-in", Xbox, false, "Games that use Xbox sign-in stop signing in without it."),
        new("Microsoft.Xbox.TCUI", "Xbox in-game interface", Xbox, false, "Some games need it for friends and invites."),
        new("Microsoft.WindowsCalculator", "Calculator", Tools, false),
        new("Microsoft.Windows.Photos", "Photos", Tools, false),
        new("Microsoft.WindowsCamera", "Camera", Tools, false),
        new("Microsoft.Paint", "Paint", Tools, false),
        new("Microsoft.WindowsNotepad", "Notepad", Tools, false, "Save open tabs first."),
        new("Microsoft.ScreenSketch", "Snipping Tool", Tools, false),
        new("Microsoft.WindowsAlarms", "Clock", Tools, false, "Alarms and timers are lost."),
        new("Microsoft.WindowsSoundRecorder", "Sound Recorder", Tools, false, "Copy your recordings first."),
        new("SpotifyAB.SpotifyMusic", "Spotify", Other, false),
        new("4DF9E0F8.Netflix", "Netflix", Other, false),
        new("AmazonVideo.PrimeVideo", "Prime Video", Other, false),
        new("Disney.37853FC22B2CE", "Disney+", Other, false),
        new("BytedancePte.Ltd.TikTok", "TikTok", Other, false),
        new("FACEBOOK.FACEBOOK", "Facebook", Other, false),
        new("Facebook.InstagramBeta", "Instagram", Other, false),
        new("7EE7776C.LinkedInforWindows", "LinkedIn", Other, false),
        new("king.com.CandyCrushSaga", "Candy Crush Saga", Other, false)
    };

    private static readonly Dictionary<string, CatalogApp> ByName =
        All.ToDictionary(a => a.PackageName, StringComparer.OrdinalIgnoreCase);

    public static CatalogApp? Find(string packageName) => ByName.GetValueOrDefault(packageName);
}
