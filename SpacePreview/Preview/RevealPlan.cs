using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>How the item on screen makes way for the next one.</summary>
internal enum SwapStyle
{
    /// <summary>Nothing of the old item is on screen.</summary>
    Clear,

    /// <summary>It stays until the next item dissolves over it (same shape, no browser window in the way).</summary>
    Hold,

    /// <summary>It fades out, then the stage takes the new shape in one step.</summary>
    FadeOut
}

/// <summary>What the wait shows at a moment, before anything is revealed.</summary>
[Flags]
internal enum WaitView
{
    Nothing = 0,
    StandIn = 1,
    Sheen = 2,
    Reason = 4
}

/// <summary>
/// The reveal's yes and no, as pure functions of a moment on a load's clock
/// (docs/DESIGN.md, Motion and Waiting). The window keeps the state and the
/// pixels; nothing here remembers anything.
/// </summary>
internal static class RevealPlan
{
    public static readonly TimeSpan StandInAt = Motion.OpenWait;
    public static readonly TimeSpan SheenAt = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan ReasonAt = TimeSpan.FromMilliseconds(1200);

    /// <summary>A browser view whose navigation never finishes still reveals, this long after it began.</summary>
    public static readonly TimeSpan EscapeAfter = TimeSpan.FromMilliseconds(1400);

    /// <summary>A PDF waits this long at most for its first rail decision.</summary>
    public static readonly TimeSpan RailGateCap = TimeSpan.FromMilliseconds(520);

    /// <summary>A video with no probed shape waits this long at most for its metadata.</summary>
    public static readonly TimeSpan MediaShapeCap = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// The moment a step runs at. Windows timers fire on 15.6 ms ticks, so a
    /// timer can fire a tick early; it still counts as the moment it was set
    /// for.
    /// </summary>
    public static TimeSpan Moment(TimeSpan elapsed, TimeSpan setFor) => elapsed > setFor ? elapsed : setFor;

    /// <summary>
    /// What a load that has not revealed shows at <paramref name="elapsed"/>
    /// since its input: nothing new at first, then the still stand-in, then
    /// a sheen (only with motion on), then the reason line when there is
    /// one. Over an item still on screen only a poster, a real image of the
    /// next one, takes its place that early; a ghost or the bare canvas
    /// would trade a picture for a flat rectangle, so it waits until the
    /// wait itself needs showing.
    /// </summary>
    public static WaitView WaitAt(TimeSpan elapsed, bool overItem, bool poster, bool reason, bool motion)
    {
        if (elapsed < StandInAt || (overItem && !poster && elapsed < SheenAt))
        {
            return WaitView.Nothing;
        }

        var view = WaitView.StandIn;
        if (motion && elapsed >= SheenAt)
        {
            view |= WaitView.Sheen;
        }

        if (reason && elapsed >= ReasonAt)
        {
            view |= WaitView.Reason;
        }

        return view;
    }

    /// <summary>
    /// The item on screen holds while the next one loads only when nothing
    /// forces it off: it is visible WPF content (a browser window cannot be
    /// drawn over) and the stage keeps its shape. Unknown shape means fade.
    /// </summary>
    public static SwapStyle ChooseSwap(bool outgoingVisible, bool outgoingIsWeb, bool? stageChanges)
    {
        if (!outgoingVisible)
        {
            return SwapStyle.Clear;
        }

        return !outgoingIsWeb && stageChanges == false ? SwapStyle.Hold : SwapStyle.FadeOut;
    }
}
