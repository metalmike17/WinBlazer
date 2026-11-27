using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PCOptimizer.Controls;

namespace PCOptimizer;

/// <summary>The loading screen shown while the app starts.</summary>
public sealed class SplashWindow : Window
{
    private readonly TextBlock _status = Ui.Text("Starting...", 12, "MutedBrush");

    public string Status { get => _status.Text; set => _status.Text = value; }

    public SplashWindow()
    {
        Title = "WinBlazer is loading";
        Width = 360;
        Height = 270;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("NavyBrush");
        FontFamily = new FontFamily("Segoe UI");
        Icon = AppLogo.Icon();

        var logo = new AppLogo { Width = 96, Height = 96, RenderTransformOrigin = new Point(0.5, 0.5) };
        var pulse = new ScaleTransform();
        logo.RenderTransform = pulse;
        var name = Ui.Text("WinBlazer", 22, weight: FontWeights.SemiBold);
        name.Margin = new Thickness(0, 16, 0, 4);
        var bar = new ProgressBar { IsIndeterminate = true, Width = 200, Height = 3, Margin = new Thickness(0, 12, 0, 0), Foreground = Ui.Brush("AccentBrush"),
                                    Background = Ui.Brush("GridBrush"), BorderThickness = new Thickness(0) };
        var panel = Ui.Stack(logo, name, _status, bar);
        panel.VerticalAlignment = VerticalAlignment.Center;
        foreach (FrameworkElement child in panel.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
        Content = new Border { BorderBrush = Ui.Brush("AccentBrush"), BorderThickness = new Thickness(1), Child = panel };

        if (!Motion.Enabled) return;
        var beat = new DoubleAnimation(1, 1.07, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        pulse.BeginAnimation(ScaleTransform.ScaleXProperty, beat);
        pulse.BeginAnimation(ScaleTransform.ScaleYProperty, beat);
    }
}
