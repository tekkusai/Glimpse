using System.Windows;
using System.Windows.Media;
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

    public static bool Enabled => Override ?? SystemParameters.ClientAreaAnimation;

    /// <summary>Tests turn motion on or off here. Nothing in the app writes it.</summary>
    internal static bool? Override { get; set; }

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

    /// <summary>
    /// Lights an element twice and leaves it as it was: the answer to a
    /// press that can change nothing else on screen. The light is on in the
    /// press's own frame, holds long enough to be seen, and fades. With
    /// motion off it lights once and goes out with no fade, because the
    /// press still needs its answer.
    /// </summary>
    public static void Flash(UIElement element, double peak)
    {
        static KeyTime At(int milliseconds) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds));

        var flash = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, At(0)));
        if (Enabled)
        {
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(peak, At(110)));
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, At(290)));
            flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(peak, At(350)));
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(peak, At(460)));
            flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, At(640)));
        }
        else
        {
            flash.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, At(400)));
        }

        element.BeginAnimation(UIElement.OpacityProperty, flash);
    }

    /// <summary>
    /// A sideways slide for a template part, set from a trigger: a XAML
    /// storyboard cannot ask the switch, so a part that slides is moved
    /// from here (the settings toggle's thumb).
    /// </summary>
    public static readonly DependencyProperty SlideXProperty = DependencyProperty.RegisterAttached(
        "SlideX",
        typeof(double),
        typeof(Motion),
        new PropertyMetadata(0.0, OnSlideXChanged));

    public static double GetSlideX(UIElement element) => (double)element.GetValue(SlideXProperty);

    public static void SetSlideX(UIElement element, double value) => element.SetValue(SlideXProperty, value);

    private static void OnSlideXChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not UIElement element)
        {
            return;
        }

        // A transform declared in a template is shared and frozen; each
        // element slides on one of its own.
        if (element.RenderTransform is not TranslateTransform { IsFrozen: false } slide)
        {
            slide = new TranslateTransform((double)change.OldValue, 0);
            element.RenderTransform = slide;
        }

        // From wherever it is now, as Fade does: the base value takes the
        // end state first.
        var from = slide.X;
        var to = (double)change.NewValue;
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        slide.X = to;
        if (!Enabled || Math.Abs(from - to) < 0.001)
        {
            return;
        }

        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    /// <summary>Cubic ease-out (opening) or ease-in (leaving) for a 0..1 progress.</summary>
    public static double Ease(double progress, bool easeIn)
    {
        var t = Math.Clamp(progress, 0, 1);
        return easeIn ? t * t * t : 1 - Math.Pow(1 - t, 3);
    }
}
