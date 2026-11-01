using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;

namespace Winawdl.Theme;

/// <summary>How far a transfer has got, as a ring that closes clockwise from
/// the top around a device, the way AirDrop draws it. Empty draws nothing.</summary>
public sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(Ring),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (owner, change) =>
        {
            if (UIElementAutomationPeer.FromElement((Ring)owner) is RingPeer peer)
                peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty,
                    Percent((double)change.OldValue), Percent((double)change.NewValue));
        }));
    public static readonly DependencyProperty ThicknessProperty = Register(nameof(Thickness), 3.0);
    public static readonly DependencyProperty StrokeProperty = Register<Brush?>(nameof(Stroke), null);
    public static readonly DependencyProperty TrackProperty = Register<Brush?>(nameof(Track), null);

    /// <summary>0 to 1.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary>The whole circle under the ring, while there is a ring.</summary>
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    private static double Percent(double progress) => double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) * 100 : 0;

    protected override AutomationPeer OnCreateAutomationPeer() => new RingPeer(this);

    private sealed class RingPeer(Ring owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(Ring);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;
        public override object? GetPattern(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPattern(pattern);
        public bool IsReadOnly => true;
        public double Minimum => 0;
        public double Maximum => 100;
        public double Value => Percent(owner.Progress);
        public double SmallChange => double.NaN;
        public double LargeChange => double.NaN;
        public void SetValue(double value) => throw new InvalidOperationException("Transfer progress is read-only.");
    }

    private static DependencyProperty Register<T>(string name, T value) =>
        DependencyProperty.Register(name, typeof(T), typeof(Ring),
            new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        var progress = Percent(Progress) / 100;
        var thickness = Thickness;
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - thickness / 2;
        if (progress <= 0 || radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (Track is { } track)
            dc.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);
        var pen = new Pen(Stroke ?? Brushes.DodgerBlue, thickness)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round,
        };
        if (progress >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }
        var angle = progress * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(end, new Size(radius, radius), 0, progress > 0.5, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }
}
