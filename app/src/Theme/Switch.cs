using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Winawdl.Theme;

/// <summary>An on/off switch whose thumb slides, or jumps when Windows has
/// animation effects turned off.</summary>
public sealed class Switch : ToggleButton
{
    private const double Travel = 20;

    private FrameworkElement? _on;
    private Ellipse? _thumb;
    private readonly TranslateTransform _slide = new();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _on = GetTemplateChild("PART_On") as FrameworkElement;
        _thumb = GetTemplateChild("PART_Thumb") as Ellipse;
        if (_thumb is not null) _thumb.RenderTransform = _slide;
        Show(animated: false);
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        Show(animated: true);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        Show(animated: true);
    }

    private void Show(bool animated)
    {
        if (_on is null || _thumb is null) return;
        var on = IsChecked == true;
        var milliseconds = animated ? Motion.Quick : 0;
        Motion.To(_on, OpacityProperty, on ? 1 : 0, milliseconds);
        Motion.To(_slide, TranslateTransform.XProperty, on ? Travel : 0, milliseconds);
        _thumb.SetResourceReference(Shape.FillProperty, on ? "OnAccent" : "InkSoft");
    }
}
