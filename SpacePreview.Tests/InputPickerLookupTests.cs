using System.Windows.Threading;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Asking a picker what is selected goes to another process and can take as
/// long as that process likes. One question is out per dialog at a time, and
/// a dialog that does not answer in time is not asked again: a provider
/// that hangs holds one worker, however often the picker is looked at.
/// </summary>
public sealed class InputPickerLookupTests
{
    /// <summary>
    /// The fault: every refresh started a lookup of its own, so a slow
    /// provider had as many blocked workers as there were selection events.
    /// </summary>
    [Fact]
    public Task APickerIsAskedOnceAtATimeAndAgainForWhatMovedMeanwhile() => WithMonitor(async monitor =>
    {
        using var picker = TestPicker.Open();
        var first = picker.Selected("first.txt");
        var last = picker.Selected("last.txt");
        var lookups = new PickerLookups { Selected = first };
        lookups.Attach(monitor);
        var reported = new List<ExplorerSelection?>();
        monitor.SelectionChanged += (_, selection) => reported.Add(selection);

        lookups.Hold();
        monitor.RequestRefresh(picker.Handle, settleMilliseconds: 0);
        await HookThread.Until(() => lookups.Started == 1, "The picker was never asked.");

        // The selection moves three times while the first answer is out.
        for (var i = 0; i < 3; i++)
        {
            monitor.RequestRefresh(picker.Handle, settleMilliseconds: 0);
            await Task.Delay(40);
        }

        Assert.Equal(1, lookups.Started);

        lookups.Selected = last;
        lookups.Release();
        await HookThread.Until(() => reported.Count > 0, "The picker's answer never arrived.");
        await Task.Delay(100);

        // The answer that was out is stale; one more question gets the newest.
        Assert.Equal(2, lookups.Started);
        Assert.Equal(1, lookups.MostAtOnce);
        Assert.Equal([last], reported);
        Assert.True(monitor.TryGetCachedSelection(picker.Handle, out var cached));
        Assert.Equal(last, cached);
    });

    /// <summary>
    /// The fault: the wait was counted against the newest refresh. Each new
    /// selection event moved it on, so a provider that never answered was
    /// never given up on, and each event left another worker blocked.
    /// </summary>
    [Fact]
    public Task APickerThatDoesNotAnswerInTimeIsNotAskedAgainHoweverOftenItMoves() => WithMonitor(async monitor =>
    {
        using var picker = TestPicker.Open();
        var lookups = new PickerLookups();
        lookups.Attach(monitor, timeout: TimeSpan.FromMilliseconds(150));

        lookups.Hold();
        try
        {
            // Selection events keep coming, closer together than the wait is long.
            for (var i = 0; i < 15; i++)
            {
                monitor.RequestRefresh(picker.Handle, settleMilliseconds: 0);
                await Task.Delay(40);
            }

            Assert.Equal(1, lookups.Started);
            Assert.False(monitor.TryGetCachedSelection(picker.Handle, out _));

            // Given up on for good: coming back to it asks nothing.
            monitor.RequestRefresh(picker.Handle, settleMilliseconds: 0);
            await Task.Delay(100);
            Assert.Equal(1, lookups.Started);
        }
        finally
        {
            lookups.Release();
        }
    });

    /// <summary>
    /// A glance over a picker that stops answering would go on showing a
    /// file the user has left. The picker is reported as having nothing
    /// selected, which ends the glance.
    /// </summary>
    [Fact]
    public Task APickerThatStopsAnsweringIsReportedAsHavingNothingSelected() => WithMonitor(async monitor =>
    {
        using var picker = TestPicker.Open();
        var selected = picker.Selected("selected.txt");
        var lookups = new PickerLookups { Selected = selected };
        lookups.Attach(monitor, timeout: TimeSpan.FromMilliseconds(150));
        var reported = new List<ExplorerSelection?>();
        monitor.SelectionChanged += (_, selection) => reported.Add(selection);

        monitor.RequestRefresh(picker.Handle, settleMilliseconds: 0);
        await HookThread.Until(() => reported.Count == 1, "The picker's selection was never read.");
        Assert.Equal([selected], reported);

        lookups.Hold();
        try
        {
            monitor.RequestRefresh(picker.Handle, settleMilliseconds: 0);
            await HookThread.Until(() => reported.Count == 2, "A picker that stopped answering was never reported.");

            Assert.Null(reported[1]);
            Assert.False(monitor.TryGetCachedSelection(picker.Handle, out _));
        }
        finally
        {
            lookups.Release();
        }
    });

    /// <summary>
    /// UI Automation into another process can hang during teardown. The
    /// picker is read off the dispatcher, with one bounded wait; the hook
    /// only ever looks at the finished cache (the keymap table covers the
    /// fail-open press, the hook theory the ban).
    /// </summary>
    [Fact]
    public Task APickerIsReadOffTheDispatcherAndWaitedForTwoSeconds() => WithMonitor(async monitor =>
    {
        Assert.Equal(TimeSpan.FromSeconds(2), Fields.Get<TimeSpan>(monitor, "_lookupTimeout"));

        using var picker = TestPicker.Open();
        var dispatcherThread = Environment.CurrentManagedThreadId;
        var readOn = 0;
        Fields.Set(monitor, "_lookUp", (Func<IntPtr, ExplorerSelection?>)(_ =>
        {
            Volatile.Write(ref readOn, Environment.CurrentManagedThreadId);
            return null;
        }));
        var completed = 0L;
        monitor.RefreshCompleted += (_, _, read) => completed = read;

        var asked = monitor.RequestRefresh(picker.Handle, settleMilliseconds: 0);
        await HookThread.Until(() => completed != 0, "The read never came back.");

        Assert.Equal(asked, completed);
        Assert.NotEqual(0, Volatile.Read(ref readOn));
        Assert.NotEqual(dispatcherThread, Volatile.Read(ref readOn));
    });

    /// <summary>One slow picker does not hold up another.</summary>
    [Fact]
    public Task AnotherPickerIsAskedWhileTheFirstHasNotAnswered() => WithMonitor(async monitor =>
    {
        using var slow = TestPicker.Open();
        using var other = TestPicker.Open();
        var lookups = new PickerLookups();
        lookups.Attach(monitor);

        lookups.Hold();
        try
        {
            monitor.RequestRefresh(slow.Handle, settleMilliseconds: 0);
            await HookThread.Until(() => lookups.Started == 1);

            monitor.RequestRefresh(other.Handle, settleMilliseconds: 0);
            await HookThread.Until(() => lookups.Started == 2, "A second picker waited for the first one's answer.");
        }
        finally
        {
            lookups.Release();
        }
    });

    private static Task WithMonitor(Func<CommonFileDialogSelectionMonitor, Task> test) => WpfApp.RunAsync(async () =>
    {
        var monitor = new CommonFileDialogSelectionMonitor(Dispatcher.CurrentDispatcher);
        try
        {
            await test(monitor);
        }
        finally
        {
            monitor.Dispose();
        }
    });
}
