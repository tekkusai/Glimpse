using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpacePreview.Services;

namespace SpacePreview.Preview;

/// <summary>
/// Rotation state for the image currently on screen. The view rotates
/// instantly; the file catches up a beat later. The delay is write
/// coalescing, not intent detection: three quick clicks land as one write,
/// and closing or browsing away flushes whatever is pending. Rotating back
/// before the commit fires is a perfect undo. A write that does not land
/// turns the view back to what the file is and says so.
/// </summary>
internal sealed class ImageRotationSession
{
    private static readonly TimeSpan CommitDelay = TimeSpan.FromSeconds(1);

    // Rotations still on their way to disk, by file. One file's writes
    // land one after another, whichever preview asked, and a preview that
    // opens the same file waits for them, so it never shows the picture as
    // it was a moment before.
    private static readonly Dictionary<string, Task> SavesInFlight = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private BitmapSource _decoded;
    private BitmapSource? _current;
    private int _baseOrientation;

    // Quarter turns the view shows on top of the decoded pixels, and how
    // many of those the file has not been asked to take yet.
    private int _turns;
    private int _unsaved;
    private bool _committed;
    private DispatcherTimer? _commitTimer;

    public string Path { get; }

    public RotationPersistence Persistence { get; private set; }

    /// <summary>The preview has moved on or closed. Its last write may still be on its way.</summary>
    public bool HasLeft { get; private set; }

    /// <summary>
    /// The full decode of an image that opened on a first frame, running
    /// until it has landed here or been given up. A turn made in that time
    /// is written when it ends: see <see cref="TryUpgradeDecoded"/>.
    /// </summary>
    public Task FullDecode { get; set; } = Task.CompletedTask;

    /// <summary>Raised on the UI thread whenever the displayed bitmap changes.</summary>
    public event Action? ViewChanged;

    /// <summary>
    /// Raised when a cached first frame is replaced by the full decode. The
    /// composition is the same, so viewers preserve their current zoom/pan.
    /// </summary>
    public event Action? QualityUpgraded;

    /// <summary>
    /// Raised on the UI thread when a rotation did not reach the file. The
    /// view has already turned back, unless the file cannot carry a rotation
    /// at all: then the turn stays, for this preview only.
    /// </summary>
    public event Action<RotationSaveResult>? NotSaved;

    public ImageRotationSession(string path, BitmapSource decoded, int baseOrientation, RotationPersistence persistence)
    {
        Path = path;
        _decoded = decoded;
        _baseOrientation = baseOrientation;
        Persistence = persistence;
    }

    public BitmapSource CurrentBitmap =>
        _current ??= ImageOrientation.Apply(_decoded, ImageOrientation.RotateCw(_baseOrientation, _turns));

    /// <summary>Completes once no rotation of this file is still on its way to disk.</summary>
    public static Task WhenSaved(string path)
    {
        lock (SavesInFlight)
        {
            return SavesInFlight.GetValueOrDefault(System.IO.Path.GetFullPath(path), Task.CompletedTask);
        }
    }

    /// <summary>
    /// The app is ending: gives every rotation still on its way to disk up
    /// to <paramref name="bound"/> to land. One cut off mid-write leaves its
    /// working file beside the picture. False when a write was still running.
    /// </summary>
    public static bool WaitForSaves(TimeSpan bound)
    {
        Task[] saves;
        lock (SavesInFlight)
        {
            saves = [.. SavesInFlight.Values];
        }

        return Task.WaitAll(saves, bound);
    }

    /// <summary>
    /// Removes the working file a save left beside this picture when the
    /// app ended under it. A save of this file that is running now owns
    /// that name, and no save can begin while this looks.
    /// </summary>
    public static void RemoveStrandedTemp(string path)
    {
        if (!ImageRotationService.SavesThroughATemp(path))
        {
            return;
        }

        var key = System.IO.Path.GetFullPath(path);
        lock (SavesInFlight)
        {
            if (!SavesInFlight.ContainsKey(key) && ImageRotationService.DeleteTemp(ImageRotationService.TempPathFor(key)))
            {
                DiagnosticLog.Write($"rotate stranded temp removed beside {key}");
            }
        }
    }

