using System.Windows;
using System.Windows.Media.Animation;

namespace Winawdl.Theme;

/// <summary>Reaches its end with one small overshoot, the way a dropped thing settles.</summary>
public sealed class SettleEase : EasingFunctionBase
{
    public SettleEase() => EasingMode = EasingMode.EaseIn;

    protected override double EaseInCore(double time) => 1 - Math.Exp(-6 * time) * Math.Cos(9 * time);

    protected override Freezable CreateInstanceCore() => new SettleEase();
}

/// <summary>Every animation goes through here, so that turning animation effects
/// off in Windows makes each state change at once.</summary>
public static class Motion
{
    public const double Quick = 110;
    public const double Move = 240;
    public const double Land = 420;

    public static bool Reduced { get; set; }

    public static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction Settle = Frozen(new SettleEase());

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    public static void To(IAnimatable target, DependencyProperty property, double to, double milliseconds,
                          IEasingFunction? ease = null)
    {
        if (Reduced || milliseconds <= 0)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = ease ?? Out,
        });
    }

    /// <summary>Starts at `from` and comes to rest where the property already is.</summary>
    public static void From(IAnimatable target, DependencyProperty property, double from, double milliseconds,
                            IEasingFunction? ease = null)
    {
        if (Reduced) return;
        target.BeginAnimation(property, new DoubleAnimation
        {
            From = from, Duration = TimeSpan.FromMilliseconds(milliseconds), EasingFunction = ease ?? Out,
        });
    }
}
