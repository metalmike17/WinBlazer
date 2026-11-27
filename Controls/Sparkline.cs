using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PCOptimizer.Controls;

/// <summary>A small line chart that slides smoothly when a new value arrives.</summary>
public sealed class Sparkline : FrameworkElement
{
    private const int Capacity = 60;
    private readonly List<double?> _values = new();

    public static readonly DependencyProperty SlideProperty = DependencyProperty.Register(
        nameof(Slide), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Slide { get => (double)GetValue(SlideProperty); set => SetValue(SlideProperty, value); }
    public Color Stroke { get; set; } = Color.FromRgb(0x00, 0xD4, 0xFF);

    /// <summary>The value at the top of the chart. Zero means scale to the largest value shown.</summary>
    public double Maximum { get; set; } = 100;
    public int Count => _values.Count;

    public Sparkline() => ClipToBounds = true;

    public void Add(double? value)
    {
        _values.Add(value);
        if (_values.Count > Capacity + 1) _values.RemoveAt(0);
        BeginAnimation(SlideProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 4) return;
        var max = Maximum > 0 ? Maximum : Math.Max(1024, _values.Max(v => v) ?? 0);
        var step = w / (Capacity - 1);

        var segment = new List<Point>();
        var pen = new Pen(new SolidColorBrush(Stroke), 2) { LineJoin = PenLineJoin.Round };
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, Stroke.R, Stroke.G, Stroke.B),
                                           Color.FromArgb(0x00, Stroke.R, Stroke.G, Stroke.B), 90);
        void Flush()
        {
            if (segment.Count > 1)
            {
                var line = new StreamGeometry();
                using (var g = line.Open())
                {
                    g.BeginFigure(segment[0], false, false);
                    g.PolyLineTo(segment.Skip(1).ToList(), true, true);
                }
                var area = new StreamGeometry();
                using (var g = area.Open())
                {
                    g.BeginFigure(new Point(segment[0].X, h), true, true);
                    g.PolyLineTo(segment.Append(new Point(segment[^1].X, h)).ToList(), false, false);
                }
                dc.DrawGeometry(fill, null, area);
                dc.DrawGeometry(null, pen, line);
            }
            segment.Clear();
        }

        for (var i = 0; i < _values.Count; i++)
        {
            if (_values[i] is not { } v) { Flush(); continue; }   // gaps for missing readings
            var x = w - (_values.Count - 1 - i - Slide) * step;
            segment.Add(new Point(x, h - 2 - (h - 4) * Math.Clamp(v / max, 0, 1)));
        }
        Flush();
    }
}
