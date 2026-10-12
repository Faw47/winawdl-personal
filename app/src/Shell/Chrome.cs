using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Winawdl.Theme;

namespace Winawdl.Shell;

/// <summary>Makes Windows draw the title bar in the table's color, so the window
/// is one surface with the system's own buttons on it.</summary>
public static class TitleBar
{
    private const int UseDarkMode = 20, BorderColor = 34, CaptionColor = 35, TextColor = 36;

    public static void Paint(Window window, Palette palette)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || palette.Plain) return;
        var dark = palette.Dark ? 1 : 0;
        DwmSetWindowAttribute(handle, UseDarkMode, ref dark, sizeof(int));
        var table = ColorRef(palette.Table);
        DwmSetWindowAttribute(handle, CaptionColor, ref table, sizeof(int));
        DwmSetWindowAttribute(handle, BorderColor, ref table, sizeof(int));
        var ink = ColorRef(palette.Ink);
        DwmSetWindowAttribute(handle, TextColor, ref ink, sizeof(int));
    }

    private static int ColorRef(Rgb color) => color.R | color.G << 8 | color.B << 16;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

/// <summary>The app's mark: a file that has just landed, turned as the files in
/// the window are, and the rings spreading from where it fell. One drawing
/// gives the icon file and the single-color mark of the notification area.
/// See app/DESIGN.md.</summary>
public static class AppIcon
{
    /// <summary>The blue Windows ships as its accent. The icon keeps it whatever
    /// the accent is, so that it is the same icon wherever it is pinned.</summary>
    public static readonly Color Ground = Color.FromRgb(0x00, 0x78, 0xD4);

    private const double Grid = 256;
    private const double Turn = -12;
    /// <summary>A ring fades and thins as it spreads.</summary>
    private static readonly double[] Fade = [1, 0.7, 0.4];
    private static readonly double[] Thin = [1, 0.88, 0.76];

    private readonly record struct Cut(double Tile, double Stroke, double[] Rings);

    /// <summary>Fewer and heavier rings as the icon gets smaller, so that each
    /// stays more than a pixel wide.</summary>
    private static Cut For(int size) => size switch
    {
        >= 48 => new Cut(62, 11, [66, 101, 136]),
        >= 20 => new Cut(66, 17, [70, 106]),
        _ => new Cut(80, 26, [90]),
    };

    /// <summary>The mark at `size` pixels, on its plate, or alone when `ground` is null.</summary>
    public static RenderTargetBitmap Draw(int size, Color mark, Color? ground)
    {
        var cut = For(size);
        var center = new Point(Grid / 2, Grid / 2);
        var brush = new SolidColorBrush(mark);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / Grid, size / Grid));
            // The plate cuts the widest ring, which shows only in its corners.
            var plate = new RectangleGeometry(new Rect(8, 8, Grid - 16, Grid - 16), 54, 54);
            if (ground is { } fill)
            {
                // Lit from above, like every raised thing in the window.
                var lit = new LinearGradientBrush(Shade(fill, 1.1), Shade(fill, 0.9), 90);
                dc.DrawGeometry(lit, null, plate);
            }
            dc.PushClip(ground is null ? new RectangleGeometry(new Rect(0, 0, Grid, Grid)) : plate);
            for (var ring = 0; ring < cut.Rings.Length; ring++)
            {
                dc.PushOpacity(Fade[ring]);
                dc.DrawEllipse(null, new Pen(brush, cut.Stroke * Thin[ring]), center, cut.Rings[ring], cut.Rings[ring]);
                dc.Pop();
            }
            dc.PushTransform(new RotateTransform(Turn, center.X, center.Y));
            var tile = new Rect(center.X - cut.Tile / 2, center.Y - cut.Tile / 2, cut.Tile, cut.Tile);
            dc.DrawRoundedRectangle(brush, null, tile, cut.Tile * 0.24, cut.Tile * 0.24);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color Shade(Color color, double by) => Color.FromRgb(
        (byte)Math.Min(255, color.R * by + (by > 1 ? 14 : 0)), (byte)Math.Min(255, color.G * by),
        (byte)Math.Min(255, color.B * by));

    /// <summary>For the notification area, which shows small marks in one color.</summary>
    public static System.Drawing.Icon ForTray(bool onDarkTaskbar)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Draw(32, onDarkTaskbar ? Colors.White : Colors.Black, null)));
        using var png = new System.IO.MemoryStream();
        encoder.Save(png);
        png.Position = 0;
        using var bitmap = new System.Drawing.Bitmap(png);
        // An icon made from a handle does not free it; its copy owns one that it does.
        var handle = bitmap.GetHicon();
        using var borrowed = System.Drawing.Icon.FromHandle(handle);
        var owned = (System.Drawing.Icon)borrowed.Clone();
        DestroyIcon(handle);
        return owned;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
