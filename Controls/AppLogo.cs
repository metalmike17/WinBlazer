using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCOptimizer.Controls;

/// <summary>
/// The app logo, drawn as vectors: a white flame with a lightning bolt on a rounded orange-red tile.
/// Assets\WinBlazer.ico (the program and shortcut icon) is this same drawing.
/// </summary>
public sealed class AppLogo : FrameworkElement
{
    // All shapes are on a 100 x 100 grid.
    private const string Flame = "M 53,7 C 57,24 79,33 81,57 C 83,78 68,93 50,93 C 31,93 18,79 19,61 " +
                                 "C 19,49 25,40 33,33 C 32,43 36,49 42,51 C 37,33 42,17 53,7 Z";
    private const string Bolt = "M 59,36 L 36,67 L 49,67 L 43,89 L 67,57 L 53,57 Z";

    protected override void OnRender(DrawingContext dc) => Draw(dc, Math.Min(ActualWidth, ActualHeight));

    private static Color Rgb(uint value) => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    /// <summary>A gradient running from the top left of a shape to its bottom right.</summary>
    private static LinearGradientBrush Diagonal(uint from, uint to) =>
        new(Rgb(from), Rgb(to), new Point(0, 0), new Point(1, 1));

    private static void Draw(DrawingContext dc, double size)
    {
        if (size <= 0) return;
        dc.PushTransform(new ScaleTransform(size / 100, size / 100));
        dc.DrawRoundedRectangle(Diagonal(0xFF8A00, 0xF0143C), null, new Rect(0, 0, 100, 100), 23, 23);
        dc.PushTransform(new ScaleTransform(1.02, 1.02, 50, 50));
        dc.DrawGeometry(Brushes.White, null, Geometry.Parse(Flame));
        dc.Pop();
        dc.DrawGeometry(Diagonal(0xFF7A00, 0xE8103F), null, Geometry.Parse(Bolt));
        dc.Pop();
    }

    /// <summary>For the window and taskbar icon.</summary>
    public static ImageSource Icon(int size = 64)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, size);
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
