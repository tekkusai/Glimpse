using SpacePreview.Native;

namespace SpacePreview.Services;

internal sealed class SpaceKeyLatch
{
    private static readonly TimeSpan DefaultRepairDelay = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Func<int, bool> _isKeyDown;
    private readonly TimeSpan _repairDelay;
    private bool _isDown;
    private CancellationTokenSource? _repair;

    public SpaceKeyLatch(Func<int, bool>? isKeyDown = null, TimeSpan? repairDelay = null)
    {
        _isKeyDown = isKeyDown ?? NativeMethods.IsKeyDown;
        _repairDelay = repairDelay ?? DefaultRepairDelay;
    }

    public bool TryPress()
    {
        lock (_gate)
        {
            if (_isDown)
            {
                return false;
            }

            _isDown = true;
            ScheduleRepair();
            return true;
        }
    }

    public void Release(int vk)
    {
        if (vk != NativeMethods.VK_SPACE)
        {
            return;
        }

        lock (_gate)
        {
            Clear();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Clear();
        }
    }

    internal void RepairIfReleased()
    {
        lock (_gate)
        {
            RepairIfReleasedCore();
        }
    }

    private void ScheduleRepair()
    {
        _repair?.Cancel();
        var repair = new CancellationTokenSource();
        _repair = repair;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_repairDelay, repair.Token);
                lock (_gate)
                {
                    if (!repair.IsCancellationRequested)
                    {
                        RepairIfReleasedCore();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void RepairIfReleasedCore()
    {
        if (_isDown && !_isKeyDown(NativeMethods.VK_SPACE))
        {
            Clear();
        }
    }

    private void Clear()
    {
        _isDown = false;
        _repair?.Cancel();
        _repair = null;
    }
}
