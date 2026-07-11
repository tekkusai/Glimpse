using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// The index sheet for a multi-selection: every selected item as a thumbnail
/// tile, one highlighted cursor, click or Enter to jump the preview there.
/// Thumbnails come from the shell's own cache, load only for tiles near the
/// viewport, and the whole sheet lives no longer than the preview it covers.
/// </summary>
internal sealed class SelectionGridView
{
    internal const int TileWidth = 176;
    internal const int TileHeight = 206;
    internal const int ThumbArea = 152;
    internal const int ThumbPixels = 160;
    internal const int MaxTiles = 512;
    internal const int ThumbCacheCap = 320;
    internal const int LookaheadRows = 2;
    internal const int ThumbnailWorkerLimit = 2;

    private readonly IReadOnlyList<string> _paths;
    private readonly Tile[] _tiles;
    private readonly WrapPanel _panel;
    private readonly ScrollViewer _scroll;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, BitmapSource> _thumbCache = [];
    private readonly LinkedList<int> _thumbOrder = [];
    private readonly HashSet<int> _pendingSet = [];
    private readonly LinkedList<int> _pendingQueue = [];
    private readonly SolidColorBrush _cursorBrush;
    private readonly SolidColorBrush _hoverBrush;
    private int _workersRunning;
    private int _cursorIndex;

    public FrameworkElement Root { get; }

    public event Action<int>? ItemInvoked;

    private sealed record Tile(Border Chrome, Image Image);

    public SelectionGridView(IReadOnlyList<string> paths, int currentIndex)
    {
        _paths = paths;
        var palette = Theme.Current;
        _cursorBrush = Theme.Brush(palette.Accent);
        _hoverBrush = Theme.Brush(palette.HoverBg);

        var tileCount = Math.Min(paths.Count, MaxTiles);
        _tiles = new Tile[tileCount];
        _panel = new WrapPanel
        {
            ItemWidth = TileWidth,
            ItemHeight = TileHeight,
            Margin = new Thickness(18, 16, 18, 16),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        for (var i = 0; i < tileCount; i++)
        {
            _panel.Children.Add(BuildTile(i, palette));
        }

        var stack = new StackPanel();
        stack.Children.Add(_panel);
        if (paths.Count > tileCount)
        {
            DiagnosticLog.Write($"grid capped at {tileCount} of {paths.Count} items");
            stack.Children.Add(new TextBlock
            {
                Text = $"Showing the first {tileCount} of {paths.Count} selected items.",
                FontSize = 12,
                Foreground = Theme.Brush(palette.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 22)
            });
        }

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = stack
        };
        _scroll.ScrollChanged += (_, _) => RequestVisibleThumbnails();
        _scroll.SizeChanged += (_, _) => RequestVisibleThumbnails();
        _scroll.Loaded += (_, _) =>
        {
            SetCursor(_cursorIndex, scrollIntoView: true);
            RequestVisibleThumbnails();
        };

        Root = new Border
        {
            Background = Theme.Brush(palette.CanvasBg),
            Child = _scroll
        };

        _cursorIndex = Math.Clamp(currentIndex, 0, Math.Max(0, tileCount - 1));
        if (_tiles.Length > 0)
        {
            _tiles[_cursorIndex].Chrome.BorderBrush = _cursorBrush;
        }
    }

    private Border BuildTile(int index, ThemePalette palette)
    {
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = ThumbArea,
            MaxHeight = ThumbArea,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var thumbHost = new Border
        {
            Height = ThumbArea + 8,
            Background = Theme.Brush(Color.FromArgb(palette.IsDark ? (byte)36 : (byte)80, palette.SurfaceBg.R, palette.SurfaceBg.G, palette.SurfaceBg.B)),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 7),
            Child = image
        };

        var caption = new TextBlock
        {
            Text = Path.GetFileName(_paths[index].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            FontSize = 11.5,
            Foreground = Theme.Brush(palette.TextSecondary),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 32,
            VerticalAlignment = VerticalAlignment.Top
        };

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(thumbHost, 0);
        Grid.SetRow(caption, 1);
        layout.Children.Add(thumbHost);
        layout.Children.Add(caption);

        var chrome = new Border
        {
            Margin = new Thickness(4),
            Padding = new Thickness(7),
            CornerRadius = new CornerRadius(9),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(2),
            Child = layout,
            Tag = index
        };
        chrome.MouseEnter += (_, _) =>
        {
            if (index != _cursorIndex)
            {
                chrome.Background = _hoverBrush;
            }
        };
        chrome.MouseLeave += (_, _) => chrome.Background = Brushes.Transparent;
        chrome.MouseLeftButtonUp += (_, e) =>
        {
            SetCursor(index, scrollIntoView: false);
            ItemInvoked?.Invoke(index);
            e.Handled = true;
        };

        _tiles[index] = new Tile(chrome, image);
        return chrome;
    }

    // -----------------------------------------------------------------
    // Cursor.
    // -----------------------------------------------------------------

