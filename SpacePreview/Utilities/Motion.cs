using System.Windows;
using System.Windows.Media.Animation;

namespace SpacePreview.Utilities;

/// <summary>
/// The one switch for motion (docs/DESIGN.md, Motion). With Windows' "Show
/// animations" off, every transition lands on its end state at once and
/// waiting states stay still.
/// </summary>
internal static class Motion
{
    /// <summary>How long a fresh open waits for its content before it shows anyway.</summary>
    public static readonly TimeSpan OpenWait = TimeSpan.FromMilliseconds(120);

    /// <summary>The window fading in.</summary>
    public static readonly TimeSpan Open = TimeSpan.FromMilliseconds(140);

    /// <summary>Content dissolving in from the canvas or its stand-in.</summary>
    public static readonly TimeSpan Reveal = TimeSpan.FromMilliseconds(160);

    /// <summary>The next item dissolving over the current one.</summary>
    public static readonly TimeSpan Browse = TimeSpan.FromMilliseconds(140);

    /// <summary>The current item fading out before a new shape, and the window closing.</summary>
    public static readonly TimeSpan Leave = TimeSpan.FromMilliseconds(90);

    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Fades an element from wherever it is now. The base value is set to the
    /// end state first, so removing the animation later can never resurrect
    /// an old opacity.
    /// </summary>
    public static void Fade(UIElement element, double to, TimeSpan duration, bool easeIn = false, Action? completed = null)
    {
        var from = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = to;
        if (!Enabled || duration <= TimeSpan.Zero || Math.Abs(from - to) < 0.001)
        {
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = easeIn ? EasingMode.EaseIn : EasingMode.EaseOut }
        };
        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>Stops a fade where it would have ended.</summary>
    public static void Settle(UIElement element, double opacity)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = opacity;
    }

    /// <summary>Cubic ease-out (opening) or ease-in (leaving) for a 0..1 progress.</summary>
    public static double Ease(double progress, bool easeIn)
    {
        var t = Math.Clamp(progress, 0, 1);
        return easeIn ? t * t * t : 1 - Math.Pow(1 - t, 3);
    }
}
