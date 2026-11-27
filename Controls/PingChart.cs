using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PCOptimizer.Services.Network;

namespace PCOptimizer.Controls;

/// <summary>Latency line for the last two minutes. Red bars mark pings with no reply.</summary>
public sealed class PingChart : FrameworkElement
{
    public PingStats? Stats { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 60 || height <= 20) return;
        const double left = 48;
        var top = Math.Max(20, Math.Ceiling((Stats?.Maximum ?? 0) * 1.2 / 10) * 10);
        var grid = new Pen(Ui.Brush("GridBrush"), 1);
        var muted = Ui.Brush("MutedBrush");
        for (var i = 0; i <= 4; i++)
        {
            var y = 8 + (height - 16) * i / 4;
            dc.DrawLine(grid, new Point(left, y), new Point(width, y));
            dc.DrawText(new FormattedText($"{top * (4 - i) / 4:0} ms", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted,
                VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(0, y - 7));
        }
        if (Stats == null || Stats.Samples.Count == 0) return;
        var step = (width - left) / (Stats.Capacity - 1);
        var start = width - step * (Stats.Samples.Count - 1);   // newest at the right edge
        var line = new Pen(Ui.Brush("AccentBrush"), 2);
        var lost = new SolidColorBrush(Color.FromArgb(170, 0xF8, 0x71, 0x71));
        Point? previous = null;
        for (var i = 0; i < Stats.Samples.Count; i++)
        {
            var x = start + step * i;
            if (Stats.Samples[i] is not { } ms)
            {
                dc.DrawRectangle(lost, null, new Rect(x - 1.5, 8, 3, height - 16));
                previous = null;
                continue;
            }
            var point = new Point(x, 8 + (height - 16) * (1 - ms / top));
            if (previous is { } from) dc.DrawLine(line, from, point);
            else dc.DrawEllipse(line.Brush, null, point, 1.5, 1.5);
            previous = point;
        }
    }
}
