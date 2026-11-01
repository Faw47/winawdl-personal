using System.Windows;
using System.Windows.Media;
using Winawdl.Model;

namespace Winawdl.Theme;

/// <summary>Small, quiet device silhouettes. The adjacent name carries identity.</summary>
public sealed class DevicePortrait : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(DeviceKind),
        typeof(DevicePortrait), new FrameworkPropertyMetadata(DeviceKind.Other, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InkProperty = BrushProperty(nameof(Ink));
    public static readonly DependencyProperty FrameProperty = BrushProperty(nameof(Frame));
    public static readonly DependencyProperty ScreenProperty = BrushProperty(nameof(Screen));

    public DevicePortrait()
    {
        IsHitTestVisible = false;
        SetResourceReference(InkProperty, "InkSoft");
        SetResourceReference(FrameProperty, "Well");
        SetResourceReference(ScreenProperty, "Raised");
    }

    public DeviceKind Kind { get => (DeviceKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public Brush Frame { get => (Brush)GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public Brush Screen { get => (Brush)GetValue(ScreenProperty); set => SetValue(ScreenProperty, value); }

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(name, typeof(Brush),
        typeof(DevicePortrait), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var scale = Math.Min(ActualWidth, ActualHeight) / 64;
        if (scale <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - 64 * scale) / 2, (ActualHeight - 64 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var edge = new Pen(Ink, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (Kind)
        {
            case DeviceKind.Phone:
                dc.DrawRoundedRectangle(Frame, edge, new Rect(20, 4, 24, 56), 5, 5);
                dc.DrawRoundedRectangle(Screen, null, new Rect(23, 7, 18, 50), 3, 3);
                dc.DrawLine(edge, new Point(29, 9), new Point(35, 9));
                dc.DrawLine(edge, new Point(28, 54), new Point(36, 54));
                break;
            case DeviceKind.Tablet:
                dc.DrawRoundedRectangle(Frame, edge, new Rect(12, 5, 40, 54), 4, 4);
                dc.DrawRoundedRectangle(Screen, null, new Rect(15, 8, 34, 48), 2, 2);
                dc.DrawEllipse(Ink, null, new Point(32, 6.5), .6, .6);
                break;
            case DeviceKind.Computer:
                dc.DrawRoundedRectangle(Frame, edge, new Rect(8, 10, 48, 33), 3, 3);
                dc.DrawRoundedRectangle(Screen, null, new Rect(11, 13, 42, 27), 1, 1);
                var baseShape = new StreamGeometry();
                using (var shape = baseShape.Open())
                {
                    shape.BeginFigure(new Point(3, 44), true, true);
                    shape.PolyLineTo([new Point(61, 44), new Point(57, 49), new Point(7, 49)], true, false);
                }
                dc.DrawGeometry(Frame, edge, baseShape);
                dc.DrawLine(edge, new Point(26, 44), new Point(38, 44));
                break;
            default:
                // An unrecognised model gets the connection symbol, not an invented device.
                foreach (var radius in new[] { 8.0, 16, 24 }) dc.DrawEllipse(null, edge, new Point(32, 32), radius, radius);
                break;
        }
        dc.Pop();
        dc.Pop();
    }
}
