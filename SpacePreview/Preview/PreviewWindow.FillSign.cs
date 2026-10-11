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

    // The session's draft, and whether the loaded PDF has one waiting.
    private SignDraftKeeper? _signDraft;
    private bool _hasSignDraft;

    /// <summary>Sign mode's page render: a pdfium helper, never this process. Tests stand in for it.</summary>
    internal Func<string, int, Task<PdfPreviewPage?>> RenderSignPage { get; set; } =
        (path, index) => PdfiumHelper.TryRenderPageAsync(path, index, CancellationToken.None);

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

    /// <summary>
    /// Looks for work kept from last time on the PDF that just loaded, off
    /// the UI thread, and lets the Sign pill offer to resume it.
    /// </summary>
    private async Task FindSignDraftAsync(string path, long version)
    {
        var (_, draft) = await Task.Run(() => SignDrafts.Load(path));
        if (version == _loadVersion && _fillSignPath == path && _fillSignSurface is null)
        {
            _hasSignDraft = draft is not null;
            SyncHeaderTools();
        }
    }

    private async Task EnterFillSignModeAsync()
    {
        if (_fillSignPath is not { } path || _fillSignSurface is not null || !IsCurrentContentPath(path))
        {
            return;
        }

        var version = _loadVersion;
        // Read again rather than trusted from the load: the file, or another
        // window's work on it, may have moved on since. The writer's check
        // starts now too, so kept work comes back with the page, not after.
        var draftLoad = Task.Run(() => SignDrafts.Load(path));
        var preflight = Task.Run(() => PdfFillSignWriter.Preflight(path));
        // Asked of the viewer now: a document scrolled by wheel alone has a
        // stale rail page.
        var (currentPage, totalPages) = await ReadPdfPositionAsync();
        var (file, draft) = await draftLoad;
        if (version != _loadVersion || !IsCurrentContentPath(path) || _fillSignSurface is not null)
        {
            return;
        }

        int? viewerPages = totalPages > 0 ? totalPages : null;
        var pageIndex = draft?.PageIndex ?? Math.Max(0, currentPage - 1);
        var pages = new SignPageCache(index => RenderSignPage(path, index), viewerPages ?? pageIndex + 1);
        var page = await pages.Get(pageIndex);
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
        // Kept only once the check passes: a draft of work this file cannot
        // save would offer to resume it forever.
        SignDraftKeeper? keeper = null;
        surface.WorkChanged += () => keeper?.Update(surface.PageIndex, surface.Placements);
        surface.ExitRequested += ExitFillSignMode;
        surface.SaveRequested += (placements, overwrite) => _ = SaveFillSignAsync(surface, keeper, placements, overwrite);
        surface.PageChangeRequested += target => _ = ShowFillSignPageAsync(surface, pages, target);

        _fillSignParkedContent = current;
        _fillSignSurface = surface;
        _hasSignDraft = false;
        PreviewContent.Content = surface;
        SyncHeaderTools();
        DiagnosticLog.Write($"fill-sign entered page={pageIndex + 1}");

        // The writer reads the file exactly as a save would. A PDF it cannot
        // update says why before any work goes onto it, and the page count
        // is the writer's own, not whatever the rail had loaded so far.
        var check = await preflight;
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
            // A problem never ends a draft: drafts exist only for files that
            // passed this check, and one waits for its file to read again.
            DiagnosticLog.Write($"fill-sign unavailable: {check.Problem}");
            surface.ApplyCheck(check);
            return;
        }

        if (draft is not null)
        {
            surface.Restore(draft.Placements);
            DiagnosticLog.Write($"fill-sign restored placements={draft.Placements.Count}");
        }

        if (file is { } stamp)
        {
            keeper = new SignDraftKeeper(path, stamp, surface.PageIndex, surface.Placements);
            _signDraft = keeper;
        }

        surface.ApplyCheck(check);
        pages.PageCount = check.PageCount;
        pages.KeepNeighbors(pageIndex);
    }

    private async Task ShowFillSignPageAsync(FillSignSurface surface, SignPageCache pages, int pageIndex)
    {
        if (_fillSignPath is not { } path || !IsCurrentContentPath(path) || !ReferenceEquals(_fillSignSurface, surface))
        {
            return;
        }

        var page = await pages.Get(pageIndex);
        if (page is null || !IsCurrentContentPath(path) || !ReferenceEquals(_fillSignSurface, surface))
        {
            pages.Forget(pageIndex);
            DiagnosticLog.Write($"fill-sign page render failed page={pageIndex + 1}");
            return;
        }

        surface.ShowPage(pageIndex, page);
        pages.KeepNeighbors(pageIndex);
    }

    /// <summary>
    /// Sign-mode pages, each rendered once in a helper. The pages either side
    /// of the one on screen render ahead, so turning a page is instant
    /// instead of a helper's start; pages further away are let go. UI thread
    /// only.
    /// </summary>
    private sealed class SignPageCache(Func<int, Task<PdfPreviewPage?>> render, int pageCount)
    {
        private readonly Dictionary<int, Task<PdfPreviewPage?>> _pages = [];

        public int PageCount { get; set; } = pageCount;

        public Task<PdfPreviewPage?> Get(int index)
        {
            if (!_pages.TryGetValue(index, out var page))
            {
                page = render(index);
                _pages[index] = page;
            }

            return page;
        }

        public void Forget(int index) => _pages.Remove(index);

        public void KeepNeighbors(int index)
        {
            foreach (var far in _pages.Keys.Where(key => Math.Abs(key - index) > 1).ToList())
            {
                _pages.Remove(far);
            }

            if (index + 1 < PageCount)
            {
                _ = Get(index + 1);
            }

            if (index > 0)
            {
                _ = Get(index - 1);
            }
        }
    }

    private void ExitFillSignMode()
    {
        if (_fillSignSurface is null)
        {
            return;
        }

        // The mode ends only with no work, work this file can never save,
        // or Discard: nothing of this session is left to keep.
        _signDraft?.Discard();
        _signDraft = null;
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

        // A draft this session could not take up, on a file that could not
        // be read, is still there: the pill offers it again.
        if (_fillSignPath is { } pdf)
        {
            _ = FindSignDraftAsync(pdf, _loadVersion);
        }

        var deferredFailure = _deferredWebFailure;
        _deferredWebFailure = null;
        deferredFailure?.Invoke();
    }

    private async Task SaveFillSignAsync(FillSignSurface surface, SignDraftKeeper? keeper, IReadOnlyList<SignPlacement> placements, bool overwrite)
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
        keeper?.Discard();
        if (!StillOwnsSurface())
        {
            // Saving is file work and still completes; navigation is UI
            // work and belongs only to the preview that started it.
            DiagnosticLog.Write("fill-sign completion left current preview unchanged");
            return;
        }

        _fillSignSurface = null;
        _fillSignParkedContent = null;
        _signDraft = null;
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
