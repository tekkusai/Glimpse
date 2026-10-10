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
    /// <summary>
    /// Ctrl+F reached the browser while it owned the keyboard. Only the PDF
    /// viewer actually opens a find bar for it (plain pages have no find UI
    /// in WebView2), and while that bar is up the next Esc is handled by it -
    /// closing the bar, not the preview. One Esc, one meaning.
    /// </summary>
    public void NoteFindShortcut()
    {
        if (FindVisualChild<WebView2>(PreviewContent) is { CoreWebView2.Source: { } source }
            && IsPdfViewerSource(source))
        {
            _escapeYieldsToContent = true;
        }
    }

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
    /// Space is a literal space and Enter is "next match". The hook checks
    /// this before claiming either key for its preview meaning.
    /// </summary>
    public bool IsEscapeYieldArmed => _escapeYieldsToContent;

    /// <summary>
    /// Enter's one promise is "open in the default app". The single honest
    /// exception besides the find bar is the PDF viewer's own toolbar: a
    /// user who just clicked its page box and typed a number means "jump",
    /// not "open". A click anywhere in the document body keeps the promise;
    /// only a click in the viewer's chrome band yields the key.
    /// </summary>
    public bool ConsumeEnterYieldToContent()
    {
        if (!_lastContentClickInViewerChrome)
        {
            return false;
        }

        if (!ContentOwnsKeyboard())
        {
            _lastContentClickInViewerChrome = false;
            return false;
        }

        var yield = FindVisualChild<WebView2>(PreviewContent) is { CoreWebView2.Source: { } source }
            && IsPdfViewerSource(source);
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

    private bool IsCurrentContentPath(string path) =>
        string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)
        || string.Equals(_shortcutTarget, path, StringComparison.OrdinalIgnoreCase);

    public bool HasHeaderZoom => _zoomWebView is not null;

    public bool TryAdjustZoomFromShortcut(int vk)
    {
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

    public bool ShouldBrowseWithPreviewArrowKeys(bool vertical)
    {
        if (IsPinned || CurrentPath is null)
        {
            return false;
        }

        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
        {
            return false;
        }

        // Left/right always browse - the main browsing rule. Media
        // seeking is handled by the scrubber; keys that mean previous/next
        // everywhere must not change meaning on a video. Only up/down yield
        // to content the user clicked into (document scroll, player volume),
        // and engagement means the user clicked the content area - a
        // browser view that grabbed focus on its own does not count.
        if (vertical && _userEngagedContent && ContentOwnsKeyboard())
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when keyboard focus sits in a child hwnd of this window (the
    /// browser view) rather than in the WPF chrome. WPF cannot answer this
    /// reliably for a windowed WebView2, so ask Win32 directly.
    /// </summary>
    private bool ContentOwnsKeyboard()
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.C)
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

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && (Keyboard.Modifiers & ModifierKeys.Alt) != ModifierKeys.Alt
            && TryAdjustZoomFromShortcut(KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key)))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ToggleGridView();
            }
            else if (IsGridViewOpen)
            {
                CommitGridCursor();
            }
            else
            {
                OpenCurrent();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            RotateCurrentImage((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        // While the sheet is up the arrows walk its cursor, whatever the
        // content underneath would normally do with them.
        if (IsGridViewOpen && Keyboard.Modifiers == ModifierKeys.None
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            MoveGridCursor(
                e.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 },
                e.Key switch { Key.Up => -1, Key.Down => 1, _ => 0 });
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        // Left and right browse the folder even when the preview holds the
        // keyboard - same as the chevrons, under the same one policy the
        // hook consults (media keeps its seek, text keeps its caret).
        if (e.Key is Key.Left or Key.Right
            && Keyboard.Modifiers == ModifierKeys.None
            && ShouldBrowseWithPreviewArrowKeys(vertical: false))
        {
            BrowseRequested?.Invoke(this, e.Key == Key.Left ? PreviewBrowseDirection.Previous : PreviewBrowseDirection.Next);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Escape or Key.Space)
        {
            if (e.Key == Key.Escape && TryConsumeEscapeLayer())
            {
                e.Handled = true;
                return;
            }

            if (!IsPinned)
            {
                DismissRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            // Pinned: Space stays with the content (play/pause, page down),
            // but Esc is the universal escape hatch - it closes this window
            // the same way as the close button.
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