    private int ColumnsPerRow()
    {
        var width = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth;
        width -= _panel.Margin.Left + _panel.Margin.Right;
        return Math.Max(1, (int)(width / TileWidth));
    }

    public void MoveCursor(int deltaColumns, int deltaRows)
    {
        if (_tiles.Length == 0)
        {
            return;
        }

        var columns = ColumnsPerRow();
        var target = _cursorIndex + deltaColumns + deltaRows * columns;
        SetCursor(Math.Clamp(target, 0, _tiles.Length - 1), scrollIntoView: true);
    }

    public void Commit() => ItemInvoked?.Invoke(_cursorIndex);

    /// <summary>The committed preview moved (external browse); the sheet follows.</summary>
    public void SetCurrentIndex(int index)
    {
        if (index >= 0 && index < _tiles.Length)
        {
            SetCursor(index, scrollIntoView: true);
        }
    }

    private void SetCursor(int index, bool scrollIntoView)
    {
        if (_tiles.Length == 0)
        {
            return;
        }

        if (_cursorIndex >= 0 && _cursorIndex < _tiles.Length)
        {
            _tiles[_cursorIndex].Chrome.BorderBrush = Brushes.Transparent;
        }

        _cursorIndex = index;
        var tile = _tiles[index];
        tile.Chrome.BorderBrush = _cursorBrush;
        tile.Chrome.Background = Brushes.Transparent;

        if (scrollIntoView)
        {
            var columns = ColumnsPerRow();
            var rowTop = _panel.Margin.Top + (index / columns) * (double)TileHeight;
            var rowBottom = rowTop + TileHeight;
            if (rowTop < _scroll.VerticalOffset)
            {
                _scroll.ScrollToVerticalOffset(rowTop - 8);
            }
            else if (rowBottom > _scroll.VerticalOffset + _scroll.ViewportHeight)
            {
                _scroll.ScrollToVerticalOffset(rowBottom - _scroll.ViewportHeight + 8);
            }
        }
    }

    // -----------------------------------------------------------------
    // Thumbnails: shell cache, viewport-driven, bounded.
    // -----------------------------------------------------------------

    private void RequestVisibleThumbnails()
    {
        if (_lifetime.IsCancellationRequested || _tiles.Length == 0)
        {
            return;
        }

        var columns = ColumnsPerRow();
        var viewportTop = _scroll.VerticalOffset - _panel.Margin.Top;
        var viewportBottom = viewportTop + Math.Max(_scroll.ViewportHeight, TileHeight);
        var firstRow = Math.Max(0, (int)(viewportTop / TileHeight) - LookaheadRows);
        var lastRow = (int)(viewportBottom / TileHeight) + LookaheadRows;
        var first = Math.Clamp(firstRow * columns, 0, _tiles.Length - 1);
        var last = Math.Clamp((lastRow + 1) * columns - 1, 0, _tiles.Length - 1);

        for (var i = first; i <= last; i++)
        {
            if (_thumbCache.ContainsKey(i) || !_pendingSet.Add(i))
            {
                continue;
            }

            _pendingQueue.AddLast(i);
        }

        while (_workersRunning < ThumbnailWorkerLimit && _pendingQueue.Count > 0)
        {
            _workersRunning++;
            _ = PumpThumbnailsAsync();
        }
    }

    private async Task PumpThumbnailsAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && _pendingQueue.Count > 0)
            {
                var index = _pendingQueue.First!.Value;
                _pendingQueue.RemoveFirst();
                _pendingSet.Remove(index);
                if (_thumbCache.ContainsKey(index))
                {
                    continue;
                }

                var path = _paths[index];
                var shellImage = await Task.Run(
                    () => ShellThumbnailProvider.TryGetThumbnail(path, ThumbPixels),
                    _lifetime.Token);
                if (_lifetime.IsCancellationRequested)
                {
                    return;
                }

                if (shellImage is null)
                {
                    continue;
                }

                StoreThumbnail(index, shellImage);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"grid thumbnail pump failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _workersRunning--;
        }
    }

    private void StoreThumbnail(int index, ShellImage shellImage)
    {
        _thumbCache[index] = shellImage.Bitmap;
        _thumbOrder.AddLast(index);
        while (_thumbOrder.Count > ThumbCacheCap)
        {
            var evicted = _thumbOrder.First!.Value;
            _thumbOrder.RemoveFirst();
            _thumbCache.Remove(evicted);
            _tiles[evicted].Image.Source = null;
        }

        var image = _tiles[index].Image;
        image.Source = shellImage.Bitmap;

        // Icons blur when scaled past native size; thumbnails fill the cell.
        image.StretchDirection = shellImage.IsThumbnail ? StretchDirection.Both : StretchDirection.DownOnly;
    }

    /// <summary>Stops loading and drops every decoded thumbnail.</summary>
    public void Detach()
    {
        _lifetime.Cancel();
        _pendingQueue.Clear();
        _pendingSet.Clear();
        _thumbCache.Clear();
        _thumbOrder.Clear();
        foreach (var tile in _tiles)
        {
            tile.Image.Source = null;
        }
    }
}
