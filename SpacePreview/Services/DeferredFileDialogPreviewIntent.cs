namespace SpacePreview.Services;

internal enum DeferredFileDialogPreviewDecision
{
    Ignore,
    Drop,
    Fulfill
}

/// <param name="Refresh">
/// The read of the picker this press asked for. Its answer is the file the
/// press was on; the answer to any later read is not.
/// </param>
internal sealed record DeferredFileDialogPreviewIntent(
    IntPtr DialogRoot,
    int RequestGeneration,
    long ArmedAtTick,
    long InputStamp = 0,
    long Refresh = 0)
{
    public bool IsCurrent(
        IntPtr completedDialog,
        long completedRefresh,
        int currentGeneration,
        long now,
        int lifetimeMilliseconds)
    {
        var age = now - ArmedAtTick;
        return RequestGeneration > 0
            && RequestGeneration == currentGeneration
            && DialogRoot != IntPtr.Zero
            && DialogRoot == completedDialog
            && Refresh == completedRefresh
            && lifetimeMilliseconds > 0
            && age >= 0
            && age <= lifetimeMilliseconds;
    }
}

/// <summary>
/// One immutable deferred picker request, exchanged atomically between the
/// keyboard hook thread and the dispatcher. The hook thread only cancels;
/// the dispatcher arms, evaluates and expires. The first read of the picker
/// to complete settles it: its own read with a selection fulfills, anything
/// else drops. Every cancel, expiry, replacement, or fulfillment is
/// compare-and-swap guarded.
/// </summary>
internal sealed class DeferredFileDialogPreviewIntentSlot
{
    private DeferredFileDialogPreviewIntent? _current;

    public DeferredFileDialogPreviewIntent? Current => Volatile.Read(ref _current);

    public DeferredFileDialogPreviewIntent Arm(
        IntPtr dialogRoot,
        int requestGeneration,
        long armedAtTick,
        long inputStamp = 0,
        long refresh = 0)
    {
        var intent = new DeferredFileDialogPreviewIntent(
            dialogRoot,
            requestGeneration,
            armedAtTick,
            inputStamp,
            refresh);
        Interlocked.Exchange(ref _current, intent);
        return intent;
    }

    public void Cancel() => Interlocked.Exchange(ref _current, null);

    public bool Expire(DeferredFileDialogPreviewIntent intent) => Cancel(intent);

    public DeferredFileDialogPreviewDecision EvaluateRefresh(
        DeferredFileDialogPreviewIntent intent,
        IntPtr completedDialog,
        long completedRefresh,
        int currentGeneration,
        long now,
        int lifetimeMilliseconds,
        bool ownershipValid,
        bool selectionAvailable)
    {
        if (!ReferenceEquals(Current, intent))
        {
            return DeferredFileDialogPreviewDecision.Ignore;
        }

        if (!intent.IsCurrent(
                completedDialog,
                completedRefresh,
                currentGeneration,
                now,
                lifetimeMilliseconds)
            || !ownershipValid
            || !selectionAvailable)
        {
            Cancel(intent);
            return DeferredFileDialogPreviewDecision.Drop;
        }

        return Cancel(intent)
            ? DeferredFileDialogPreviewDecision.Fulfill
            : DeferredFileDialogPreviewDecision.Ignore;
    }

    private bool Cancel(DeferredFileDialogPreviewIntent intent) =>
        ReferenceEquals(
            Interlocked.CompareExchange(ref _current, null, intent),
            intent);
}
