using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Winawdl.Model;

namespace Winawdl.Theme;

/// <summary>A device's picture: the owner's art for a phone, tablet or Mac
/// (Devices.xaml), the glyph for a model that is not recognised.</summary>
public sealed class DevicePortrait : Decorator
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(DeviceKind),
        typeof(DevicePortrait), new FrameworkPropertyMetadata(DeviceKind.Other, (portrait, _) => ((DevicePortrait)portrait).Dress()));

    public DevicePortrait()
    {
        IsHitTestVisible = false;
        Dress();
    }

    public DeviceKind Kind { get => (DeviceKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>The glyph's size, for a device with no art.</summary>
    public double GlyphSize { get; set; } = 28;

    // The mockup draws the Mac in 64 px, the iPhone in 58 and the iPad 48 high.
    private static double Scale(DeviceKind kind) => kind switch
    {
        DeviceKind.Phone => 58.0 / 64,
        DeviceKind.Tablet => 48.0 / 64,
        _ => 1,
    };

    private void Dress()
    {
        if (TryFindResource($"DeviceArt.{Kind}") is FrameworkElement art)
        {
            // Each picture keeps the size the mockup gives it beside the others.
            art.LayoutTransform = new ScaleTransform(Scale(Kind), Scale(Kind));
            art.HorizontalAlignment = HorizontalAlignment.Center;
            art.VerticalAlignment = VerticalAlignment.Center;
            Child = art;
            return;
        }
        var glyph = new GlyphText
        {
            Text = Words.Glyph(Kind), FontSize = GlyphSize, Style = TryFindResource("Glyph") as Style,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");
        Child = glyph;
    }
}
