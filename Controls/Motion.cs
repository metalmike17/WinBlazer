using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PCOptimizer.Controls;

/// <summary>
/// Page transitions and number counters. Transitions only animate opacity and a render transform, so
/// WPF composites them on the graphics card without laying the page out again. Both are skipped when
/// "Animation effects" is turned off in Windows.
/// </summary>
public static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;
    /// <summary>2 means full hardware acceleration, 0 means software rendering.</summary>
    public static int RenderTier => RenderCapability.Tier >> 16;

    /// <summary>Slides and fades a page in. direction is +1 to rise from below, -1 to drop from above.</summary>
    public static void Enter(FrameworkElement host, int direction)
    {
        if (host.RenderTransform is not TranslateTransform move || move.IsFrozen)
            host.RenderTransform = move = new TranslateTransform();
        if (!Enabled) return;
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        // Cached as one bitmap only while moving; removed afterwards so text stays sharp.
        host.CacheMode = new BitmapCache { RenderAtScale = VisualTreeHelper.GetDpi(host).DpiScaleX };
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => host.CacheMode = null;
        host.BeginAnimation(UIElement.OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24 * direction, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(double), typeof(Motion), new PropertyMetadata(0.0, (target, e) =>
        {
            if (target is TextBlock text && text.GetValue(FormatProperty) is Func<double, string> format)
                text.Text = format((double)e.NewValue);
        }));

    private static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached(
        "Format", typeof(Func<double, string>), typeof(Motion));

    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(double?), typeof(Motion));

    /// <summary>Counts from the number now shown to a new one. A null value shows the fallback text.</summary>
    public static void Count(TextBlock text, double? value, Func<double, string> format, string fallback = "n/a")
    {
        var previous = (double?)text.GetValue(TargetProperty);
        var from = previous == null ? 0 : (double)text.GetValue(ShownProperty);
        text.BeginAnimation(ShownProperty, null);
        text.SetValue(TargetProperty, value);
        if (value is not { } to || !double.IsFinite(to))
        {
            text.SetValue(FormatProperty, null);
            text.Text = fallback;
            return;
        }
        text.SetValue(FormatProperty, format);
        text.SetValue(ShownProperty, from);
        text.Text = format(from);
        if (!Enabled || !text.IsVisible || from == to)
        {
            text.SetValue(ShownProperty, to);
            text.Text = format(to);
            return;
        }
        text.BeginAnimation(ShownProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(600))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
