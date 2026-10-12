using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    // ---------------------------------------------------------------------
    // Multi-selection surfaces: the index sheet and the slideshow. Both are
    // views over the same captured set the chevrons walk; the controller
    // owns the set, this window owns how it looks.
    // ---------------------------------------------------------------------

    internal bool IsGridViewOpen => _gridView is not null;

    internal bool IsSlideshowActive => _slideshowTimer?.IsEnabled == true;

    /// <summary>
    /// The controller hands over the captured selection set (null when
    /// browsing left it or the selection collapsed to one item).
    /// </summary>
    internal void SetMultiSelection(IReadOnlyList<string>? paths, int index)
    {
        var setChanged = !ReferenceEquals(_multiSelectionPaths, paths);
        _multiSelectionPaths = paths;
        _multiSelectionIndex = index;
        UpdateSelectionPosition();
        ShowOptionalLanes();

        if (paths is null)
        {
            CloseGridView();
            StopSlideshow(restoreSize: true);
            return;
        }

        if (setChanged)
        {
            // A different selection means a different sheet.
            CloseGridView();
            StopSlideshow(restoreSize: true);
        }
    }

    /// <summary>The preview moved within the set; sheet cursor and slideshow cadence follow.</summary>
    internal void SetMultiSelectionIndex(int index)
    {
        _multiSelectionIndex = index;
        UpdateSelectionPosition();
        _gridView?.SetCurrentIndex(index);
        if (IsSlideshowActive)
        {
            // Manual steps restart the beat so the next auto-advance gets a
            // full interval on screen.
            _slideshowTimer!.Stop();
            _slideshowTimer.Start();
        }
    }

    /// <summary>"2 of 14" beside the arrows, while a set is open.</summary>
    private void UpdateSelectionPosition()
    {
        if (_multiSelectionPaths is not { Count: > 1 } paths)
        {
            SelectionPositionText.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionPositionText.Text = $"{Math.Clamp(_multiSelectionIndex, 0, paths.Count - 1) + 1} of {paths.Count}";
        SelectionPositionText.Visibility = Visibility.Visible;
    }

    internal void ToggleGridView()
    {
        if (_gridView is not null)
        {
            CloseGridView();
            return;
        }

        if (_multiSelectionPaths is not { Count: > 1 } paths)
        {
            return;
        }

        StopSlideshow(restoreSize: false);
        var grid = new SelectionGridView(paths, _multiSelectionIndex);
        grid.ItemInvoked += index =>
        {
            CloseGridView();
            SelectionItemRequested?.Invoke(this, index);
        };
        ApplyScrollBarStyle(grid.Root);

        var host = (Grid)PreviewContent.Parent;
        Panel.SetZIndex(grid.Root, SheetLayer);
        host.Children.Add(grid.Root);
        ShowPreviewUnderSheet(false);
        Motion.Settle(grid.Root, 0);
        Motion.Fade(grid.Root, 1, TimeSpan.FromMilliseconds(120));

        _gridView = grid;
        GridButton.SetResourceReference(ForegroundProperty, "Theme.Accent");
        DiagnosticLog.Write($"grid view open ({paths.Count} items)");
    }

    internal void CloseGridView()
    {
        if (_gridView is not { } grid)
        {
            return;
        }

        _gridView = null;
        grid.Detach();
        ((Grid)PreviewContent.Parent).Children.Remove(grid.Root);
        ShowPreviewUnderSheet(true);
        GridButton.ClearValue(ForegroundProperty);
        DiagnosticLog.Write("grid view closed");
    }

    /// <summary>
    /// A browser's window paints over every WPF sibling, the sheet too, so
    /// the preview hides while the sheet is up. Hidden, not released: the
    /// page keeps its place and its playback, and content that lands
    /// meanwhile lands hidden.
    /// </summary>
    private void ShowPreviewUnderSheet(bool show)
    {
        var visibility = show ? Visibility.Visible : Visibility.Hidden;
        _front.Visibility = visibility;
        _back.Visibility = visibility;
    }

    /// <summary>Arrow keys walk the sheet while it is up.</summary>
    internal void MoveGridCursor(int deltaColumns, int deltaRows) => _gridView?.MoveCursor(deltaColumns, deltaRows);

    /// <summary>Enter commits the sheet's cursor to the preview.</summary>
    internal void CommitGridCursor() => _gridView?.Commit();

    private void GridButton_Click(object sender, RoutedEventArgs e) => ToggleGridView();

    private void PlayButton_Click(object sender, RoutedEventArgs e) => ToggleSlideshow();

    private void ToggleSlideshow()
    {
        if (IsSlideshowActive)
        {
            // The slideshow returns the size it borrowed; leaving the window
            // maximized after stopping latched every following preview to
            // one size.
            StopSlideshow(restoreSize: true);
        }
        else
        {
            StartSlideshow();
        }
    }

    private void StartSlideshow()
    {
        if (_multiSelectionPaths is not { Count: > 1 })
        {
            return;
        }

        CloseGridView();
        if (!IsStageMaximized)
        {
            _slideshowRestoresSize = true;
            ToggleMaximize();
        }

        _slideshowTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _slideshowTimer.Tick -= OnSlideshowTick;
        _slideshowTimer.Tick += OnSlideshowTick;
        _slideshowTimer.Start();
        PlayButton.Content = StopSlideshowGlyph;
        PlayButton.ToolTip = "Stop slideshow (Esc)";
        AutomationProperties.SetName(PlayButton, "Stop slideshow");
        DiagnosticLog.Write("slideshow started");
    }

    private void StopSlideshow(bool restoreSize)
    {
        var wasActive = IsSlideshowActive;
        _slideshowTimer?.Stop();
        PlayButton.Content = PlayGlyph;
        PlayButton.ToolTip = "Slideshow";
        AutomationProperties.SetName(PlayButton, "Slideshow");
        if (restoreSize && _slideshowRestoresSize && IsStageMaximized)
        {
            ToggleMaximize();
        }

        _slideshowRestoresSize = false;
        if (wasActive)
        {
            DiagnosticLog.Write("slideshow stopped");
        }
    }

    private void OnSlideshowTick(object? sender, EventArgs e) => SlideshowAdvanceRequested?.Invoke(this, EventArgs.Empty);
}
