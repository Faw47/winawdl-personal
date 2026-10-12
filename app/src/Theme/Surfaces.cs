using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Winawdl.Theme;

/// <summary>A surface that stands above the table: it casts a shadow, rises a
/// little under the pointer and sinks when pressed.</summary>
public sealed class Raised : Decorator
{
    public static readonly DependencyProperty RadiusProperty = Register(nameof(Radius), 18.0);
    public static readonly DependencyProperty FillProperty = Register<Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty EdgeProperty = Register<Brush?>(nameof(Edge), null);
    public static readonly DependencyProperty OutlineProperty = Register<Brush?>(nameof(Outline), null);
    public static readonly DependencyProperty ShadowStrengthProperty = Register(nameof(ShadowStrength), 0.2);
    public static readonly DependencyProperty ShadowColorProperty = Register(nameof(ShadowColor), Colors.Black);
    public static readonly DependencyProperty IsHotProperty = Register(nameof(IsHot), false, Moved);
    public static readonly DependencyProperty IsDownProperty = Register(nameof(IsDown), false, Moved);
    private static readonly DependencyProperty LiftProperty = Register("Lift", 0.0);
    private static readonly DependencyProperty SinkProperty = Register("Sink", 0.0);

    private readonly DrawingVisual _rest = new() { Effect = Shadow(blur: 14, depth: 3) };
    private readonly DrawingVisual _lifted = new() { Effect = Shadow(blur: 28, depth: 9) };
    private readonly DrawingVisual _surface = new();
    private readonly TranslateTransform _press = new();
    private Size _size;

