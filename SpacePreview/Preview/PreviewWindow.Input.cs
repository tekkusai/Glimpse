using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    /// <summary>
    /// Stopwatch stamp of the key press that started the open or browse now
    /// being shown; 0 when no key started it. The controller sets it before
    /// each ShowPreview so the reveal clock can start at the input.
    /// </summary>
    internal long InputStamp { get; set; }

    /// <summary>
    /// Ctrl+F reached the browser while it owned the keyboard. Only the PDF
    /// viewer actually opens a find bar for it (plain pages have no find UI
    /// in WebView2), and while that bar is up the next Esc is handled by it -
    /// closing the bar, not the preview. One Esc, one meaning.
    /// </summary>
    public void NoteFindShortcut()
    {
        if (TakesFindShortcut)
        {
            _escapeYieldsToContent = true;
        }
    }

    /// <summary>
    /// Ctrl+F opens a find bar in what is on show. Published with the
    /// rest of the window's facts, so a key pressed before the dispatcher
    /// has noted the Ctrl+F is still judged with the bar open.
    /// </summary>
    internal bool TakesFindShortcut =>
        FindVisualChild<WebView2>(PreviewContent) is { CoreWebView2.Source: { } source }
        && IsPdfViewerSource(source);

    /// <summary>True once per armed find bar: this Esc is the content's.</summary>
    public bool ConsumeEscapeYield()
    {
        if (!_escapeYieldsToContent)
        {
            return false;
        }

        _escapeYieldsToContent = false;
        _lastContentClickInViewerChrome = false;
        return true;
    }

    /// <summary>
    /// While the content's find bar is open, typed keys are the user's words:
    /// Space is a literal space and Enter is "next match".
    /// </summary>
    public bool IsEscapeYieldArmed => _escapeYieldsToContent;

    /// <summary>
    /// Enter's one promise is "open in the default app". The single honest
    /// exception besides the find bar is the PDF viewer's own toolbar: a
    /// user who just clicked its page box and typed a number means "jump",
    /// not "open". A click anywhere in the document body keeps the promise;
    /// only a click in the viewer's chrome band yields the key.
    /// </summary>
    internal bool EnterYieldsToContent =>
        _lastContentClickInViewerChrome
        && ContentOwnsKeyboard()
        && FindVisualChild<WebView2>(PreviewContent) is { CoreWebView2.Source: { } source }
        && IsPdfViewerSource(source);

    /// <summary>Spends the toolbar click: the yield holds for one Enter.</summary>
    public bool ConsumeEnterYieldToContent()
    {
        var yield = EnterYieldsToContent;
        _lastContentClickInViewerChrome = false;
        return yield;
    }

    /// <summary>The viewer's Source may carry open parameters (#view=FitH).</summary>
    private static bool IsPdfViewerSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            : source.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Media by what is actually playing: a shortcut to a video counts.</summary>
    public bool IsMediaPreview =>
        CurrentPath is not null && FileKinds.IsMedia(_shortcutTarget ?? CurrentPath);

    /// <summary>A player whose page is up, so Space and the arrows can reach it.</summary>
    internal bool IsMediaPlayable =>
        IsMediaPreview && FindVisualChild<WebView2>(PreviewContent) is { CoreWebView2: not null };

    private bool IsCurrentContentPath(string path) =>
        string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)
        || string.Equals(_shortcutTarget, path, StringComparison.OrdinalIgnoreCase);

    public bool HasHeaderZoom => _zoomWebView is not null || _imageZoom is not null;

    public bool TryAdjustZoomFromShortcut(int vk)
    {
        if (_zoomWebView is null && _imageZoom is { } image)
        {
            var direction = IsZoomInKey(vk) ? 1 : IsZoomOutKey(vk) ? -1 : IsZoomResetKey(vk) ? 0 : (int?)null;
            if (direction is { } step)
            {
                image.ZoomBy(step);
            }

            return direction is not null;
        }

        if (_zoomWebView is null)
        {
            return false;
        }

        if (IsZoomInKey(vk))
        {
            AdjustZoom(1);
            return true;
        }

        if (IsZoomOutKey(vk))
        {
            AdjustZoom(-1);
            return true;
        }

        if (IsZoomResetKey(vk))
        {
            ResetZoom();
            return true;
        }

        return false;
    }

    /// <summary>The user clicked into the content, so up and down may be its keys.</summary>
    internal bool UserEngagedContent => _userEngagedContent;

    /// <summary>A WPF text box in this window holds the keyboard.</summary>
    internal bool HasTextFocus =>
        Keyboard.FocusedElement is TextBoxBase box && GetWindow(box) == this;

    internal bool CanRotateFromKeyboard => !_isFileDialogMode && _rotationSession is not null;

    internal void RotateFromKeyboard(int direction) => RotateCurrentImage(direction);

    internal void ToggleFillScreen() => ToggleMaximize();

    /// <summary>
    /// True when keyboard focus sits in a child hwnd of this window (the
    /// browser view) rather than in the WPF chrome. WPF cannot answer this
    /// reliably for a windowed WebView2, so ask Win32 directly.
    /// </summary>
    internal bool ContentOwnsKeyboard()
    {
        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(0, ref info)
            || info.hwndFocus == IntPtr.Zero
            || info.hwndFocus == Hwnd)
        {
            return false;
        }

        return NativeMethods.GetAncestor(info.hwndFocus, NativeMethods.GA_ROOT) == Hwnd;
    }

    /// <summary>
    /// A window from this preview's browser processes: a print dialog, a file
    /// chooser or a prompt the page opened. It belongs to the glance.
    /// </summary>
    internal bool IsWebViewWindow(IntPtr root)
    {
        if (root == IntPtr.Zero)
        {
            return false;
        }

        var processId = NativeMethods.GetWindowProcessId(root);
        lock (_popupSync)
        {
            return _webViewProcessIds.Contains(processId);
        }
    }

    public bool IsOutsideDismissSuppressed =>
        Interlocked.Read(ref _suppressOutsideDismissUntilTicks) > DateTime.UtcNow.Ticks;

    private void SuppressOutsideDismissFor(TimeSpan duration) =>
        Interlocked.Exchange(ref _suppressOutsideDismissUntilTicks, DateTime.UtcNow.Add(duration).Ticks);

    /// <summary>
    /// Progressive escape: the sheet closes first, then a running slideshow
    /// stops, and only then does Esc mean dismiss. One key, nearest surface.
    /// </summary>
    internal bool TryConsumeEscapeLayer()
    {
        if (_gridView is not null)
        {
            CloseGridView();
            return true;
        }

        if (IsSlideshowActive)
        {
            StopSlideshow(restoreSize: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The window's own keys. The keyboard hook claims Space, Esc, Enter, the
    /// arrows and the header shortcuts for a transient preview before they
    /// arrive here; this handles what the hook leaves to the window (copy, a
    /// pinned window's keys) and keeps sign mode's keys with the page.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Sign mode: Space, Enter and the arrows belong to the page and its
        // date editor. Esc walks the mode's layers unless a text box takes it.
        if (_fillSignSurface is not null
            && e.Key is Key.Space or Key.Enter or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBoxBase)
            {
                RequestFillSignEscape();
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl && e.Key == Key.C)
        {
            // Selected text copies as text; otherwise Ctrl+C copies the file
            // itself.
            if (Keyboard.FocusedElement is TextBox { SelectionLength: > 0 } textBox)
            {
                textBox.Copy();
            }
            else if (Keyboard.FocusedElement is RichTextBox richTextBox && !richTextBox.Selection.IsEmpty)
            {
                richTextBox.Copy();
            }
            else if (FindVisualChild<WebView2>(PreviewContent) is { } webView)
            {
                DiagnosticLog.Write("ctrl+c reached window with webview present");
                _ = CopyWebViewSelectionAsync(webView);
            }
            else
            {
                CopyCurrentFile();
            }

            e.Handled = true;
            return;
        }

        if (ctrl
            && (Keyboard.Modifiers & ModifierKeys.Alt) != ModifierKeys.Alt
            && TryAdjustZoomFromShortcut(KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key)))
        {
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.R)
        {
            RotateCurrentImage((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        // A pinned window keeps its own Enter and Esc: open the file, close
        // the window.
        if (IsPinned && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.Key == Key.Enter)
            {
                OpenCurrent();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }
}
