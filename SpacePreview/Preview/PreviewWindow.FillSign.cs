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

    // ERROR_FILE_EXISTS: another save took the copy's name first.
    private const int FileExists = unchecked((int)0x80070050);

    // Every window's sign session, by the file it signs.
    private static readonly Dictionary<string, SignSession> SignSessions = [];

    // The session's draft, and whether the loaded PDF has one waiting.
    private SignDraftKeeper? _signDraft;
    private bool _hasSignDraft;
    private SignSession? _signSession;
    private long _signPageRequest;

    /// <summary>
    /// The Sign pill was pressed and its page is not up yet. The header
    /// holds the pill off for as long, and the window shows the working
    /// cursor: the press was taken.
    /// </summary>
    internal bool IsEnteringFillSign { get; private set; }

    /// <summary>What sign mode says when the page it opens on cannot be drawn.</summary>
    internal const string SignPageUnavailable = "This page couldn't be opened for signing. Try again, or open the PDF in another app to sign it.";

    /// <summary>Sign mode's page render: a pdfium helper, never this process. Tests stand in for it.</summary>
    internal Func<string, int, CancellationToken, Task<PdfPreviewPage?>> RenderSignPage { get; set; } =
        PdfiumHelper.TryRenderPageAsync;

    /// <summary>The writer's check before any work goes on; tests hold it to open the gap it leaves.</summary>
    internal Func<string, PdfSignCheck> CheckSignable { get; set; } = PdfFillSignWriter.Preflight;

    /// <summary>The writer itself; tests hold it to open the gaps around a save.</summary>
    internal Action<string, string, IReadOnlyList<SignPlacement>> WriteSignedPdf { get; set; } = PdfFillSignWriter.WriteSignedCopy;

    /// <summary>
    /// Where the signed copy goes when the PDF's own folder cannot take a
    /// new file, or null when the user names no place. A dialog, unless a
    /// test stands in for it.
    /// </summary>
    internal Func<string, string?>? AskWhereToSaveCopy { get; set; }

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
        if (_fillSignPath is not { } path
            || _fillSignSurface is not null
            || !IsCurrentContentPath(path)
            || BeginSignSession(path) is not { } session)
        {
            return;
        }

        // The viewer and the helper can each take seconds to answer. The
        // press shows at once that it was taken: the pill goes quiet and the
        // window works.
        IsEnteringFillSign = true;
        SignButton.IsEnabled = false;
        var cursor = Cursor;
        Cursor = Cursors.AppStarting;
        try
        {
            await EnterFillSignSessionAsync(path, session);
        }
        finally
        {
            IsEnteringFillSign = false;
            Cursor = cursor;
            SyncHeaderTools();

            // An entry that did not leave its page up leaves the file free.
            if (_fillSignSurface is null || !ReferenceEquals(_signSession, session))
            {
                EndSignSession(session);
            }
        }
    }

    private async Task EnterFillSignSessionAsync(string path, SignSession session)
    {
        var version = _loadVersion;
        // Read again rather than trusted from the load: the file, or another
        // window's work on it, may have moved on since. The writer's check
        // starts now too, so kept work comes back with the page, not after.
        var draftLoad = Task.Run(() => SignDrafts.Load(path));
        var preflight = Task.Run(() => CheckSignable(path));
        // Asked of the viewer now: a document scrolled by wheel alone has a
        // stale rail page. The viewer can take seconds to answer, so the
        // page the rail last knew starts rendering meanwhile.
        var rail = _activePdfRail;
        var asked = ReadPdfPositionAsync();
        var (file, draft) = await draftLoad;
        if (version != _loadVersion || !IsCurrentContentPath(path) || _fillSignSurface is not null)
        {
            return;
        }

        var pages = new SignPageCache(index => RenderSignPage(path, index, session.Cancellation.Token), 1);
        var pageIndex = draft?.PageIndex ?? Math.Max(0, (rail?.CurrentPage ?? 1) - 1);
        _ = pages.Get(pageIndex);
        if (draft is null)
        {
            var (currentPage, _) = await asked;
            if (version != _loadVersion || !IsCurrentContentPath(path) || _fillSignSurface is not null)
            {
                return;
            }

            pageIndex = Math.Max(0, currentPage - 1);
        }

        // Read after the wait: the rail may have counted the pages meanwhile.
        int? viewerPages = rail?.Document is { TotalPages: > 0 } counted ? counted.TotalPages : null;
        pages.PageCount = viewerPages ?? pageIndex + 1;
        var page = await pages.TryGet(pageIndex);
        if (version != _loadVersion || !IsCurrentContentPath(path) || _fillSignSurface is not null
            || PreviewContent.Content is not FrameworkElement current)
        {
            return;
        }

        if (page is null)
        {
            // The mode still opens, on a blank sheet that says so: a press
            // that ends in nothing reads as a pill that does not work. No
            // work can go on it, so the file stays free to sign, here after
            // Close or in another window now.
            DiagnosticLog.Write($"fill-sign entry render failed page={pageIndex + 1}");
            var blank = new FillSignSurface(pageIndex, viewerPages ?? pageIndex + 1, BlankSignPage(rail?.PageAspect ?? 612.0 / 792.0));
            blank.ExitRequested += ExitFillSignMode;
            _fillSignParkedContent = current;
            _fillSignSurface = blank;
            PreviewContent.Content = blank;
            blank.ShowProblem(SignPageUnavailable);
            EndSignSession(session);
            return;
        }

        // The page shows now; its tools wait for the check below.
        var surface = new FillSignSurface(pageIndex, viewerPages ?? pageIndex + 1, page);
        surface.ExitRequested += ExitFillSignMode;
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
        if (version != _loadVersion || !ReferenceEquals(_fillSignSurface, surface))
        {
            return;
        }

        if (check.Problem is null && viewerPages is { } shown && shown != check.PageCount)
        {
            // Two readers disagree on the pages: ink could land on the wrong one.
            check = new PdfSignCheck(check.PageCount, PdfFillSignWriter.UnsupportedStructure);
        }

        if (check.Problem is null && file is null)
        {
            // A draft is pinned to the file's stamp: no stamp, no draft, no work.
            check = new PdfSignCheck(check.PageCount, "This PDF can't be read right now.");
        }

        if (check.Problem is not null || file is not { } stamp)
        {
            // A problem never ends a draft: drafts exist only for files that
            // passed this check, and one waits for its file to read again.
            DiagnosticLog.Write($"fill-sign unavailable: {check.Problem}");
            surface.ApplyCheck(check);
            return;
        }

        if (draft is not null)
        {
            surface.Restore(draft.Placements, draft.Lost);
            DiagnosticLog.Write($"fill-sign restored placements={draft.Placements.Count}");
        }

        // The keeper listens before the tools come alive: no mark is ever
        // made that a draft does not keep.
        var keeper = new SignDraftKeeper(path, stamp, surface.PageIndex, surface.Placements);
        _signDraft = keeper;
        surface.WorkChanged += () => keeper.Update(surface.PageIndex, surface.Placements);
        surface.SaveRequested += (placements, overwrite) => _ = SaveFillSignAsync(surface, keeper, placements, overwrite);
        surface.ApplyCheck(check);
        pages.PageCount = check.PageCount;
        pages.KeepNeighbors(surface.PageIndex);
    }

    private async Task ShowFillSignPageAsync(FillSignSurface surface, SignPageCache pages, int pageIndex)
    {
        if (_fillSignPath is not { } path || !IsCurrentContentPath(path) || !ReferenceEquals(_fillSignSurface, surface))
        {
            return;
        }

        // Pages render at their own pace; only the last one asked for shows.
        // One that is not ready yet says it is on its way.
        var request = ++_signPageRequest;
        var rendering = pages.TryGet(pageIndex);
        if (!rendering.IsCompleted)
        {
            surface.ShowNotice($"Opening page {pageIndex + 1}\u2026");
        }

        var page = await rendering;
        if (page is null)
        {
            DiagnosticLog.Write($"fill-sign page render failed page={pageIndex + 1}");
        }

        if (request != _signPageRequest
            || !IsCurrentContentPath(path)
            || !ReferenceEquals(_fillSignSurface, surface))
        {
            return;
        }

        if (page is null)
        {
            surface.ShowNotice($"Page {pageIndex + 1} didn't open. Try again.");
            return;
        }

        surface.ShowPage(pageIndex, page);
        pages.KeepNeighbors(pageIndex);
    }

    /// <summary>
    /// A white sheet in the document's page shape, for a surface whose page
    /// did not render. One bit a pixel: it is there to be a shape.
    /// </summary>
    private static PdfPreviewPage BlankSignPage(double aspect)
    {
        aspect = double.IsFinite(aspect) ? Math.Clamp(aspect, 0.2, 5) : 612.0 / 792.0;
        var (width, height) = aspect >= 1 ? (1224, (int)Math.Round(1224 / aspect)) : ((int)Math.Round(1224 * aspect), 1224);
        var stride = (width + 7) / 8;
        var sheet = BitmapSource.Create(width, height, 96, 96, PixelFormats.Indexed1, new BitmapPalette([Colors.White, Colors.White]), new byte[stride * height], stride);
        sheet.Freeze();
        return new PdfPreviewPage(sheet, width, height);
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

        /// <summary>
        /// The page's render, started if it has none. A render that ended
        /// with no page is forgotten here, so asking again tries again: a
        /// page that failed once ahead of the reader stayed out of reach
        /// until they moved two pages away.
        /// </summary>
        public Task<PdfPreviewPage?> Get(int index)
        {
            if (!_pages.TryGetValue(index, out var page)
                || (page.IsCompleted && (!page.IsCompletedSuccessfully || page.Result is null)))
            {
                try
                {
                    page = render(index);
                }
                catch (Exception ex)
                {
                    page = Task.FromException<PdfPreviewPage?>(ex);
                }

                _pages[index] = page;
            }

            return page;
        }

        /// <summary>The page, or null when its render failed in any way.</summary>
        public async Task<PdfPreviewPage?> TryGet(int index)
        {
            try
            {
                return await Get(index);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"fill-sign page render faulted page={index + 1}: {ex.GetType().Name}");
                return null;
            }
        }

        public void KeepNeighbors(int index)
        {
            foreach (var far in _pages.Keys.Where(key => Math.Abs(key - index) > 1).ToList())
            {
                _pages.Remove(far);
            }

            if (index + 1 < PageCount)
            {
                _ = TryGet(index + 1);
            }

            if (index > 0)
            {
                _ = TryGet(index - 1);
            }
        }
    }

    /// <summary>
    /// One sign session: from the Sign pill's press until the mode ends, or
    /// the window moves on to another item or closes. Cancelling it stops
    /// every page helper it started.
    /// </summary>
    private sealed record SignSession(PreviewWindow Window, string Key, long Version, CancellationTokenSource Cancellation)
    {
        /// <summary>A window that moved on or closed counted a new load, and its session went with it.</summary>
        public bool IsLive => ReferenceEquals(Window._signSession, this) && Window._loadVersion == Version;
    }

    /// <summary>
    /// Starts this window's sign session on the file. Two sessions on one
    /// PDF would each keep the whole work and write it over the other's
    /// draft, so a window that already signs it comes forward instead and
    /// this answers null. UI thread only.
    /// </summary>
    private SignSession? BeginSignSession(string path)
    {
        var key = SignDrafts.Canonical(path);
        if (SignSessions.TryGetValue(key, out var held) && held.IsLive)
        {
            if (!ReferenceEquals(held.Window, this))
            {
                DiagnosticLog.Write("fill-sign is open in another window, bringing it forward");
                held.Window.BringForward();
            }

            return null;
        }

        // Any earlier session here ended when the window moved on; letting
        // it go keeps a closed window from living on in the table.
        EndSignSession(_signSession);

        // A new item or a hide cancels the load, and the session with it.
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_loadCancellation?.Token ?? CancellationToken.None);
        var session = new SignSession(this, key, _loadVersion, cancellation);
        SignSessions[key] = session;
        _signSession = session;
        _ = Task.Run(() => ClearAbandonedSigningTemps(path, DateTime.UtcNow));
        return session;
    }

    /// <summary>
    /// A save that died midway (a crash, a power cut) leaves its
    /// temporary file beside the PDF. The next session on the file clears
    /// them: only exact names, only once older than any save takes, and only
    /// while the original is there, since a failed replace can leave the
    /// temporary file as the one copy.
    /// </summary>
    internal static void ClearAbandonedSigningTemps(string path, DateTime nowUtc)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(folder) || !File.Exists(path))
            {
                return;
            }

            foreach (var temp in Directory.EnumerateFiles(folder, $".{name}.*.signing"))
            {
                var stem = Path.GetFileName(temp);
                var tag = stem.Length == name.Length + 18 ? stem.Substring(name.Length + 2, 8) : string.Empty;
                if (tag.Length != 8
                    || !tag.All(char.IsAsciiHexDigitLower)
                    || !stem.StartsWith($".{name}.", StringComparison.OrdinalIgnoreCase)
                    || nowUtc - File.GetLastWriteTimeUtc(temp) < TimeSpan.FromMinutes(10))
                {
                    continue;
                }

                File.Delete(temp);
                DiagnosticLog.Write("fill-sign cleared an abandoned save");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"fill-sign temp sweep failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The session is over: its helpers stop and its file is free to sign
    /// again. Safe to call twice. The source is cancelled, never disposed,
    /// so a late render that reads its token still gets an answer.
    /// </summary>
    private void EndSignSession(SignSession? session)
    {
        if (session is null)
        {
            return;
        }

        session.Cancellation.Cancel();
        if (ReferenceEquals(_signSession, session))
        {
            _signSession = null;
        }

        if (SignSessions.TryGetValue(session.Key, out var held) && ReferenceEquals(held, session))
        {
            SignSessions.Remove(session.Key);
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
        EndSignSession(_signSession);
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

    private async Task SaveFillSignAsync(FillSignSurface surface, SignDraftKeeper keeper, IReadOnlyList<SignPlacement> placements, bool overwrite)
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
        var stamp = keeper.File;
        string finalPath;
        try
        {
            if (overwrite)
            {
                finalPath = path;
                var temp = SigningTempPath(path);
                try
                {
                    await Task.Run(() =>
                    {
                        RequireUnchanged(path, stamp);
                        WriteSignedPdf(path, temp, placements);
                    });

                    // The embedded viewer holds the original open; release
                    // it and give Chromium a beat to let go before the swap.
                    // The placements survive any failure: the surface stays.
                    if (StillOwnsSurface())
                    {
                        _fillSignParkedContent = null;
                        _currentDisposable?.Dispose();
                        _currentDisposable = null;
                    }

                    await ReplaceWithRetriesAsync(temp, path, stamp);
                }
                catch
                {
                    CleanUpSigningTemp(temp, path);
                    throw;
                }
            }
            else
            {
                finalPath = await SaveSignedCopyAsync(path, stamp, placements, StillOwnsSurface);
            }
        }
        catch (OperationCanceledException)
        {
            // No place was named for the copy: the choices are back, and say why.
            if (StillOwnsSurface())
            {
                surface.ShowSaveFailure(CopyNeedsAnotherFolder);
            }

            return;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"fill-sign save failed {path}: {ex.GetType().Name}: {ex.Message}");
            if (StillOwnsSurface())
            {
                surface.ShowSaveFailure(SaveFailureReason(ex, overwrite));
            }

            return;
        }

        DiagnosticLog.Write($"fill-sign saved {(overwrite ? "in place" : "as copy")} placements={placements.Count}");
        keeper.Discard();
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
        EndSignSession(_signSession);
        SyncHeaderTools();

        // The saved file is the honest outcome: show it.
        if (!ShowPreview(finalPath, Hwnd))
        {
            PresentationFailed?.Invoke(this, LastPresentationFailureReason);
        }
    }

    /// <summary>What a save says when the PDF is no longer the version the work was placed on.</summary>
    internal const string ChangedSinceSigningBegan = "This PDF was changed in another app while you were signing. Nothing was saved.";

    /// <summary>What a save says when the copy's folder is read-only and no other was picked.</summary>
    internal const string CopyNeedsAnotherFolder = "This folder is read-only. Save a signed copy again and pick another.";

    // ERROR_WRITE_PROTECT, ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION,
    // ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL, as an IOException carries them.
    private const int WriteProtected = unchecked((int)0x80070013);
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int HandleDiskFull = unchecked((int)0x80070027);
    private const int DiskFull = unchecked((int)0x80070070);

    /// <summary>
    /// What a failed save says in place of the choices it brings back. Each
    /// line has to be true whoever holds the file and wherever the copy was
    /// going: the holder may be this app's own viewer or another of its
    /// windows, and a copy is only ever written beside the original or
    /// where the user said.
    /// </summary>
    internal static string SaveFailureReason(Exception ex, bool replacing) => ex switch
    {
        // Only a sentence written for the user is shown as it is.
        SignRefusalException => ex.Message,
        _ when IsReadOnlyPlace(ex) => replacing
            ? "The original can't be replaced here. Save a signed copy instead."
            : "That folder can't be written to. Save a signed copy again and pick another.",
        IOException { HResult: SharingViolation or LockViolation } =>
            "The file is in use. Close anything else that shows it, then try again.",
        IOException { HResult: DiskFull or HandleDiskFull } => "The disk is full. Nothing was saved.",
        _ => "The signed file couldn't be saved. Nothing was changed."
    };

    /// <summary>The folder, the file or the disk refuses a write that another place would take.</summary>
    private static bool IsReadOnlyPlace(Exception ex) =>
        ex is UnauthorizedAccessException or IOException { HResult: WriteProtected };

    /// <summary>
    /// The work was placed on one version of the file. Saving onto any
    /// other would put ink on whatever is there now, or write over another
    /// app's newer save, so the save is refused and the work stays.
    /// </summary>
    private static void RequireUnchanged(string path, FileStamp stamp)
    {
        if (FileStamp.Of(path) != stamp)
        {
            throw new SignRefusalException(ChangedSinceSigningBegan);
        }
    }

    /// <summary>
    /// A file beside the original, its own for each save: two saves at once
    /// never write into, or swap in, each other's. It is not hidden. The
    /// swap gives the original's attributes to the signed file on NTFS, but
    /// nothing promises that on another file system, and a signed document
    /// that turns up hidden is a document the user cannot find.
    /// </summary>
    internal static string SigningTempPath(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, $".{Path.GetFileName(path)}.{Guid.NewGuid().ToString("N")[..8]}.signing");

    /// <summary>
    /// Writes the copy under the first free "(signed)" name. Another save
    /// can take that name between the look and the write; the writer then
    /// refuses it and the next free name is tried.
    /// </summary>
    private string WriteSignedCopyBeside(string path, IReadOnlyList<SignPlacement> placements)
    {
        for (var attempt = 0; ; attempt++)
        {
            var candidate = NextSignedCopyPath(path);
            try
            {
                WriteSignedPdf(path, candidate, placements);
                return candidate;
            }
            catch (IOException ex) when (ex.HResult == FileExists && attempt < 8)
            {
                // Taken a moment ago; the next look skips it.
            }
        }
    }

    /// <summary>
    /// The copy beside the original, or, when that folder cannot take a new
    /// file, wherever the user says. A copy that could only be saved in a
    /// place it cannot be saved in is no way to sign a PDF on a read-only
    /// share or disc. Cancelled when the user names no place.
    /// </summary>
    private async Task<string> SaveSignedCopyAsync(
        string path,
        FileStamp stamp,
        IReadOnlyList<SignPlacement> placements,
        Func<bool> stillOwnsSurface)
    {
        try
        {
            return await Task.Run(() =>
            {
                RequireUnchanged(path, stamp);
                return WriteSignedCopyBeside(path, placements);
            });
        }
        catch (Exception ex) when (IsReadOnlyPlace(ex) && stillOwnsSurface())
        {
            DiagnosticLog.Write($"fill-sign copy needs another folder: {ex.GetType().Name}");
        }

        // The save bar says "Saving" while the dialog is up, and nothing on
        // the surface takes input: the dialog is the one thing to answer.
        var chosen = (AskWhereToSaveCopy ?? AskForCopyPath)(path);
        if (chosen is null || !stillOwnsSurface())
        {
            throw new OperationCanceledException();
        }

        return await Task.Run(() =>
        {
            RequireUnchanged(path, stamp);
            return WriteSignedCopyAt(path, chosen, placements);
        });
    }

    private string? AskForCopyPath(string path)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the signed copy",
            FileName = Path.GetFileName(NextSignedCopyPath(Path.Combine(documents, Path.GetFileName(path)))),
            InitialDirectory = documents,
            Filter = "PDF|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// The copy under the name the user gave. A file already there was
    /// agreed to in the dialog, and the writer still never writes into one:
    /// the copy is made beside the name and moved onto it.
    /// </summary>
    private string WriteSignedCopyAt(string path, string chosen, IReadOnlyList<SignPlacement> placements)
    {
        if (Path.GetFullPath(chosen).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            throw new SignRefusalException("That is the original. Pick another name for the signed copy.");
        }

        if (!File.Exists(chosen))
        {
            WriteSignedPdf(path, chosen, placements);
            return chosen;
        }

        var staged = SigningTempPath(chosen);
        try
        {
            WriteSignedPdf(path, staged, placements);
            File.Move(staged, chosen, overwrite: true);
            return chosen;
        }
        catch
        {
            CleanUpSigningTemp(staged, chosen);
            throw;
        }
    }

    /// <summary>
    /// After a failed save the temporary file goes, unless it is the only
    /// copy of the document: a replace that lost the original and could
    /// not put the signed file in its place leaves the signed file where
    /// it is, and the failure names it.
    /// </summary>
    internal static void CleanUpSigningTemp(string temp, string original)
    {
        try
        {
            if (File.Exists(temp) && File.Exists(original))
            {
                File.Delete(temp);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"fill-sign temp cleanup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Swaps the signed file in, off the UI thread, retrying while the
    /// viewer lets go of the original. Every try checks first that the
    /// original is still the version the work was placed on.
    /// </summary>
    private static async Task ReplaceWithRetriesAsync(string signedPath, string destinationPath, FileStamp stamp)
    {
        var originalLost = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (!originalLost)
                    {
                        RequireUnchanged(destinationPath, stamp);
                    }

                    SwapIn(signedPath, destinationPath, ReplaceFile, ref originalLost);
                });
                return;
            }
            catch (IOException) when (attempt < 12)
            {
                await Task.Delay(250);
            }
            catch (IOException) when (originalLost)
            {
                throw new SignRefusalException(
                    $"The signed PDF was saved as \"{Path.GetFileName(signedPath)}\" in the same folder. Rename it to finish.");
            }
        }
    }

    private static void ReplaceFile(string signedPath, string destinationPath) =>
        File.Replace(signedPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);

    /// <summary>
    /// One try at the swap. Replacing a file can fail after the original is
    /// already gone. The signed file is then the only copy of the document:
    /// it takes the original's name, and that is the save, not a failure.
    /// </summary>
    internal static void SwapIn(string signedPath, string destinationPath, Action<string, string> replace, ref bool originalLost)
    {
        if (!originalLost)
        {
            try
            {
                replace(signedPath, destinationPath);
                return;
            }
            catch (IOException) when (!File.Exists(destinationPath) && File.Exists(signedPath))
            {
                originalLost = true;
            }
        }

        File.Move(signedPath, destinationPath);
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
