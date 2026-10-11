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
    // Fill and sign. The header's Sign pill comes with every native PDF;
    // sign mode swaps in our own page surface (the embedded viewer is a
    // closed world), and saving never touches the original except by the
    // user's explicit second choice.
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
        return _fillSignSurface?.TryLeave() != false;
    }

    private async Task EnterFillSignModeAsync()
    {
        if (_fillSignPath is not { } path || _fillSignSurface is not null || !IsCurrentContentPath(path))
        {
            return;
        }

        var version = _loadVersion;
        // Asked of the viewer now: a document scrolled by wheel alone has a
        // stale rail page.
        var (currentPage, totalPages) = await ReadPdfPositionAsync();
        if (version != _loadVersion || !IsCurrentContentPath(path) || _fillSignSurface is not null)
        {
            return;
        }

        int? viewerPages = totalPages > 0 ? totalPages : null;
        var pageIndex = Math.Max(0, currentPage - 1);
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

        var surface = new FillSignSurface(pageIndex, viewerPages ?? pageIndex + 1, page);
        surface.ExitRequested += ExitFillSignMode;
        surface.SaveRequested += (placements, overwrite) => _ = SaveFillSignAsync(surface, placements, overwrite);
        surface.PageChangeRequested += target => _ = ShowFillSignPageAsync(surface, target);

        _fillSignParkedContent = current;
        _fillSignSurface = surface;
        PreviewContent.Content = surface;
        SyncHeaderTools();
        DiagnosticLog.Write($"fill-sign entered page={pageIndex + 1}");

        // The writer reads the file exactly as a save would. A PDF it cannot
        // update says why before any work goes onto it, and the page count
        // is the writer's own, not whatever the rail had loaded so far.
        var check = await Task.Run(() => PdfFillSignWriter.Preflight(path));
        if (!ReferenceEquals(_fillSignSurface, surface))
        {
            return;
        }

        if (check.Problem is null && viewerPages is { } shown && shown != check.PageCount)
        {
            // Two readers disagree on the pages: ink could land on the wrong one.
            check = new PdfSignCheck(check.PageCount, PdfFillSignWriter.UnsupportedStructure);
        }

        if (check.Problem is not null)
        {
            DiagnosticLog.Write($"fill-sign unavailable: {check.Problem}");
        }

        surface.ApplyCheck(check);
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
    }

    private void ExitFillSignMode()
    {
        if (_fillSignSurface is null)
        {
            return;
        }

        _fillSignSurface = null;
        if (_fillSignParkedContent is not { } parked)
        {
            // A failed replace already released the viewer to free the file:
            // the document comes back from disk instead of a dead surface.
            DiagnosticLog.Write("fill-sign exited, reloading the document");
            if (_fillSignPath is { } path && !ShowPreview(path, Hwnd))
            {
                PresentationFailed?.Invoke(this, LastPresentationFailureReason);
            }

            return;
        }

        PreviewContent.Content = parked;
        parked.Visibility = Visibility.Visible;
        parked.UpdateLayout();
        _fillSignParkedContent = null;
        SyncHeaderTools();
        DiagnosticLog.Write("fill-sign exited");
        var deferredFailure = _deferredWebFailure;
        _deferredWebFailure = null;
        deferredFailure?.Invoke();
    }

    private async Task SaveFillSignAsync(FillSignSurface surface, IReadOnlyList<SignPlacement> placements, bool overwrite)
    {
        if (_fillSignPath is not { } path
            || !ReferenceEquals(_fillSignSurface, surface)
            || !IsCurrentContentPath(path)
            || placements.Count == 0)
        {
            return;
        }

        var version = _loadVersion;
        bool StillOwnsSurface() =>
            version == _loadVersion
            && ReferenceEquals(_fillSignSurface, surface)
            && IsCurrentContentPath(path);

        surface.SetSaving(true);
        var finalPath = overwrite ? path : NextSignedCopyPath(path);
        try
        {
            if (overwrite)
            {
                var temp = SigningTempPath(path);
                try
                {
                    await Task.Run(() => WriteHiddenSignedCopy(path, temp, placements));

                    // The embedded viewer holds the original open; release
                    // it and give Chromium a beat to let go before the swap.
                    // The placements survive any failure: the surface stays.
                    if (StillOwnsSurface())
                    {
                        _fillSignParkedContent = null;
                        _currentDisposable?.Dispose();
                        _currentDisposable = null;
                    }

                    await ReplaceWithRetriesAsync(temp, path);
                }
                catch
                {
                    CleanUpSigningTemp(temp, path);
                    throw;
                }
            }
            else
            {
                await Task.Run(() => PdfFillSignWriter.WriteSignedCopy(path, finalPath, placements));
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"fill-sign save failed {path}: {ex.GetType().Name}: {ex.Message}");
            if (StillOwnsSurface())
            {
                surface.ShowSaveFailure(SaveFailureReason(ex));
            }

            return;
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
        SyncHeaderTools();

        // The saved file is the honest outcome: show it.
        if (!ShowPreview(finalPath, Hwnd))
        {
            PresentationFailed?.Invoke(this, LastPresentationFailureReason);
        }
    }

    /// <summary>What a failed save says in place of the choices it brings back.</summary>
    internal static string SaveFailureReason(Exception ex) => ex switch
    {
        // The writer's own refusals are already user-facing sentences.
        InvalidOperationException => ex.Message,
        UnauthorizedAccessException => "The file or its folder is read-only. Save a signed copy somewhere else.",
        IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) } =>
            "The file is open in another app. Close it there and try again.",
        IOException { HResult: unchecked((int)0x80070070) } => "The disk is full. Nothing was saved.",
        _ => "The signed file couldn't be saved. Nothing was changed."
    };

    /// <summary>
    /// One predictable, hidden file beside the original: a crash leaves at
    /// most one behind, and the next save reuses it.
    /// </summary>
    internal static string SigningTempPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "." + Path.GetFileName(path) + ".signing");

    private static void WriteHiddenSignedCopy(string path, string temp, IReadOnlyList<SignPlacement> placements)
    {
        if (File.Exists(temp))
        {
            File.SetAttributes(temp, FileAttributes.Normal);
            File.Delete(temp);
        }

        PdfFillSignWriter.WriteSignedCopy(path, temp, placements);
        File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.Hidden);
    }

    /// <summary>
    /// After a failed replace the temporary file normally goes. ReplaceFile
    /// can fail after removing the original, though, and then the temporary
    /// file is the only copy of the document: it takes the original's name.
    /// </summary>
    internal static void CleanUpSigningTemp(string temp, string original)
    {
        try
        {
            if (!File.Exists(temp))
            {
                return;
            }

            File.SetAttributes(temp, File.GetAttributes(temp) & ~FileAttributes.Hidden);
            if (File.Exists(original))
            {
                File.Delete(temp);
            }
            else
            {
                File.Move(temp, original);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"fill-sign temp cleanup failed: {ex.GetType().Name}: {ex.Message}");
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
