using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpacePreview.Preview;

/// <summary>
/// Rotation state for the image currently on screen. The view rotates
/// instantly; the file catches up a beat later. The delay is write
/// coalescing, not intent detection: three quick clicks land as one write,
/// and closing or browsing away flushes whatever is pending. Rotating back
/// before the commit fires is a perfect undo.
/// </summary>
internal sealed class ImageRotationSession
{
    private static readonly TimeSpan CommitDelay = TimeSpan.FromSeconds(1);

    private BitmapSource _decoded;
    private int _baseOrientation;
    private int _quarterTurns;
    private bool _viewWasRotated;
    private DispatcherTimer? _commitTimer;
    private Task _saveChain = Task.CompletedTask;

    public string Path { get; }

    public RotationPersistence Persistence { get; }

    /// <summary>Raised on the UI thread whenever the displayed bitmap changes.</summary>
    public event Action? ViewChanged;

    /// <summary>
    /// Raised when a cached first frame is replaced by the full decode. The
    /// composition is the same, so viewers preserve their current zoom/pan.
    /// </summary>
    public event Action? QualityUpgraded;

    public ImageRotationSession(string path, BitmapSource decoded, int baseOrientation)
    {
        Path = path;
        _decoded = decoded;
        _baseOrientation = baseOrientation;
        Persistence = ImageRotationService.Classify(path);
    }

    public BitmapSource CurrentBitmap =>
        ImageOrientation.Apply(_decoded, ImageOrientation.RotateCw(_baseOrientation, _quarterTurns));

    /// <summary>Rotates the view a quarter turn. Positive is clockwise.</summary>
    public void Rotate(int direction)
    {
        _viewWasRotated = true;
        _quarterTurns = ((_quarterTurns + direction) % 4 + 4) % 4;
        ViewChanged?.Invoke();

        _commitTimer?.Stop();
        if (Persistence == RotationPersistence.ViewOnly)
        {
            return;
        }

        _commitTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = CommitDelay };
        _commitTimer.Tick -= OnCommitTimer;
        _commitTimer.Tick += OnCommitTimer;
        _commitTimer.Start();
    }

    /// <summary>
    /// Replaces a shell-cached first frame with the real decoded pixels. A
    /// rotation is user intent and wins the race: once touched, the session
    /// keeps its current composition rather than rebasing under the user.
    /// </summary>
    public bool TryUpgradeDecoded(BitmapSource decoded, int baseOrientation)
    {
        if (_viewWasRotated)
        {
            return false;
        }

        _decoded = decoded;
        _baseOrientation = baseOrientation;
        QualityUpgraded?.Invoke();
        return true;
    }

    private void OnCommitTimer(object? sender, EventArgs e) => Commit();

    /// <summary>
    /// Hands any pending turns to the disk writer. Safe to call at any time
    /// from the UI thread; the write itself runs serialized off-thread.
    /// </summary>
    public void Commit()
    {
        _commitTimer?.Stop();
        var turns = _quarterTurns;
        if (turns == 0 || Persistence == RotationPersistence.ViewOnly)
        {
            return;
        }

        // Rebase the view so it stays exactly as the user left it while the
        // file takes over carrying the rotation. A failed write leaves the
        // view rotated for this preview only; the log names it.
        _quarterTurns = 0;
        if (Persistence == RotationPersistence.ExifTag)
        {
            _baseOrientation = ImageOrientation.RotateCw(_baseOrientation, turns);
        }
        else
        {
            _decoded = ImageOrientation.Apply(_decoded, ImageOrientation.FromQuarterTurns(turns));
        }

        var path = Path;
        _saveChain = _saveChain.ContinueWith(
            _ => ImageRotationService.TrySaveQuarterTurns(path, turns),
            CancellationToken.None,
            TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default);
    }
}
