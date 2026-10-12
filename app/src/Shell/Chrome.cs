using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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

/// <summary>Stands in for an icon until the owner describes one: the first letter
/// of the name, set in the app's own type. See app/DESIGN.md.</summary>
public static class PlaceholderIcon
{
    public const string Letter = "w";

    /// <summary>For the taskbar and Alt+Tab: the letter on a raised square.</summary>
    public static ImageSource ForWindow(Palette palette)
    {
        const int size = 64;
        var tile = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(14),
            Background = palette.Brush(palette.Raised), BorderBrush = palette.Brush(palette.Line),
            BorderThickness = new Thickness(1),
            Child = Mark(palette.Brush(palette.Ink), 40),
        };
        return Render(tile, size);
    }

    /// <summary>For the tray, which shows small single-color marks.</summary>
    public static System.Drawing.Icon ForTray(bool onDarkTaskbar)
    {
        const int size = 32;
        var mark = new Border
        {
            Width = size, Height = size,
            Child = Mark(onDarkTaskbar ? Brushes.White : Brushes.Black, 30),
        };
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(mark, size)));
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

    private static TextBlock Mark(Brush brush, double fontSize) => new()
    {
        Text = Letter, Foreground = brush, FontSize = fontSize, FontWeight = FontWeights.SemiBold,
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, -6, 0, 0),
    };

    private static RenderTargetBitmap Render(FrameworkElement element, int size)
    {
        element.Measure(new Size(size, size));
        element.Arrange(new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        bitmap.Freeze();
        return bitmap;
    }
}
