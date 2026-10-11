using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// Debounced folder-row thumbnails. The row never owns its own window; it only
/// asks the folder card to swap the large artwork while the pointer rests.
/// </summary>
internal sealed class FolderHoverThumbnail
{
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(80);
    private const int ThumbPixels = 256;
    private const int CacheCap = 48;
    private const int WarmAhead = 2;

    private readonly Action<bool, ShellImage?> _show;
    private readonly int _folderIconPixels;
    private readonly int _fileIconPixels;
    private readonly DispatcherTimer _delay;
    private readonly DispatcherTimer _hideDelay;
    private readonly Dictionary<string, ShellImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _cacheOrder = [];
    private readonly Queue<string> _warmQueue = [];
    private readonly HashSet<string> _warmQueued = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _warmPaths = [];
    private UIElement? _pendingAnchor;
    private string? _pendingPath;
    private bool _pendingIsDirectory;
    private bool _hoverReady;
    private string? _fetchingPath;
    private int _generation;

    /// <summary>
    /// Icon sizes are device pixels for the card's own icon slots, so a
    /// hovered icon is never stretched past what the shell drew.
    /// </summary>
    public FolderHoverThumbnail(Action<bool, ShellImage?> show, int folderIconPixels, int fileIconPixels)
    {
        _show = show;
        _folderIconPixels = Math.Max(1, folderIconPixels);
        _fileIconPixels = Math.Max(1, fileIconPixels);

        _delay = new DispatcherTimer(DispatcherPriority.Background) { Interval = HoverDelay };
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            if (_pendingPath is null)
            {
                Hide();
                return;
            }

            _hoverReady = true;
            Pump();
        };

        _hideDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = HideDelay };
        _hideDelay.Tick += (_, _) =>
        {
            _hideDelay.Stop();
            if (_pendingPath is null)
            {
                Hide();
            }
        };
    }

    public void OnRowEnter(UIElement anchor, string path, bool isDirectory)
    {
        _pendingAnchor = anchor;
        _pendingPath = path;
        _pendingIsDirectory = isDirectory;
        _hoverReady = false;
        _hideDelay.Stop();

        QueueNeighbors(path);

        // A cached answer shows without the wait; the debounce exists to
        // keep scrubbing down the list from churning the decoder.
        if (_cache.TryGetValue(path, out var known))
        {
            _delay.Stop();
            Show(isDirectory, known);
            return;
        }

        _delay.Stop();
        _delay.Start();
    }

    public void OnRowLeave(UIElement anchor)
    {
        if (!ReferenceEquals(_pendingAnchor, anchor))
        {
            return;
        }

        _pendingAnchor = null;
        _pendingPath = null;
        _hoverReady = false;
        _delay.Stop();
        _hideDelay.Stop();
        _hideDelay.Start();
        Pump();
    }

    public void SetWarmPaths(IReadOnlyList<string> paths)
    {
        _warmPaths = paths;
        QueueWarm(paths.Take(WarmAhead));
        Pump();
    }

    /// <summary>Content changed hands: drop everything, including the cache.</summary>
    public void Reset()
    {
        _generation++;
        _pendingAnchor = null;
        _pendingPath = null;
        _fetchingPath = null;
        _hoverReady = false;
        _delay.Stop();
        _hideDelay.Stop();
        Hide();
        _cache.Clear();
        _cacheOrder.Clear();
        _warmPaths = [];
        _warmQueue.Clear();
        _warmQueued.Clear();
    }

    private void Pump()
    {
        if (_fetchingPath is not null)
        {
            return;
        }

        if (_hoverReady && _pendingPath is { } hovered && _pendingAnchor is not null)
        {
            StartFetch(hovered, _pendingIsDirectory);
            return;
        }

        // Do not begin speculative work while the pointer's debounce is
        // counting down. If it settles, that row owns the next Shell call.
        if (_pendingPath is not null)
        {
            return;
        }

        while (_warmQueue.TryDequeue(out var path))
        {
            _warmQueued.Remove(path);
            if (!_cache.ContainsKey(path))
            {
                StartFetch(path, isDirectory: true);
                return;
            }
        }
    }

    private async void StartFetch(string path, bool isDirectory)
    {
        var generation = _generation;
        _fetchingPath = path;
        var started = Stopwatch.StartNew();
        try
        {
            var shellImage = await Task.Run(() =>
            {
                if (!isDirectory)
                {
                    return ShellThumbnailProvider.TryGetThumbnail(path, ThumbPixels, _fileIconPixels);
                }

                // Preserve real folder artwork. Only its icon fallbacks share
                // the resting card's source size, preventing a hover nudge.
                return ShellThumbnailProvider.NormalizeFolderImage(
                    path,
                    ShellThumbnailProvider.TryGetThumbnail(path, ThumbPixels, _folderIconPixels),
                    _folderIconPixels);
            });
            if (generation != _generation)
            {
                return;
            }

            StoreInCache(path, shellImage);
            if (_hoverReady
                && string.Equals(_pendingPath, path, StringComparison.OrdinalIgnoreCase)
                && _pendingAnchor is not null)
            {
                Show(isDirectory, shellImage);
                _hoverReady = false;
            }

            if (started.ElapsedMilliseconds >= 400)
            {
                DiagnosticLog.Write($"slow hover thumbnail {started.ElapsedMilliseconds}ms {path}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"hover thumbnail failed {path}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _fetchingPath = null;

            if (generation == _generation)
            {
                Pump();
            }
        }
    }

    private void QueueNeighbors(string path)
    {
        for (var index = 0; index < _warmPaths.Count; index++)
        {
            if (string.Equals(_warmPaths[index], path, StringComparison.OrdinalIgnoreCase))
            {
                QueueWarm(_warmPaths.Skip(index + 1).Take(WarmAhead));
                return;
            }
        }
    }

    private void QueueWarm(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!_cache.ContainsKey(path)
                && !string.Equals(_fetchingPath, path, StringComparison.OrdinalIgnoreCase)
                && _warmQueued.Add(path))
            {
                _warmQueue.Enqueue(path);
            }
        }
    }

    private void StoreInCache(string path, ShellImage? shellImage)
    {
        if (_cache.ContainsKey(path))
        {
            return;
        }

        _cache[path] = shellImage;
        _cacheOrder.AddLast(path);
        while (_cacheOrder.Count > CacheCap)
        {
            var evicted = _cacheOrder.First!.Value;
            _cacheOrder.RemoveFirst();
            _cache.Remove(evicted);
        }
    }

    private void Show(bool isDirectory, ShellImage? shellImage)
    {
        if (shellImage is null)
        {
            Hide();
            return;
        }

        _show(isDirectory, shellImage);
    }

    private void Hide()
    {
        _show(false, null);
    }
}
