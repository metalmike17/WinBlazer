using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PCOptimizer.Controls;

/// <summary>A one-minute chart that scrolls smoothly between once-a-second samples.</summary>
public sealed class LiveChart : FrameworkElement
{
    private const int Visible = 60;
    private readonly List<double?>[] _series;
    private readonly Color[] _colors;
    private readonly Stopwatch _sinceSample = new();
    private TimeSpan _lastFrame;
    private bool _animating;

    /// <summary>Fixed top of the scale, or null to fit the data.</summary>
    public double? Maximum { get; init; }
    public double MinimumScale { get; init; } = 1;
    public Func<double, string>? ScaleText { get; init; }
    public int SampleCount => _series[0].Count;

    public LiveChart(params Color[] colors)
    {
        _colors = colors;
        _series = colors.Select(_ => new List<double?>()).ToArray();
        ClipToBounds = true;
        // Frames are only drawn while the chart is on screen.
        Loaded += (_, _) => Animate(true);
        Unloaded += (_, _) => Animate(false);
    }

    public void Add(params double?[] values)
    {
        for (var i = 0; i < _series.Length; i++)
        {
            _series[i].Add(i < values.Length ? values[i] : null);
            // Two extra points, so the line reaches the left edge while it scrolls.
            if (_series[i].Count > Visible + 2) _series[i].RemoveAt(0);
        }
        _sinceSample.Restart();
        InvalidateVisual();
    }

    private void Animate(bool on)
    {
        if (on == _animating) return;
        _animating = on;
        if (on) CompositionTarget.Rendering += Frame;
        else CompositionTarget.Rendering -= Frame;
    }

    private void Frame(object? sender, EventArgs e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime;
        // About 30 frames a second, which looks smooth and costs little.
        if (!IsVisible || !SystemParameters.ClientAreaAnimation || (time - _lastFrame).TotalMilliseconds < 32 ||
            _sinceSample.Elapsed.TotalSeconds > 1.2) return;
        _lastFrame = time;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 4 || height <= 4) return;
        var top = Maximum ?? Math.Max(MinimumScale,
            _series.SelectMany(s => s).Select(v => v ?? 0).DefaultIfEmpty().Max() * 1.2);
        var grid = new Pen(Ui.Brush("GridBrush"), 1);
        for (var i = 1; i < 4; i++) dc.DrawLine(grid, new Point(0, height * i / 4), new Point(width, height * i / 4));

        // The newest point slides in from just past the right edge.
        var progress = SystemParameters.ClientAreaAnimation
            ? Math.Min(1, _sinceSample.Elapsed.TotalSeconds) : 1;
        var step = width / (Visible - 1);
        for (var s = 0; s < _series.Length; s++)
        {
            var data = _series[s];
            var points = new List<Point>();
            for (var i = 0; i < data.Count; i++)
                if (data[i] is { } v)
                    points.Add(new Point(width + step * (1 - progress) - step * (data.Count - 1 - i),
                                         height - 1 - Math.Clamp(v / top, 0, 1) * (height - 3)));
            if (points.Count < 2) continue;
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var c = line.Open())
            {
                c.BeginFigure(points[0], false, false);
                c.PolyLineTo(points.Skip(1).ToList(), true, true);
            }
            using (var c = area.Open())
            {
                c.BeginFigure(new Point(points[0].X, height), true, true);
                c.PolyLineTo(points, false, false);
                c.LineTo(new Point(points[^1].X, height), false, false);
            }
            var color = _colors[s];
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)), null, area);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.6), line);
        }
        if (ScaleText != null)
            dc.DrawText(new FormattedText(ScaleText(top), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, Ui.Brush("MutedBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip),
                new Point(4, 1));
    }
}
