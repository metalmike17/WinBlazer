using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PCOptimizer.Services.MemoryTools;

namespace PCOptimizer.Controls;

/// <summary>Stacked area chart of the last 2 minutes. The top of the chart is installed (usable) RAM.</summary>
public sealed class MemoryChart : FrameworkElement
{
    public static readonly Color InUse = Color.FromRgb(0x00, 0xD4, 0xFF);
    public static readonly Color Modified = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color Standby = Color.FromRgb(0x7C, 0x3A, 0xED);

    private IReadOnlyList<MemorySnapshot> _samples = Array.Empty<MemorySnapshot>();

    public void Show(IReadOnlyList<MemorySnapshot> samples)
    {
        _samples = samples.ToList();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double left = 52, bottom = 18, top = 8;
        double w = ActualWidth - left - 8, h = ActualHeight - top - bottom;
        if (w <= 0 || h <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = Ui.Brush("MutedBrush");
        var grid = new Pen(Ui.Brush("GridBrush"), 1);
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted, dpi), new Point(x, y));

        var total = _samples.Count > 0 ? _samples[^1].Total : 0;
        for (var i = 0; i <= 4; i++)
        {
            var y = top + h - h * i / 4;
            dc.DrawLine(grid, new Point(left, y), new Point(left + w, y));
            if (total > 0) Label($"{total * i / 4.0 / (1 << 30):N0} GB", 4, y - 7);
        }
        Label("2 min ago", left, top + h + 3);
        Label("now", left + w - 20, top + h + 3);
        if (_samples.Count < 2 || total <= 0) return;

        var now = _samples[^1].TimeUtc;
        double X(MemorySnapshot s) => left + w * (1 - (now - s.TimeUtc).TotalSeconds / MemoryService.Window.TotalSeconds);
        double Y(long bytes) => top + h - h * Math.Clamp((double)bytes / total, 0, 1);

        var layers = new (Color Color, Func<MemorySnapshot, long> Lower, Func<MemorySnapshot, long> Upper)[]
        {
            (InUse, _ => 0, s => s.InUse),
            (Modified, s => s.InUse, s => s.InUse + s.Modified),
            (Standby, s => s.InUse + s.Modified, s => s.InUse + s.Modified + s.Standby)
        };
        dc.PushClip(new RectangleGeometry(new Rect(left, top, w, h)));
        foreach (var layer in layers)
        {
            var upper = _samples.Select(s => new Point(X(s), Y(layer.Upper(s)))).ToList();
            var lower = _samples.Reverse().Select(s => new Point(X(s), Y(layer.Lower(s))));
            var shape = new StreamGeometry();
            using (var g = shape.Open())
            {
                g.BeginFigure(upper[0], true, true);
                g.PolyLineTo(upper.Skip(1).Concat(lower).ToList(), false, false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xA0, layer.Color.R, layer.Color.G, layer.Color.B)),
                            null, shape);
            var edge = new StreamGeometry();
            using (var g = edge.Open())
            {
                g.BeginFigure(upper[0], false, false);
                g.PolyLineTo(upper.Skip(1).ToList(), true, true);
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(layer.Color), 1.5), edge);
        }
        dc.Pop();
    }
}
