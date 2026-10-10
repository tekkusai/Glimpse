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
    internal const int TextDisplayLimit = 400_000;
    internal const int LongLineDisplayLimit = 120_000;
    private const int LongLineThreshold = 32_768;

    private static (string Text, bool Wrap)? LoadFormattedText(string path) =>
        FormatTextPreview(path, TextPreviewLoader.TryLoad(path));

    internal static (string Text, bool Wrap)? FormatTextPreview(string path, TextPreview? preview)
    {
        // WPF's TextBox lays out the whole document on the UI thread; past
        // these sizes the first layout visibly stutters, so large previews
        // trade completeness for instant open. A single enormous record is
        // more expensive than ordinary multiline text, so it gets a tighter
        // budget. This covers bencoded metadata, minified source maps,
        // lockfiles, and malformed CSV/log records without type exceptions.
        if (preview is null)
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        var text = preview.Text;
        var wrap = extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsonc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".log", StringComparison.OrdinalIgnoreCase);

        var displayLimit = ContainsPathologicalLine(text)
            ? LongLineDisplayLimit
            : TextDisplayLimit;
        if (text.Length > displayLimit)
        {
            text = text[..displayLimit]
                + $"\n\nShowing the first {displayLimit:N0} characters. Open the file for the rest.";
            wrap = false;
        }

        return (text, wrap);
    }

    private static bool ContainsPathologicalLine(string text)
    {
        var lineLength = 0;
        var scanLength = Math.Min(text.Length, TextDisplayLimit);
        for (var index = 0; index < scanLength; index++)
        {
            if (text[index] is '\r' or '\n')
            {
                lineLength = 0;
                continue;
            }

            if (++lineLength > LongLineThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Plain text set like the code page beside it (docs/DESIGN.md, Type):
    /// 13 px Cascadia Mono at 1.6 line height, the same top inset, primary
    /// text, so a .txt next to a .cs reads as one app.
    /// </summary>
    private static FrameworkElement BuildTextPreview(TextPreview preview, bool wrap)
    {
        const double fontSize = 13;
        var textBox = BuildSelectableTextBox(
            preview.Text,
            new FontFamily("Cascadia Mono, Consolas"),
            fontSize,
            wrap,
            // The text box insets its text 2 px on each side of its own.
            new Thickness(22, 18, 22, 48),
            Theme.Current.CanvasBg);
        TextBlock.SetLineHeight(textBox, fontSize * 1.6);
        TextBlock.SetLineStackingStrategy(textBox, LineStackingStrategy.BlockLineHeight);
        return textBox;
    }

    private static TextBox BuildSelectableTextBox(
        string text,
        FontFamily fontFamily,
        double fontSize,
        bool wrap,
        Thickness padding,
        Color background)
    {
        var palette = Theme.Current;
        var textBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Theme.Brush(background),
            Foreground = Theme.Brush(palette.TextPrimary),
            CaretBrush = Theme.Brush(palette.TextPrimary),
            SelectionBrush = Theme.Brush(palette.Accent),
            SelectionOpacity = 0.55,
            Padding = padding,
            FontFamily = fontFamily,
            FontSize = fontSize,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            AcceptsTab = true,
            IsUndoEnabled = false,
            SpellCheck = { IsEnabled = false },
            Focusable = true,
            ClipToBounds = true
        };
        TextOptions.SetTextFormattingMode(textBox, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(textBox, TextRenderingMode.ClearType);
        AttachMiddleAutoScroll(textBox, () => FindVisualChild<ScrollViewer>(textBox), horizontal: !wrap);
        ApplyScrollBarStyle(textBox);
        return textBox;
    }

    /// <summary>
    /// WPF does not push implicit styles into template-internal scrollbars
    /// reliably, so the shared style is assigned directly once the template
    /// has been applied. Keeps every surface's scrollbar identical.
    /// </summary>
    private static void ApplyScrollBarStyle(FrameworkElement host)
    {
        // Staged content attaches COLLAPSED, and a collapsed control has no
        // template yet - at Loaded there are no scrollbars to style, and the
        // preview would show native white bars after its reveal. Style on
        // every visibility gain too, once layout has built the tree.
        var wired = false;
        void Apply()
        {
            if (Application.Current.Resources["PreviewScrollBar"] is not Style style)
            {
                DiagnosticLog.Write("scrollbar style missing from application resources");
                return;
            }

            var found = false;
            foreach (var scrollBar in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(host))
            {
                scrollBar.Style = style;
                found = true;
            }

            if (!found || wired)
            {
                return;
            }

            var scrollViewer = host as ScrollViewer ?? FindVisualChild<ScrollViewer>(host);
            if (scrollViewer is not null)
            {
                wired = true;
                scrollViewer.ScrollChanged += (_, _) => ClampThumbSizes(scrollViewer);
                ClampThumbSizes(scrollViewer);
            }
        }

        host.Loaded += (_, _) => Apply();
        host.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                host.Dispatcher.BeginInvoke(Apply, DispatcherPriority.Loaded);
            }
        };
    }

    /// <summary>
    /// WPF's Track only respects the tiny system drag metric, so huge
    /// documents get a near-invisible thumb. Inflating ViewportSize keeps the
    /// thumb grabbable; drag math stays consistent because Track derives
    /// position and size from the same value.
    /// </summary>
    private static void ClampThumbSizes(ScrollViewer scrollViewer)
    {
        const double MinThumb = 30;
        foreach (var bar in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(scrollViewer))
        {
            var vertical = bar.Orientation == Orientation.Vertical;
            var range = vertical ? scrollViewer.ScrollableHeight : scrollViewer.ScrollableWidth;
            var viewport = vertical ? scrollViewer.ViewportHeight : scrollViewer.ViewportWidth;
            var trackLength = vertical ? bar.ActualHeight : bar.ActualWidth;
            if (range <= 0 || viewport <= 0 || trackLength <= MinThumb * 3)
            {
                continue;
            }

            var natural = trackLength * viewport / (range + viewport);
            bar.ViewportSize = natural < MinThumb
                ? MinThumb * range / (trackLength - MinThumb)
                : viewport;
        }
    }

    private static FrameworkElement BuildScrollSurface(UIElement child, Thickness padding, bool horizontal, Color background)
    {
        var scrollViewer = new ScrollViewer
        {
            Background = Theme.Brush(background),
            Padding = padding,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = child
        };

        scrollViewer.PreviewMouseWheel += (_, e) =>
        {
            // Preview events tunnel: when this surface has nothing to scroll,
            // stay out of the way so a nested list (folder breakdown) still
            // gets the wheel.
            if (scrollViewer.ScrollableHeight <= 0 && scrollViewer.ScrollableWidth <= 0)
            {
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && horizontal)
            {
                scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta * 0.9);
            }
            else
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta * 0.9);
            }

            e.Handled = true;
        };

        AttachMiddleAutoScroll(scrollViewer, () => scrollViewer, horizontal);
        ApplyScrollBarStyle(scrollViewer);

        return scrollViewer;
    }

    private static void AttachMiddleAutoScroll(FrameworkElement eventSource, Func<ScrollViewer?> getScrollViewer, bool horizontal)
    {
        DispatcherTimer? autoScrollTimer = null;
        Point origin = default;

        void Stop()
        {
            autoScrollTimer?.Stop();
            autoScrollTimer = null;
            eventSource.ReleaseMouseCapture();
            eventSource.Cursor = Cursors.Arrow;
        }

        eventSource.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle)
            {
                if (autoScrollTimer is not null)
                {
                    Stop();
                }

                return;
            }

            if (autoScrollTimer is not null)
            {
                Stop();
                e.Handled = true;
                return;
            }

            var scrollViewer = getScrollViewer();
            if (scrollViewer is null)
            {
                return;
            }

            origin = e.GetPosition(eventSource);
            eventSource.CaptureMouse();
            eventSource.Cursor = Cursors.SizeNS;
            autoScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            autoScrollTimer.Tick += (_, _) =>
            {
                var activeScrollViewer = getScrollViewer();
                if (activeScrollViewer is null)
                {
                    Stop();
                    return;
                }

                var position = Mouse.GetPosition(eventSource);
                var verticalDelta = AutoScrollDelta(position.Y - origin.Y);
                if (Math.Abs(verticalDelta) > 0)
                {
                    activeScrollViewer.ScrollToVerticalOffset(activeScrollViewer.VerticalOffset + verticalDelta);
                }

                if (horizontal)
                {
                    var horizontalDelta = AutoScrollDelta(position.X - origin.X);
                    if (Math.Abs(horizontalDelta) > 0)
                    {
                        activeScrollViewer.ScrollToHorizontalOffset(activeScrollViewer.HorizontalOffset + horizontalDelta);
                    }
                }
            };
            autoScrollTimer.Start();
            e.Handled = true;
        };

        eventSource.Unloaded += (_, _) => Stop();
    }

    private static double AutoScrollDelta(double distance)
    {
        const double deadZone = 8;
        var magnitude = Math.Abs(distance) - deadZone;
        if (magnitude <= 0)
        {
            return 0;
        }

        return Math.Sign(distance) * Math.Min(82, Math.Pow(magnitude, 1.22) * 0.12);
    }
}
