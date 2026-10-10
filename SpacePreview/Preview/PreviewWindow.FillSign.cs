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
    // Fill and sign. The pen pill appears only after the user engages a
    // native PDF; sign mode swaps in our own page surface (the embedded
    // viewer is a closed world), and saving never touches the original
    // except by the user's explicit second choice.
    // ---------------------------------------------------------------------

    internal bool IsFillSignModeActive => _fillSignSurface is not null;

    internal void RequestFillSignEscape() => _fillSignSurface?.HandleEscape();

    /// <summary>
    /// Every way out of sign mode asks first. With placed work the save
    /// choice comes up and the exit waits for the user's answer; an empty
    /// session lets it go ahead.
    /// </summary>
    internal bool TryLeaveFillSign()
    {
        if (_fillSignSurface is not { HasWork: true } surface)
        {
            return true;
        }

        surface.ShowSaveChoice();
        return false;
    }

    private void UpdateFillSignPill()
    {
        if (_fillSignPill is null)
        {
            _fillSignPill = BuildFillSignPill();
            ContentToolsSlot.Children.Add(_fillSignPill);
        }

        // Visible from the moment a native PDF loads: discoverability beat
        // the engagement gate in tester hands, and the reserved slot means
        // no reflow either way.
        _fillSignPill.Visibility =
            !_isFileDialogMode && _fillSignPath is not null && _fillSignSurface is null
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateContentToolsSlotVisibility();
    }

    private Border BuildFillSignPill()
    {
        var palette = Theme.Current;
        var pill = new Border
        {
            Height = 27,
            Padding = new Thickness(11, 0, 12, 0),
            CornerRadius = Metrics.PillCorner,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Fill and sign",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "",
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Theme.Brush(palette.TextSecondary)
                    },
                    new TextBlock
                    {
                        Text = "Sign",
                        FontSize = Metrics.FontLabel,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Theme.Brush(palette.TextPrimary)
                    }
                }
            }
        };
        pill.MouseEnter += (_, _) => pill.Background = Theme.Brush(palette.HoverBg);
        pill.MouseLeave += (_, _) => pill.Background = Theme.Brush(palette.RaisedBg);
        pill.MouseLeftButtonDown += (_, e) =>
        {
            pill.Background = Theme.Brush(palette.PressedBg);
            e.Handled = true;
        };
        pill.MouseLeftButtonUp += async (_, e) =>
        {
            pill.Background = Theme.Brush(palette.RaisedBg);
            e.Handled = true;
            await EnterFillSignModeAsync();
        };
        return pill;
    }

    private async Task EnterFillSignModeAsync()
    {
        if (_fillSignPath is not { } path || _fillSignSurface is not null || !IsCurrentContentPath(path))
        {
            return;
        }

        var version = _loadVersion;
        var pageIndex = Math.Max(0, (_activePdfRail?.CurrentPage ?? 1) - 1);
        var totalPages = _activePdfRail?.Document?.TotalPages ?? pageIndex + 1;
        var page = await Task.Run(() => PdfPreviewRenderer.TryRenderPage(path, pageIndex, CancellationToken.None));
        if (page is null)
        {
            if (version == _loadVersion && IsCurrentContentPath(path) && _fillSignSurface is null)
            {
                DiagnosticLog.Write($"fill-sign entry render failed page={pageIndex + 1}");
            }

            return;
        }

        if (version != _loadVersion || !IsCurrentContentPath(path) || _fillSignSurface is not null
            || PreviewContent.Content is not FrameworkElement current)
        {
            return;
        }

        var surface = new FillSignSurface(path, pageIndex, Math.Max(totalPages, pageIndex + 1), page);
        surface.ExitRequested += ExitFillSignMode;
        surface.SaveRequested += OnFillSignSaveRequested;
        surface.PageChangeRequested += target => _ = ShowFillSignPageAsync(surface, target);

        _fillSignParkedContent = current;
        _fillSignSurface = surface;
        PreviewContent.Content = surface;
        UpdateFillSignPill();
        DiagnosticLog.Write($"fill-sign entered page={pageIndex + 1}");
    }

    private async Task ShowFillSignPageAsync(FillSignSurface surface, int pageIndex)
    {
        if (_fillSignPath is not { } path || !IsCurrentContentPath(path) || !ReferenceEquals(_fillSignSurface, surface))
        {
            return;
        }

        var page = await Task.Run(() => PdfPreviewRenderer.TryRenderPage(path, pageIndex, CancellationToken.None));
        if (page is null || !IsCurrentContentPath(path) || !ReferenceEquals(_fillSignSurface, surface))
        {
            DiagnosticLog.Write($"fill-sign page render failed page={pageIndex + 1}");
            return;
        }

        surface.ShowPage(pageIndex, page);
        DiagnosticLog.Write($"fill-sign page -> {pageIndex + 1}");
    }

    private void ExitFillSignMode()
    {
        if (_fillSignSurface is null)
        {
            return;
        }

        _fillSignSurface = null;
        if (_fillSignParkedContent is { } parked)
        {
            PreviewContent.Content = parked;
            parked.Visibility = Visibility.Visible;
            parked.UpdateLayout();
            _fillSignParkedContent = null;
        }

        UpdateFillSignPill();
        DiagnosticLog.Write("fill-sign exited");
        var deferredFailure = _deferredWebFailure;
        _deferredWebFailure = null;
        deferredFailure?.Invoke();
    }

    private async void OnFillSignSaveRequested(SignSession session, bool overwrite)
    {
        if (_fillSignPath is not { } path
            || _fillSignSurface is not { } surface
            || !IsCurrentContentPath(path)
            || !session.HasWork)
        {
            return;
        }

        var version = _loadVersion;
        bool StillOwnsSurface() =>
            version == _loadVersion
            && ReferenceEquals(_fillSignSurface, surface)
            && IsCurrentContentPath(path);

        surface.SetSaving(true);
        try
        {
            var placements = BuildWriterPlacements(session);
            string finalPath;
            if (overwrite)
            {
                var signedPath = await Task.Run(() => PdfFillSignWriter.SignPdf(path, placements));

                // The embedded viewer holds the original open; release it
                // and give Chromium a beat to let go before the swap. The
                // user's placements survive any failure - the surface stays.
                if (StillOwnsSurface())
                {
                    _fillSignParkedContent = null;
                    _currentDisposable?.Dispose();
                    _currentDisposable = null;
                }

                await ReplaceWithRetriesAsync(signedPath, path);
                finalPath = path;
            }
            else
            {
                finalPath = NextSignedCopyPath(path);
                await Task.Run(() => PdfFillSignWriter.WriteSignedCopy(path, finalPath, placements));
            }

            DiagnosticLog.Write($"fill-sign saved {(overwrite ? "in place" : "as copy")} placements={placements.Count}");
            if (!StillOwnsSurface())
            {
                // Saving is file work and still completes; navigation is UI
                // work and belongs only to the preview that started it.
                DiagnosticLog.Write("fill-sign completion left current preview unchanged");
                return;
            }

            _fillSignSurface = null;
            _fillSignParkedContent = null;
            UpdateFillSignPill();

            // The saved file is the honest outcome: show it.
            if (!ShowPreview(finalPath, Hwnd))
            {
                PresentationFailed?.Invoke(this, LastPresentationFailureReason);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"fill-sign save failed {path}: {ex.GetType().Name}: {ex.Message}");
            surface.SetSaving(false);
        }
    }

    private static async Task ReplaceWithRetriesAsync(string signedPath, string destinationPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Replace(signedPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) when (attempt < 12)
            {
                await Task.Delay(250);
            }
        }
    }

    private static List<PdfFillSignPlacement> BuildWriterPlacements(SignSession session)
    {
        var placements = new List<PdfFillSignPlacement>(session.Placements.Count);
        foreach (var item in session.Placements)
        {
            var bounds = new PdfNormalizedRect(item.X, item.Y, item.Width, item.Height);
            // Placements carry their pixels from placement time; nothing at
            // save time reaches back into the store.
            placements.Add(item.Kind switch
            {
                SignPlacementKind.Date => new PdfFillSignPlacement(
                    item.PageIndex + 1, PdfFillSignPlacementKind.Date, bounds, Text: item.Text, FontSize: 0),
                SignPlacementKind.Stamp => new PdfFillSignPlacement(
                    item.PageIndex + 1, PdfFillSignPlacementKind.Stamp, bounds, PngBytes: item.ImagePng),
                _ => new PdfFillSignPlacement(
                    item.PageIndex + 1, PdfFillSignPlacementKind.Signature, bounds, PngBytes: item.ImagePng)
            });
        }

        return placements;
    }

    private static string NextSignedCopyPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var candidate = Path.Combine(directory, $"{name} (signed){extension}");
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, $"{name} (signed {i}){extension}");
        }

        return candidate;
    }
}