    /// <summary>Rotates the view a quarter turn. Positive is clockwise.</summary>
    public void Rotate(int direction)
    {
        _turns = Quarter(_turns + direction);
        _current = null;
        ViewChanged?.Invoke();

        _commitTimer?.Stop();
        if (!ImageRotationService.Saves(Persistence))
        {
            return;
        }

        _unsaved = Quarter(_unsaved + direction);
        _commitTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = CommitDelay };
        _commitTimer.Tick -= OnCommitTimer;
        _commitTimer.Tick += OnCommitTimer;
        _commitTimer.Start();
    }

    /// <summary>
    /// Replaces a first frame with the full decode. Pending turns ride on
    /// top of the better pixels, so rotating early never freezes a stand-in
    /// on screen. Once a write has been handed to the disk, nothing says
    /// whether a decode read the file before the write or after it, so it
    /// is refused. While the preview is up that never happens: the write
    /// waits for <see cref="FullDecode"/>.
    /// </summary>
    public bool TryUpgradeDecoded(BitmapSource decoded, int baseOrientation)
    {
        if (_committed)
        {
            return false;
        }

        _decoded = decoded;
        _baseOrientation = baseOrientation;
        _current = null;
        QualityUpgraded?.Invoke();
        return true;
    }

    private async void OnCommitTimer(object? sender, EventArgs e)
    {
        _commitTimer!.Stop();

        // Written now, the turn would shut out the full decode, and the
        // picture would stay a first frame until it was reopened. However
        // the decode ends, the write follows it.
        await Task.WhenAny(FullDecode);

        // A turn made during the wait started the clock again, and its own
        // tick writes.
        if (!_commitTimer.IsEnabled)
        {
            Save();
        }
    }

    /// <summary>
    /// The preview is leaving this image: hands any pending turns to the
    /// disk writer. From here the session no longer moves a view, and a
    /// write that fails is one for a file no longer on screen. Safe to call
    /// at any time from the UI thread; the write itself runs off-thread.
    /// </summary>
    public void Commit()
    {
        HasLeft = true;
        Save();
    }

    private void Save()
    {
        _commitTimer?.Stop();
        var turns = _unsaved;
        if (turns == 0 || !ImageRotationService.Saves(Persistence))
        {
            return;
        }

        // The view stays exactly as the user left it while the file takes
        // over carrying the rotation.
        _unsaved = 0;
        _committed = true;

        var key = System.IO.Path.GetFullPath(Path);
        Task<RotationSaveResult> save;
        lock (SavesInFlight)
        {
            save = SavesInFlight.GetValueOrDefault(key, Task.CompletedTask).ContinueWith(
                _ => ImageRotationService.SaveQuarterTurns(key, turns),
                CancellationToken.None,
                TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default);
            SavesInFlight[key] = save;
        }

        save.ContinueWith(
            done =>
            {
                lock (SavesInFlight)
                {
                    if (SavesInFlight.GetValueOrDefault(key) == done)
                    {
                        SavesInFlight.Remove(key);
                    }
                }

                _dispatcher.InvokeAsync(() => OnSaved(turns, done.Result));
            },
            TaskScheduler.Default);
    }

    private void OnSaved(int turns, RotationSaveResult result)
    {
        if (result == RotationSaveResult.Saved)
        {
            return;
        }

        _commitTimer?.Stop();
        if (result == RotationSaveResult.CannotCarry)
        {
            // Found out only by trying. The file is view-only from here on,
            // and the turn the user made stays in the view, as it would
            // have had the header said so.
            Persistence = RotationPersistence.CannotCarry;
            _unsaved = 0;
        }
        else
        {
            // The file is as it was, so the view is again: these turns and
            // any made since come back out.
            _turns = Quarter(_turns - turns - _unsaved);
            _unsaved = 0;
            _current = null;
            if (!HasLeft)
            {
                ViewChanged?.Invoke();
            }
        }

        NotSaved?.Invoke(result);
    }

    private static int Quarter(int turns) => ((turns % 4) + 4) % 4;
}
