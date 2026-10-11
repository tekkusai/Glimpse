namespace SpacePreview.Services;

internal enum DeferredFileDialogPreviewDecision
{
    Ignore,
    Wait,
    Drop,
    Fulfill
}

internal sealed record DeferredFileDialogPreviewIntent(
    IntPtr DialogRoot,
    int RequestGeneration,
    long ArmedAtTick,
    long InputStamp = 0)
{
    public bool IsCurrent(
        IntPtr completedDialog,
        int currentGeneration,
        long now,
        int lifetimeMilliseconds)
    {
        var age = now - ArmedAtTick;
        return RequestGeneration > 0
            && RequestGeneration == currentGeneration
            && DialogRoot != IntPtr.Zero
            && DialogRoot == completedDialog
            && lifetimeMilliseconds > 0
            && age >= 0
            && age <= lifetimeMilliseconds;
    }
}

/// <summary>
/// One immutable deferred picker request, exchanged atomically between the
/// hook lane and dispatcher. Refreshes may wait without consuming it; every
/// cancel, expiry, replacement, or fulfillment is compare-and-swap guarded.
/// </summary>
internal sealed class DeferredFileDialogPreviewIntentSlot
{
    private DeferredFileDialogPreviewIntent? _current;

    public DeferredFileDialogPreviewIntent? Current => Volatile.Read(ref _current);

    public DeferredFileDialogPreviewIntent Arm(
        IntPtr dialogRoot,
        int requestGeneration,
        long armedAtTick,
        long inputStamp = 0)
    {
        var intent = new DeferredFileDialogPreviewIntent(
            dialogRoot,
            requestGeneration,
            armedAtTick,
            inputStamp);
        Interlocked.Exchange(ref _current, intent);
        return intent;
    }

    public void Cancel() => Interlocked.Exchange(ref _current, null);

    public bool Expire(DeferredFileDialogPreviewIntent intent) => Cancel(intent);

    public DeferredFileDialogPreviewDecision EvaluateRefresh(
        DeferredFileDialogPreviewIntent intent,
        IntPtr completedDialog,
        int currentGeneration,
        long now,
        int lifetimeMilliseconds,
        bool ownershipValid,
        bool selectionAvailable,
        bool cacheReady)
    {
        if (!ReferenceEquals(Current, intent))
        {
            return DeferredFileDialogPreviewDecision.Ignore;
        }

        if (!intent.IsCurrent(
                completedDialog,
                currentGeneration,
                now,
                lifetimeMilliseconds)
            || !ownershipValid)
        {
            Cancel(intent);
            return DeferredFileDialogPreviewDecision.Drop;
        }

        if (!selectionAvailable || !cacheReady)
        {
            return DeferredFileDialogPreviewDecision.Wait;
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
