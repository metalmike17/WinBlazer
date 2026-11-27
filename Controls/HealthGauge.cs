using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PCOptimizer.Controls;

/// <summary>A ring showing a 0 to 100 score, or a dash when there is no score.</summary>
public sealed class HealthGauge : FrameworkElement
{
    private readonly int? _score;
    public HealthGauge(int? score) => (_score, Width, Height) = (score, 110, 110);

    public static Color ColorFor(int? score) => score switch
    {
        null => Color.FromRgb(0x8B, 0x98, 0xBF),
        >= 90 => Color.FromRgb(0x34, 0xD3, 0x99),
        >= 70 => Color.FromRgb(0x00, 0xD4, 0xFF),
        >= 40 => Color.FromRgb(0xFB, 0xBF, 0x24),
        _ => Color.FromRgb(0xF8, 0x71, 0x71)
    };

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(Width, Height), radius = size / 2 - 7;
        var center = new Point(size / 2, size / 2);
        dc.DrawEllipse(null, new Pen(Ui.Brush("GridBrush"), 10), center, radius, radius);
        var color = Ui.Brush(_score switch
        { null => "MutedBrush", >= 90 => "GoodBrush", >= 70 => "AccentBrush", >= 40 => "WarnBrush", _ => "BadBrush" });
        if (_score is > 0)
        {
            var pen = new Pen(color, 10) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (_score >= 100) dc.DrawEllipse(null, pen, center, radius, radius);
            else
            {
                var angle = _score.Value / 100.0 * 2 * Math.PI;
                var arc = new StreamGeometry();
                using (var g = arc.Open())
                {
                    g.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                    g.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
                        new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, true, false);
                }
                dc.DrawGeometry(null, pen, arc);
            }
        }
        var text = new FormattedText(_score?.ToString() ?? "—", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 30,
            color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
