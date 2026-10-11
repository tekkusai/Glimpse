namespace SpacePreview.Services;

/// <summary>
/// Tells a held Space from a new press, from the hook's own events alone.
/// A press the keymap acted on latches until its key-up; its auto-repeats
/// then carry no new meaning. A lost key-up expires on its own once the
/// repeats stop arriving.
/// </summary>
internal sealed class SpaceKeyLatch
{
    private readonly uint _repeatWindowMilliseconds;
    private bool _held;
    private uint _lastDownTime;

    /// <param name="repeatWindowMilliseconds">
    /// Longest gap between two downs of one hold: the keyboard repeat delay
    /// plus slack. Windows' slowest delay is 1 s, its slowest rate 400 ms.
    /// </param>
    public SpaceKeyLatch(uint repeatWindowMilliseconds)
    {
        _repeatWindowMilliseconds = repeatWindowMilliseconds;
    }

    /// <summary>The window for this machine's keyboard repeat delay (0 to 3, 250 ms steps).</summary>
    public static uint RepeatWindowFor(int keyboardDelaySetting) =>
        (uint)(Math.Clamp(keyboardDelaySetting, 0, 3) + 1) * 250 + 200;

    public bool IsRepeat(uint time) =>
        _held && unchecked(time - _lastDownTime) <= _repeatWindowMilliseconds;

    /// <summary>Records a Space down. A fresh press latches only when the keymap acted on it.</summary>
    public void Press(uint time, bool acted)
    {
        if (!IsRepeat(time))
        {
            _held = acted;
        }

        _lastDownTime = time;
    }

    public void Release() => _held = false;

    public void Reset() => _held = false;
}
