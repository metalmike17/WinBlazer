using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Controls;

/// <summary>Rectangle area is proportional to size. Click a folder to drill down.</summary>
public sealed class Treemap : FrameworkElement
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x00, 0x8F, 0xB3), Color.FromRgb(0x6D, 0x31, 0xD1), Color.FromRgb(0x1F, 0x5F, 0xBF),
        Color.FromRgb(0x9D, 0x2F, 0x8F), Color.FromRgb(0x0F, 0x76, 0x6E), Color.FromRgb(0x4F, 0x46, 0xA5)
    };

    private readonly List<(Rect Area, DiskNode Node)> _hits = new();
    private DiskNode? _node;
    private DiskNode? _hover;

    public event Action<DiskNode>? FolderClicked;
    public DiskNode? Node { get => _node; set { _node = value; _hover = null; ToolTip = null; InvalidateVisual(); } }

    /// <summary>Squarified layout. Items with no size get no rectangle.</summary>
    public static List<(T Item, Rect Area)> Layout<T>(IEnumerable<T> items, Func<T, double> size, Rect bounds)
    {
        var sorted = items.Where(i => size(i) > 0).OrderByDescending(size).ToList();
        var result = new List<(T, Rect)>();
        var total = sorted.Sum(size);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;
        var scale = bounds.Width * bounds.Height / total;
        var free = bounds;
        var start = 0;
        while (start < sorted.Count)
        {
            var side = Math.Max(Math.Min(free.Width, free.Height), 0.0001);
            double sum = 0, best = double.MaxValue, largest = size(sorted[start]) * scale;
            var end = start;
            while (end < sorted.Count)   // grow the row while its worst aspect ratio improves
            {
                var area = size(sorted[end]) * scale;
                var next = sum + area;
                var worst = Math.Max(side * side * largest / (next * next), next * next / (side * side * area));
                if (end > start && worst > best) break;
                (best, sum) = (worst, next);
                end++;
            }
            var wide = free.Width >= free.Height;
            var thickness = Math.Min(sum / side, wide ? free.Width : free.Height);
            double offset = 0;
            for (var i = start; i < end; i++)
            {
                var length = size(sorted[i]) * scale / Math.Max(thickness, 0.0001);
                result.Add((sorted[i], wide
                    ? new Rect(free.X, free.Y + offset, Math.Max(0, thickness), length)
                    : new Rect(free.X + offset, free.Y, length, Math.Max(0, thickness))));
                offset += length;
            }
            free = wide
                ? new Rect(free.X + thickness, free.Y, Math.Max(0, free.Width - thickness), free.Height)
                : new Rect(free.X, free.Y + thickness, free.Width, Math.Max(0, free.Height - thickness));
            start = end;
        }
        return result;
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Ui.Brush("ConsoleBrush"), null, bounds);
        if (_node == null || _node.Size <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var border = new Pen(Ui.Brush("NavyBrush"), 1);
        var index = 0;
        foreach (var (child, area) in Layout(_node.Children, c => c.Size, bounds))
        {
            var color = Palette[index++ % Palette.Length];
            dc.DrawRectangle(new SolidColorBrush(color), border, area);
            _hits.Add((area, child));
            if (area.Width < 40 || area.Height < 18) continue;
            Label(dc, $"{child.Name}  {CleanupEngine.Size(child.Size)}", area, dpi);

            // Show one nested level inside folders that are large enough.
            if (!child.IsFolder || area.Width < 70 || area.Height < 55) continue;
            var inner = new Rect(area.X + 4, area.Y + 20, area.Width - 8, area.Height - 24);
            foreach (var (grand, small) in Layout(child.Children, c => c.Size, inner))
            {
                dc.DrawRectangle(Ui.Brush("GridBrush"), border, small);
                if (small.Width >= 60 && small.Height >= 18) Label(dc, grand.Name, small, dpi);
            }
        }
    }

    private static void Label(DrawingContext dc, string text, Rect area, double dpi)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, Brushes.White, dpi)
        { MaxTextWidth = area.Width - 8, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, new Point(area.X + 4, area.Y + 2));
    }

    private DiskNode? At(Point point) => _hits.FirstOrDefault(h => h.Area.Contains(point)).Node;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var node = At(e.GetPosition(this));
        if (node == _hover) return;
        _hover = node;
        ToolTip = node == null ? null : new ToolTip
        {
            Content = $"{node.Path}\n{node.Name}\n{CleanupEngine.Size(node.Size)} " +
                      $"({100.0 * node.Size / Math.Max(1, _node!.Size):N1}% of this folder)"
        };
        Cursor = node is { IsFolder: true } ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (At(e.GetPosition(this)) is { IsFolder: true } folder) FolderClicked?.Invoke(folder);
    }
}
