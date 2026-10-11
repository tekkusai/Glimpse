using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>How the item on screen makes way for the next one.</summary>
internal enum SwapStyle
{
    /// <summary>Nothing of the old item is on screen: drop it at once.</summary>
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
/// One load's presentation decisions, as plain state on an injected clock.
/// The window owns the timers and the pixels; this owns the yes and no. A
/// superseded load never reveals, the first owner reveals and later ones are
/// told no, the window fades in once, the old item's beat ends before the
/// next item shows, and the wait shows its stand-in, sheen and reason only
/// while nothing has been revealed.
/// </summary>
internal sealed class RevealPlan
{
    public static readonly TimeSpan StandInAt = Motion.OpenWait;
    public static readonly TimeSpan SheenAt = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan ReasonAt = TimeSpan.FromMilliseconds(1200);

    /// <summary>A browser view that never finishes navigating still reveals.</summary>
    public static readonly TimeSpan EscapeAfter = TimeSpan.FromMilliseconds(1400);

    /// <summary>A PDF waits this long at most for its first rail decision.</summary>
    public static readonly TimeSpan RailGateCap = TimeSpan.FromMilliseconds(520);

    /// <summary>A video with no probed shape waits this long at most for its metadata.</summary>
    public static readonly TimeSpan MediaShapeCap = TimeSpan.FromMilliseconds(600);

    private readonly Func<TimeSpan> _sinceInput;
    private readonly bool _motion;
    private TaskCompletionSource _beat = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// A fresh open has nothing on screen to make way, so its beat is over;
    /// a browse step's window is already on screen, so it has nothing left
    /// to fade in.
    /// </summary>
    public RevealPlan(long version, bool freshOpen, Func<TimeSpan> sinceInput, bool motion)
    {
        Version = version;
        FreshOpen = freshOpen;
        _sinceInput = sinceInput;
        _motion = motion;
        if (freshOpen)
        {
            _beat.TrySetResult();
        }
        else
        {
            WindowShown = true;
        }
    }

    public long Version { get; }

    public bool FreshOpen { get; }

    public TimeSpan Elapsed => _sinceInput();

    public bool Superseded { get; private set; }

    public bool Revealed { get; private set; }

    public bool WindowShown { get; private set; }

    /// <summary>The old item has left (or stays, holding): content may attach and the stage may move.</summary>
    public bool BeatDone => _beat.Task.IsCompleted;

    /// <summary>Completes when the beat ends; a view staged meanwhile reveals only after it.</summary>
    public Task BeatEnded => _beat.Task;

    public SwapStyle Swap { get; set; } = SwapStyle.Clear;

    /// <summary>Why the wait is long, when there is a reason worth one line.</summary>
    public string? Reason { get; set; }

    /// <summary>A newer load took over. Anything waiting on this one's beat wakes and finds it stale.</summary>
    public void Supersede()
    {
        Superseded = true;
        _beat.TrySetResult();
    }

    /// <summary>The item on screen starts to make way; a second beat in one load waits again.</summary>
    public void BeginBeat()
    {
        if (_beat.Task.IsCompleted && !Superseded)
        {
            _beat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void EndBeat() => _beat.TrySetResult();

    /// <summary>True for the first owner only, and never once superseded.</summary>
    public bool TryReveal()
    {
        if (Superseded || Revealed)
        {
            return false;
        }

        Revealed = true;
        return true;
    }

    /// <summary>The window fades in once per open: with its content, or at the end of the wait.</summary>
    public bool TryShowWindow()
    {
        if (Superseded || WindowShown)
        {
            return false;
        }

        WindowShown = true;
        return true;
    }

    /// <summary>
    /// What the wait shows at <paramref name="elapsed"/> since the input:
    /// nothing new at first, then the still stand-in, then a sheen (only
    /// with motion on), then the reason line when there is one. Nothing
    /// shows while the old item is still leaving. Over an item that holds,
    /// a stand-in without the content's shape would be a blank beat, so it
    /// waits until the wait itself needs showing.
    /// </summary>
    public WaitView WaitAt(TimeSpan elapsed, bool shaped)
    {
        if (Superseded || Revealed || !BeatDone || elapsed < StandInAt)
        {
            return WaitView.Nothing;
        }

        if (Swap == SwapStyle.Hold && !shaped && elapsed < SheenAt)
        {
            return WaitView.Nothing;
        }

        var view = WaitView.StandIn;
        if (_motion && elapsed >= SheenAt)
        {
            view |= WaitView.Sheen;
        }

        if (Reason is not null && elapsed >= ReasonAt)
        {
            view |= WaitView.Reason;
        }

        return view;
    }

    /// <summary>
    /// The wait at a timer's moment. Windows timers fire on 15.6 ms ticks, so
    /// a timer can fire a tick early; it still counts as its moment.
    /// </summary>
    public WaitView WaitAtMoment(TimeSpan moment, bool shaped)
    {
        var elapsed = Elapsed;
        return WaitAt(elapsed > moment ? elapsed : moment, shaped);
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

    /// <summary>Waits for a gate, never past its cap. True when the gate answered.</summary>
    public static async Task<bool> WaitCappedAsync(Task gate, TimeSpan cap, Func<TimeSpan, Task> delay)
    {
        if (gate.IsCompleted)
        {
            return true;
        }

        return await Task.WhenAny(gate, delay(cap)) == gate;
    }
}

/// <summary>
/// One browser view's reveal. Navigation and the escape hatch race for it;
/// the first taker owns the reveal and every later one is told no, so a
/// slow gate can never reveal twice or reveal a superseded load.
/// </summary>
internal sealed class RevealTicket(Func<bool> stillCurrent)
{
    public bool Taken { get; private set; }

    public bool TryTake()
    {
        if (Taken || !stillCurrent())
        {
            return false;
        }

        Taken = true;
        return true;
    }
}
