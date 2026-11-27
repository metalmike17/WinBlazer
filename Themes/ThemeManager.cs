using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PCOptimizer.Themes;

/// <summary>
/// The light and dark palettes. Code gets "live" brushes whose color changes in place; XAML uses
/// DynamicResource, so the live brushes are never put in styles (WPF would freeze them there).
/// Switching therefore keeps every page, and anything running on it, as it is.
/// </summary>
public static class ThemeManager
{
    private static readonly Dictionary<string, (string Dark, string Light)> Palette = new()
    {
        ["NavyBrush"] = ("#0A0E1A", "#EDF0F8"),
        ["TextBrush"] = ("#E6EDFF", "#141A2E"),
        ["MutedBrush"] = ("#8B98BF", "#4A5578"),
        ["AccentBrush"] = ("#00D4FF", "#00678F"),
        ["VioletBrush"] = ("#7C3AED", "#5B21B6"),
        ["GlassBrush"] = ("#14FFFFFF", "#CCFFFFFF"),
        ["EdgeBrush"] = ("#26FFFFFF", "#30202A55"),
        ["HoverBrush"] = ("#14FFFFFF", "#12101840"),
        ["PanelBrush"] = ("#141930", "#FFFFFF"),
        ["ConsoleBrush"] = ("#050812", "#F7F8FC"),
        ["GridBrush"] = ("#1EFFFFFF", "#22000030"),
        ["SwitchOffBrush"] = ("#26FFFFFF", "#8A93AE"),
        ["GoodBrush"] = ("#34D399", "#0B7048"),
        ["WarnBrush"] = ("#FBBF24", "#855400"),
        ["BadBrush"] = ("#F87171", "#B42318"),
        ["PurpleBrush"] = ("#A78BFA", "#5B3FC4")
    };

    private static readonly Dictionary<string, SolidColorBrush> LiveBrushes = new();

    public static bool IsLight { get; private set; }
    public static IEnumerable<string> Keys => Palette.Keys;
    public static event Action? Changed;

    public static Color Color(string key) =>
        (Color)ColorConverter.ConvertFromString(IsLight ? Palette[key].Light : Palette[key].Dark);

    public static bool Has(string key) => Palette.ContainsKey(key);

    /// <summary>A brush that follows the theme. For use from code on the UI thread.</summary>
    public static SolidColorBrush Live(string key)
    {
        if (!LiveBrushes.TryGetValue(key, out var brush)) LiveBrushes[key] = brush = new SolidColorBrush(Color(key));
        return brush;
    }

    public static void Apply(bool light)
    {
        IsLight = light;
        foreach (var key in Palette.Keys)
        {
            var fixedBrush = new SolidColorBrush(Color(key));
            fixedBrush.Freeze();
            Application.Current.Resources[key] = fixedBrush;
            if (LiveBrushes.TryGetValue(key, out var live)) live.Color = Color(key);
        }
        foreach (Window window in Application.Current.Windows) StyleTitleBar(window);
        Changed?.Invoke();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Matches the Windows title bar to the theme, where Windows supports it.</summary>
    public static void StyleTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = IsLight ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }
}
