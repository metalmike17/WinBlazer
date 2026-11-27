using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PCOptimizer.Controls;

/// <summary>Small helpers so modules share one look.</summary>
public static class Ui
{
    public static Brush Brush(string key) =>
        Themes.ThemeManager.Has(key) ? Themes.ThemeManager.Live(key) : (Brush)Application.Current.FindResource(key);

    public static TextBlock Text(string text, double size = 13, string brush = "TextBrush",
                                 FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = Brush(brush), TextWrapping = TextWrapping.Wrap,
        FontWeight = weight ?? FontWeights.Normal, Margin = new Thickness(0, 0, 0, 6)
    };

    public static Border Card(UIElement child) => new()
    {
        Style = (Style)Application.Current.FindResource("GlassCard"), Child = child
    };

    public static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Page(string title, string subtitle) =>
        Stack(Text(title, 26, weight: FontWeights.SemiBold), Text(subtitle, 13, "MutedBrush"));

    /// <summary>Makes inputs in a button row match the buttons' height, with centered text.</summary>
    public static void AlignWithButtons(params Control[] inputs)
    {
        foreach (var input in inputs)
        {
            input.Height = 34;
            input.VerticalAlignment = VerticalAlignment.Top;
            input.VerticalContentAlignment = VerticalAlignment.Center;
        }
    }

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
}
