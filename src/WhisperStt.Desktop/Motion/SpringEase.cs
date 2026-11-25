using System.Windows;
using System.Windows.Media.Animation;

namespace WhisperStt.Desktop.Motion;

// Spring easing for WPF animations. DampingRatio 1 = critically damped (settles without overshoot,
// the default for UI); about 0.8 = a little bounce, for motion the user threw. The animation's
// Duration plays the role of the spring's response: the curve is within 1% of the target at the end.
// WPF animations without From start from the current on-screen value, so they stay interruptible.
// Honors Windows' "Animation effects" setting: when it is off, springs jump straight to the end.
public sealed class SpringEase : EasingFunctionBase
{
    // ω·Duration for which a critically damped spring has covered 99% of the distance.
    private const double Stiffness = 6.6;

    public static readonly DependencyProperty DampingRatioProperty = DependencyProperty.Register(
        nameof(DampingRatio), typeof(double), typeof(SpringEase), new PropertyMetadata(1.0));

    public SpringEase() => EasingMode = EasingMode.EaseIn; // EaseInCore below is already the final curve

    public static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

    public double DampingRatio
    {
        get => (double)GetValue(DampingRatioProperty);
        set => SetValue(DampingRatioProperty, value);
    }

    protected override double EaseInCore(double normalizedTime)
    {
        if (ReducedMotion || normalizedTime >= 1)
            return 1;
        return Position(normalizedTime) / Position(1);
    }

    // Position of a spring released at rest from 0 toward 1.
    private double Position(double normalizedTime)
    {
        var zeta = Math.Clamp(DampingRatio, 0.1, 1.0);
        var t = Stiffness * normalizedTime;
        if (zeta >= 0.999)
            return 1 - (1 + t) * Math.Exp(-t);
        var damped = Math.Sqrt(1 - zeta * zeta);
        return 1 - Math.Exp(-zeta * t) * (Math.Cos(damped * t) + zeta / damped * Math.Sin(damped * t));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