    public Raised()
    {
        AddVisualChild(_rest);
        AddVisualChild(_lifted);
        AddVisualChild(_surface);
        RenderTransform = _press;
        SetResourceReference(ShadowColorProperty, "ShadowColor");
    }

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    /// <summary>The lit upper edge.</summary>
    public Brush? Edge { get => (Brush?)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
    /// <summary>A line all round, for when there is no shadow to show the edge.</summary>
    public Brush? Outline { get => (Brush?)GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public double ShadowStrength { get => (double)GetValue(ShadowStrengthProperty); set => SetValue(ShadowStrengthProperty, value); }
    public Color ShadowColor { get => (Color)GetValue(ShadowColorProperty); set => SetValue(ShadowColorProperty, value); }
    public bool IsHot { get => (bool)GetValue(IsHotProperty); set => SetValue(IsHotProperty, value); }
    public bool IsDown { get => (bool)GetValue(IsDownProperty); set => SetValue(IsDownProperty, value); }

    private static DropShadowEffect Shadow(double blur, double depth) => new()
    {
        BlurRadius = blur, ShadowDepth = depth, Direction = 270, Color = Colors.Black, Opacity = 1,
    };

    private static DependencyProperty Register<T>(string name, T value, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, typeof(T), typeof(Raised), new FrameworkPropertyMetadata(
            value, FrameworkPropertyMetadataOptions.AffectsRender, changed ?? ((d, _) => ((Raised)d).Draw())));

    private static void Moved(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var raised = (Raised)d;
        Motion.To(raised, LiftProperty, raised.IsHot && !raised.IsDown ? 1 : 0, Motion.Quick);
        Motion.To(raised, SinkProperty, raised.IsDown ? 1 : 0, Motion.Quick);
    }

    protected override int VisualChildrenCount => 3 + (Child is null ? 0 : 1);

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _rest, 1 => _lifted, 2 => _surface, _ => Child,
    };

    protected override Size ArrangeOverride(Size size)
    {
        var arranged = base.ArrangeOverride(size);
        _size = arranged;
        Draw();
        return arranged;
    }

    private void Draw()
    {
        var size = _size;
        if (size.Width <= 2 || size.Height <= 2) return;
        var lift = (double)GetValue(LiftProperty);
        var sink = (double)GetValue(SinkProperty);
        _press.Y = Math.Round(sink * 1.5 - lift, 2);

        // The shadow's source hides under the surface, a pixel inside its edge.
        var hidden = new Rect(1, 1, size.Width - 2, size.Height - 2);
        var dark = new SolidColorBrush(ShadowColor);
        foreach (var (visual, opacity) in new[] { (_rest, 1 - 0.65 * sink), (_lifted, lift * (1 - sink)) })
        {
            visual.Opacity = ShadowStrength * opacity;
            ((DropShadowEffect)visual.Effect).Color = ShadowColor;
            using var shade = visual.RenderOpen();
            if (ShadowStrength > 0)
                shade.DrawRoundedRectangle(dark, null, hidden, Radius, Radius);
        }

        using var dc = _surface.RenderOpen();
        var bounds = new Rect(size);
        dc.DrawRoundedRectangle(Fill, null, bounds, Radius, Radius);
        if (Outline is not null)
        {
            var inner = new Rect(0.5, 0.5, size.Width - 1, size.Height - 1);
            dc.DrawRoundedRectangle(null, new Pen(Outline, 1), inner, Radius, Radius);
        }
        if (Lit.Fading(Edge, fromTop: true, Radius, size.Height) is { } lit)
        {
            var inner = new Rect(0.5, 0.5, size.Width - 1, size.Height - 1);
            dc.DrawRoundedRectangle(null, new Pen(lit, 1), inner, Radius, Radius);
        }
    }
}

/// <summary>Light falls from above: it catches the upper edge of what is raised
/// and the lower lip of what is cut in, and fades out round the corners.</summary>
internal static class Lit
{
    public static Brush? Fading(Brush? edge, bool fromTop, double radius, double height)
    {
        if (edge is not SolidColorBrush solid || height <= 0) return null;
        var color = solid.Color;
        color.A = (byte)(color.A * solid.Opacity);
        var reach = Math.Min(1, radius * 0.9 / height);
        var brush = fromTop
            ? new LinearGradientBrush(color, Colors.Transparent, new Point(0, 0), new Point(0, reach))
            : new LinearGradientBrush(Colors.Transparent, color, new Point(0, 1 - reach), new Point(0, 1));
        // Transparent is "transparent white": the fade keeps the edge's own hue.
        brush.GradientStops[fromTop ? 1 : 0].Color = Color.FromArgb(0, color.R, color.G, color.B);
        brush.Freeze();
        return brush;
    }
}

/// <summary>A surface cut into the table, where things land: shaded under its
/// upper lip and lit along its lower one.</summary>
public sealed class Well : Decorator
{
    public static readonly DependencyProperty RadiusProperty = Register(nameof(Radius), 22.0);
    public static readonly DependencyProperty FillProperty = Register<Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty EdgeProperty = Register<Brush?>(nameof(Edge), null);
    public static readonly DependencyProperty OutlineProperty = Register<Brush?>(nameof(Outline), null);
    public static readonly DependencyProperty ShadeStrengthProperty = Register(nameof(ShadeStrength), 0.1);
    public static readonly DependencyProperty TargetProperty = Register<Brush?>(nameof(Target), null);
    private static readonly DependencyProperty TargetAmountProperty = Register("TargetAmount", 0.0);
    public static readonly DependencyProperty IsTargetProperty = DependencyProperty.Register(
        nameof(IsTarget), typeof(bool), typeof(Well), new PropertyMetadata(false, (d, e) =>
            Motion.To((Well)d, TargetAmountProperty, (bool)e.NewValue ? 1 : 0, Motion.Quick)));

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Edge { get => (Brush?)GetValue(EdgeProperty); set => SetValue(EdgeProperty, value); }
    public Brush? Outline { get => (Brush?)GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public double ShadeStrength { get => (double)GetValue(ShadeStrengthProperty); set => SetValue(ShadeStrengthProperty, value); }
    /// <summary>The ring drawn while something is held over the well.</summary>
    public Brush? Target { get => (Brush?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public bool IsTarget { get => (bool)GetValue(IsTargetProperty); set => SetValue(IsTargetProperty, value); }

    private static DependencyProperty Register<T>(string name, T value) =>
        DependencyProperty.Register(name, typeof(T), typeof(Well),
            new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (size.Width <= 2 || size.Height <= 2) return;
        var bounds = new Rect(size);
        dc.DrawRoundedRectangle(Fill, null, bounds, Radius, Radius);
        dc.PushClip(new RectangleGeometry(bounds, Radius, Radius));
        if (ShadeStrength > 0)
        {
            var shade = new LinearGradientBrush(Color.FromArgb((byte)(255 * ShadeStrength), 0, 0, 0),
                                                Colors.Transparent, 90);
            dc.DrawRectangle(shade, null, new Rect(0, 0, size.Width, Math.Min(14, size.Height)));
        }
        dc.Pop();
        var inner = new Rect(0.5, 0.5, size.Width - 1, size.Height - 1);
        if (Lit.Fading(Edge, fromTop: false, Radius, size.Height) is { } lit)
            dc.DrawRoundedRectangle(null, new Pen(lit, 1), inner, Radius, Radius);
        if (Outline is not null)
            dc.DrawRoundedRectangle(null, new Pen(Outline, 1), inner, Radius, Radius);
        var amount = (double)GetValue(TargetAmountProperty);
        if (Target is not null && amount > 0)
        {
            var ring = new Rect(1, 1, size.Width - 2, size.Height - 2);
            dc.PushOpacity(amount);
            dc.DrawRoundedRectangle(null, new Pen(Target, 2), ring, Radius - 1, Radius - 1);
            dc.Pop();
        }
    }
}

/// <summary>How far a transfer has come, drawn as the surface under it filling
/// from the left with a solid line along the bottom of the filled part.</summary>
public sealed class FillBar : FrameworkElement
{
    private static readonly DependencyProperty ShownProperty = Register("Shown", 0.0);
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(FillBar), new PropertyMetadata(0.0, (d, e) =>
        {
            var bar = (FillBar)d;
            var to = Math.Clamp((double)e.NewValue, 0, 1);
            // A transfer that starts over does not run backwards on screen.
            Motion.To(bar, ShownProperty, to, to < (double)bar.GetValue(ShownProperty) ? 0 : 180);
        }));
    public static readonly DependencyProperty RadiusProperty = Register(nameof(Radius), 18.0);
    public static readonly DependencyProperty TintProperty = Register<Brush?>(nameof(Tint), null);
    public static readonly DependencyProperty LineProperty = Register<Brush?>(nameof(Line), null);

    public FillBar() => IsHitTestVisible = false;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Brush? Line { get => (Brush?)GetValue(LineProperty); set => SetValue(LineProperty, value); }

    private static DependencyProperty Register<T>(string name, T value) =>
        DependencyProperty.Register(name, typeof(T), typeof(FillBar),
            new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        var shown = (double)GetValue(ShownProperty);
        if (shown <= 0 || size.Width <= 0) return;
        var filled = size.Width * shown;
        dc.PushClip(new RectangleGeometry(new Rect(size), Radius, Radius));
        dc.DrawRectangle(Tint, null, new Rect(0, 0, filled, size.Height));
        dc.DrawRectangle(Line, null, new Rect(0, size.Height - 3, filled, 3));
        dc.Pop();
    }
}

/// <summary>Clips its child to rounded corners, which a border does not.</summary>
public sealed class RoundClip : Decorator
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(RoundClip),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    protected override Size ArrangeOverride(Size size)
    {
        var arranged = base.ArrangeOverride(size);
        Clip = new RectangleGeometry(new Rect(size), Radius, Radius);
        return arranged;
    }
}

/// <summary>What a button's template needs to know that a button does not carry.</summary>
public static class Plate
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached(
        "Progress", typeof(double), typeof(Plate), new PropertyMetadata(0.0));

    public static double GetProgress(DependencyObject target) => (double)target.GetValue(ProgressProperty);
    public static void SetProgress(DependencyObject target, double value) => target.SetValue(ProgressProperty, value);

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Plate), new PropertyMetadata(""));

    public static string GetGlyph(DependencyObject target) => (string)target.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject target, string value) => target.SetValue(GlyphProperty, value);
}
