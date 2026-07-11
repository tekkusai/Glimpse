using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class DeferredFileDialogPreviewIntentTests
{
    private static readonly IntPtr Dialog = new(123);

    [Fact]
    public void VerifiedCompletionFulfillsAndConsumesIntent()
    {
        var slot = new DeferredFileDialogPreviewIntentSlot();
        var intent = slot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);

        var decision = Complete(slot, intent, now: 1100);

        Assert.Equal(DeferredFileDialogPreviewDecision.Fulfill, decision);
        Assert.Null(slot.Current);
    }

    [Fact]
    public void SuspensionCancellationMakesInflightCompletionHarmless()
    {
        var slot = new DeferredFileDialogPreviewIntentSlot();
        var intent = slot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);
        slot.Cancel();

        var decision = Complete(slot, intent, now: 1100);

        Assert.Equal(DeferredFileDialogPreviewDecision.Ignore, decision);
        Assert.Null(slot.Current);
    }

    [Fact]
    public void TeardownCancellationDefusesDelayedExpiryAndCompletion()
    {
        var slot = new DeferredFileDialogPreviewIntentSlot();
        var intent = slot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);
        slot.Cancel();

        Assert.False(slot.Expire(intent));
        Assert.Equal(
            DeferredFileDialogPreviewDecision.Ignore,
            Complete(slot, intent, now: 1100));
        Assert.Null(slot.Current);
    }

    [Fact]
    public void OwnershipLossDropsAndConsumesIntent()
    {
        var slot = new DeferredFileDialogPreviewIntentSlot();
        var intent = slot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);

        var decision = slot.EvaluateRefresh(
            intent,
            Dialog,
            currentGeneration: 10,
            now: 1100,
            lifetimeMilliseconds: 1200,
            ownershipValid: false,
            selectionAvailable: true,
            cacheReady: true);

        Assert.Equal(DeferredFileDialogPreviewDecision.Drop, decision);
        Assert.Null(slot.Current);
    }

    [Fact]
    public void OldExpiryCannotCancelReplacementIntent()
    {
        var slot = new DeferredFileDialogPreviewIntentSlot();
        var old = slot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);
        var replacement = slot.Arm(Dialog, requestGeneration: 11, armedAtTick: 1050);

        Assert.False(slot.Expire(old));
        Assert.Same(replacement, slot.Current);
    }

    [Fact]
    public void RepeatedSpaceReplacesRatherThanStacksIntents()
    {
        var slot = new DeferredFileDialogPreviewIntentSlot();
        var first = slot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);
        var second = slot.Arm(Dialog, requestGeneration: 11, armedAtTick: 1010);

        Assert.Equal(DeferredFileDialogPreviewDecision.Ignore, Complete(slot, first, 1100, 10));
        Assert.Equal(DeferredFileDialogPreviewDecision.Fulfill, Complete(slot, second, 1100, 11));
        Assert.Null(slot.Current);
    }

    [Fact]
    public void SupersedingRequestGenerationDropsCurrentIntent()
    {
        var slot = new DeferredFileDialogPreviewIntentSlot();
        var intent = slot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);

        Assert.Equal(
            DeferredFileDialogPreviewDecision.Drop,
            Complete(slot, intent, now: 1100, currentGeneration: 11));
        Assert.Null(slot.Current);
    }

    [Fact]
    public void NullCompletionWaitsThenVerifiedCompletionFulfills()
    {
        var slot = new DeferredFileDialogPreviewIntentSlot();
        var intent = slot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);

        var waiting = slot.EvaluateRefresh(
            intent,
            Dialog,
            currentGeneration: 10,
            now: 1050,
            lifetimeMilliseconds: 1200,
            ownershipValid: true,
            selectionAvailable: false,
            cacheReady: false);

        Assert.Equal(DeferredFileDialogPreviewDecision.Wait, waiting);
        Assert.Same(intent, slot.Current);
        Assert.Equal(DeferredFileDialogPreviewDecision.Fulfill, Complete(slot, intent, 1100));
    }

    [Fact]
    public void NewerRefreshInFlightWaitsWithoutConsumingIntent()
    {
        var slot = new DeferredFileDialogPreviewIntentSlot();
        var intent = slot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);

        var waiting = slot.EvaluateRefresh(
            intent,
            Dialog,
            currentGeneration: 10,
            now: 1050,
            lifetimeMilliseconds: 1200,
            ownershipValid: true,
            selectionAvailable: true,
            cacheReady: false);

        Assert.Equal(DeferredFileDialogPreviewDecision.Wait, waiting);
        Assert.Same(intent, slot.Current);
        Assert.Equal(DeferredFileDialogPreviewDecision.Fulfill, Complete(slot, intent, 1100));
    }

    [Fact]
    public void ExpiredOrReplacementDialogCompletionDropsIntent()
    {
        var expiredSlot = new DeferredFileDialogPreviewIntentSlot();
        var expired = expiredSlot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);
        Assert.Equal(
            DeferredFileDialogPreviewDecision.Drop,
            Complete(expiredSlot, expired, now: 2201));
        Assert.Null(expiredSlot.Current);

        var replacedSlot = new DeferredFileDialogPreviewIntentSlot();
        var replaced = replacedSlot.Arm(Dialog, requestGeneration: 10, armedAtTick: 1000);
        var decision = replacedSlot.EvaluateRefresh(
            replaced,
            completedDialog: new IntPtr(456),
            currentGeneration: 10,
            now: 1100,
            lifetimeMilliseconds: 1200,
            ownershipValid: true,
            selectionAvailable: true,
            cacheReady: true);

        Assert.Equal(DeferredFileDialogPreviewDecision.Drop, decision);
        Assert.Null(replacedSlot.Current);
    }

    private static DeferredFileDialogPreviewDecision Complete(
        DeferredFileDialogPreviewIntentSlot slot,
        DeferredFileDialogPreviewIntent intent,
        long now,
        int currentGeneration = 10) =>
        slot.EvaluateRefresh(
            intent,
            Dialog,
            currentGeneration,
            now,
            lifetimeMilliseconds: 1200,
            ownershipValid: true,
            selectionAvailable: true,
            cacheReady: true);
}
