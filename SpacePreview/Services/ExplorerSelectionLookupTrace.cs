using System.Diagnostics;
using System.Threading;

namespace SpacePreview.Services;

internal enum ExplorerSelectionLookupPhase
{
    ActiveTab,
    Census,
    Selection,
    View
}

/// <summary>
/// Reports only slow selection lookups. Four service buckets retain useful
/// attribution without timing every individual COM call.
/// </summary>
internal sealed class ExplorerSelectionLookupTrace
{
    internal const double SlowThresholdMilliseconds = 80;

    private readonly long _queuedAt = Stopwatch.GetTimestamp();
    private readonly long[] _phaseTicks = new long[4];
    private long _staStartedAt;
    private long _staCompletedAt;
    private int _entryCount = -1;
    private string _outcome = "unknown";

    public void MarkStaStarted() =>
        Interlocked.CompareExchange(ref _staStartedAt, Stopwatch.GetTimestamp(), 0);

    public void MarkStaCompleted() =>
        Volatile.Write(ref _staCompletedAt, Stopwatch.GetTimestamp());

    public void SetEntryCount(int count) => Volatile.Write(ref _entryCount, count);

    public void SetOutcome(string outcome) => Volatile.Write(ref _outcome, outcome);

    public static PhaseScope Track(
        ExplorerSelectionLookupTrace? trace,
        ExplorerSelectionLookupPhase phase) => new(trace, phase);

    public void WriteIfSlow(string origin, bool found)
    {
        var completedAt = Stopwatch.GetTimestamp();
        var totalMilliseconds = ToMilliseconds(completedAt - _queuedAt);
        if (totalMilliseconds < SlowThresholdMilliseconds)
        {
            return;
        }

        var staStartedAt = Volatile.Read(ref _staStartedAt);
        var staCompletedAt = Volatile.Read(ref _staCompletedAt);
        var dispatchMilliseconds = staStartedAt > 0
            ? ToMilliseconds(staStartedAt - _queuedAt)
            : 0;
        var serviceMilliseconds = staStartedAt > 0 && staCompletedAt >= staStartedAt
            ? ToMilliseconds(staCompletedAt - staStartedAt)
            : 0;
        var continuationMilliseconds = staCompletedAt > 0
            ? ToMilliseconds(completedAt - staCompletedAt)
            : 0;
        var activeTabMilliseconds = PhaseMilliseconds(ExplorerSelectionLookupPhase.ActiveTab);
        var censusMilliseconds = PhaseMilliseconds(ExplorerSelectionLookupPhase.Census);
        var selectionMilliseconds = PhaseMilliseconds(ExplorerSelectionLookupPhase.Selection);
        var viewMilliseconds = PhaseMilliseconds(ExplorerSelectionLookupPhase.View);
        var otherMilliseconds = Math.Max(
            0,
            serviceMilliseconds - activeTabMilliseconds - censusMilliseconds - selectionMilliseconds - viewMilliseconds);

        DiagnosticLog.Write(
            $"selection lookup slow +{totalMilliseconds:0}ms origin={origin} result={(found ? "hit" : "miss")} " +
            $"timing staDispatch={dispatchMilliseconds:0} service={serviceMilliseconds:0} activeTab={activeTabMilliseconds:0} " +
            $"census={censusMilliseconds:0} selection={selectionMilliseconds:0} view={viewMilliseconds:0} " +
            $"other={otherMilliseconds:0} continuation={continuationMilliseconds:0} " +
            $"entries={Volatile.Read(ref _entryCount)} outcome={Volatile.Read(ref _outcome)}");
    }

    private double PhaseMilliseconds(ExplorerSelectionLookupPhase phase) =>
        ToMilliseconds(Volatile.Read(ref _phaseTicks[(int)phase]));

    private void Add(ExplorerSelectionLookupPhase phase, long ticks) =>
        Interlocked.Add(ref _phaseTicks[(int)phase], ticks);

    private static double ToMilliseconds(long ticks) =>
        ticks <= 0 ? 0 : ticks * 1000d / Stopwatch.Frequency;

    internal readonly struct PhaseScope : IDisposable
    {
        private readonly ExplorerSelectionLookupTrace? _trace;
        private readonly ExplorerSelectionLookupPhase _phase;
        private readonly long _startedAt;

        public PhaseScope(ExplorerSelectionLookupTrace? trace, ExplorerSelectionLookupPhase phase)
        {
            _trace = trace;
            _phase = phase;
            _startedAt = trace is null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_trace is not null)
            {
                _trace.Add(_phase, Stopwatch.GetTimestamp() - _startedAt);
            }
        }
    }
}
