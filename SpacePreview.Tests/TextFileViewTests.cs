using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// A text file too long to lay out whole is read a piece at a time into the
/// same text box. The box holds a run of the file around the reader, grows
/// toward wherever they scroll, lets go of the far end past its budget, and
/// goes to the file's real end and start on End and Home. The view tests
/// use small pieces and a small budget so a 400 KB file behaves as a
/// 20 MB one does at the real sizes.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class TextFileViewTests
{
    private const int Head = 8 * 1024;
    private const int Piece = 4 * 1024;
    private const int Budget = 40_000;

    private static readonly Rect ParkedPixels = new(-30000, 0, 1920, 1040);

    [Fact]
    public Task AFileShortOfTheBudgetIsAllThereOnceTheBoxHasBeenIdle() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, text) = Write(temp, Lines(0, 500));
        var (window, box, view) = Show(path);
        try
        {
            // It opens on its first lines alone.
            Assert.InRange(box.Text.Length, 1, Head);
            await Settle(view);

            Assert.Equal(text, box.Text);
            Assert.Equal((0L, (long)text.Length), view.Range);
            Assert.Equal(text.Length, view.Characters);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ALongFileIsHeldAroundTheReaderAndNeverWhole() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, text) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);

            // Filled from the start up to the budget, and no further while
            // the reader stays at the top.
            Assert.Equal(0, view.Range.Start);
            Assert.InRange(view.Characters, Budget, Budget + Piece);
            Assert.Equal(Slice(text, view.Range), box.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ScrollingDownReadsOnLetsGoOfTheTopAndKeepsThePlace() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, text) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);
            var held = view.Range;

            box.ScrollToEnd();
            box.UpdateLayout();
            var looking = box.GetLineText(box.GetFirstVisibleLineIndex());
            await Settle(view);

            // More of the file below, the far top gone, the budget kept,
            // and the same line still at the top of the view.
            Assert.True(view.Range.End > held.End);
            Assert.True(view.Range.Start > 0);
            Assert.InRange(view.Characters, 1, Budget + Piece);
            Assert.Equal(Slice(text, view.Range), box.Text);
            Assert.Equal(looking, box.GetLineText(box.GetFirstVisibleLineIndex()));

            // And on, to the last line of the file.
            for (var step = 0; step < 400 && view.Range.End < text.Length; step++)
            {
                box.ScrollToEnd();
                await Settle(view);
            }

            Assert.Equal(text.Length, view.Range.End);
            Assert.EndsWith("line 0006499 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);
            Assert.InRange(view.Characters, 1, Budget + Piece);
            Assert.Equal(Slice(text, view.Range), box.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task EndGoesToTheFilesLastLineAndHomeBackToItsFirst() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, text) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);

            view.GoToEnd();
            await Settle(view);
            Assert.Equal(text.Length, view.Range.End);
            Assert.True(view.Range.Start > 0);
            Assert.EndsWith("line 0006499 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);
            Assert.Equal(Slice(text, view.Range), box.Text);
            var scroll = Scroll(box);
            Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 1, scroll.ScrollableHeight + 1);

            view.GoToStart();
            await Settle(view);
            Assert.Equal(0, view.Range.Start);
            Assert.StartsWith("line 0000000 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);
            Assert.Equal(Slice(text, view.Range), box.Text);
            Assert.Equal(0, Scroll(box).VerticalOffset);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ScrollingUpFromTheEndReadsBackAndKeepsThePlace() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, text) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            view.GoToEnd();
            await Settle(view);

            // At rest it has filled back from the end to the budget.
            Assert.Equal(text.Length, view.Range.End);
            Assert.InRange(view.Characters, Budget, Budget + Piece);
            var held = view.Range;

            box.ScrollToHome();
            box.UpdateLayout();
            var looking = box.GetLineText(box.GetFirstVisibleLineIndex());
            await Settle(view);

            Assert.True(view.Range.Start < held.Start);
            Assert.True(view.Range.End < held.End);
            Assert.InRange(view.Characters, 1, Budget + Piece);
            Assert.Equal(Slice(text, view.Range), box.Text);
            Assert.Equal(looking, box.GetLineText(box.GetFirstVisibleLineIndex()));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ASelectionStaysOnItsTextWhenTheTopIsLetGo() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, _) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);

            // A line near the end of what the box holds, so it outlasts
            // the pieces dropped above it.
            var line = box.GetLineIndexFromCharacterIndex(box.Text.Length - 200);
            box.Select(box.GetCharacterIndexFromLineIndex(line), box.GetLineLength(line));
            var selected = box.SelectedText;
            Assert.StartsWith("line 000", selected, StringComparison.Ordinal);

            box.ScrollToEnd();
            await Settle(view);
            Assert.True(view.Range.Start > 0);
            Assert.Equal(selected, box.SelectedText);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AFileThatGrewShowsItsNewLastLineAtEnd() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, _) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            view.GoToEnd();
            await Settle(view);
            Assert.EndsWith("line 0006499 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);

            File.AppendAllText(path, "written while it was open\r\n");
            view.GoToEnd();
            await Settle(view);

            Assert.EndsWith("line 0006499 the quick brown fox\r\nwritten while it was open\r\n", box.Text, StringComparison.Ordinal);
            var scroll = Scroll(box);
            Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 1, scroll.ScrollableHeight + 1);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AFileOfVeryWideLinesStaysWhereTheReaderIs() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();

        // Lines of 3,000 characters: the budget is thirteen of them, and
        // the window shows more than that at once.
        var (path, text) = Write(temp, WideLines(0, 600));
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);
            var scroll = Scroll(box);

            // At rest, at the top: the file's first line is still there,
            // and the box holds the screen and two more to scroll into.
            Assert.Equal(0, view.Range.Start);
            Assert.StartsWith("wide 0000000 ", box.Text, StringComparison.Ordinal);
            Assert.Equal(Slice(text, view.Range), box.Text);
            Assert.InRange(scroll.ExtentHeight, scroll.ViewportHeight * 3, scroll.ViewportHeight * 5);

            // Scrolled to the end of what it holds, it reads on and lets
            // go above. The line the reader is on stays where it is, they
            // are left in the middle of what is held, and that is never
            // much more than the three screens.
            for (var step = 0; step < 60 && view.Range.End < text.Length; step++)
            {
                box.ScrollToEnd();
                box.UpdateLayout();
                var looking = TopLine(box);
                await Settle(view);

                Assert.Equal(looking, TopLine(box));
                Assert.InRange(scroll.ExtentHeight, scroll.ViewportHeight * 2.5, scroll.ViewportHeight * 4);
                if (view.Range.End < text.Length)
                {
                    Assert.InRange(scroll.VerticalOffset, scroll.ViewportHeight * 0.5, scroll.ViewportHeight * 2);
                }
            }

            Assert.Equal(text.Length, view.Range.End);
            Assert.True(view.Range.Start > 0);
            Assert.Equal(Slice(text, view.Range), box.Text);

            // And back up, to its first.
            for (var step = 0; step < 60 && view.Range.Start > 0; step++)
            {
                box.ScrollToHome();
                box.UpdateLayout();
                var looking = TopLine(box);
                await Settle(view);

                Assert.Equal(looking, TopLine(box));
                Assert.InRange(scroll.ExtentHeight, scroll.ViewportHeight * 2.5, scroll.ViewportHeight * 4);
            }

            Assert.Equal(0, view.Range.Start);
            Assert.Equal(Slice(text, view.Range), box.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AReaderAtTheEdgeOfAFullBoxIsStillReadTo() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();

        // Two hundred ordinary lines, then wide ones. The box is full with
        // the ordinary lines still on screen above the wide ones, so there
        // is nothing off the screen to let go of.
        var (path, text) = Write(temp, Lines(0, 200) + WideLines(0, 120));
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);
            Assert.Equal(0, view.Range.Start);

            for (var step = 0; step < 60 && view.Range.End < text.Length; step++)
            {
                box.ScrollToEnd();
                await Settle(view);
            }

            Assert.Equal(text.Length, view.Range.End);
            Assert.Equal(Slice(text, view.Range), box.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AReaderAtTheTopEdgeOfAFullBoxIsStillReadTo() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();

        // The same the other way up: wide lines, then two hundred ordinary
        // ones that are still on screen below them when the box is full.
        var (path, text) = Write(temp, WideLines(0, 120) + Lines(0, 200));
        var (window, box, view) = Show(path);
        try
        {
            view.GoToEnd();
            await Settle(view);
            Assert.Equal(text.Length, view.Range.End);
            Assert.True(view.Range.Start > 0);

            for (var step = 0; step < 60 && view.Range.Start > 0; step++)
            {
                box.ScrollToHome();
                await Settle(view);
            }

            Assert.Equal(0, view.Range.Start);
            Assert.Equal(Slice(text, view.Range), box.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AVeryLongLineIsSaidAndNotLaidOut() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var before = Lines(0, 300);
        var after = Lines(300, 300);
        var (path, _) = Write(temp, before + new string('x', 100_000) + "\r\n" + after);
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);

            // Read forward, it stops in front of the line and says so.
            Assert.EndsWith("A very long line follows. Open the file for the rest.", box.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("xxxx", box.Text, StringComparison.Ordinal);
            Assert.InRange(view.Range.End, 1, before.Length);

            // From the end, the lines after it, and the same said above them.
            view.GoToEnd();
            await Settle(view);
            Assert.EndsWith("line 0000599 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);
            Assert.StartsWith("A very long line comes before this. Open the file to see it.\r\n", box.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("xxxx", box.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TextThatFitsIsShownWholeAndLongerTextComesWithItsReader()
    {
        using var temp = new TempDirectory();

        // Under the display limit: all of it, wrapped as a log is.
        var small = Path.Combine(temp.Path, "small.log");
        var text = Lines(0, 2000);
        File.WriteAllText(small, text);
        var whole = PreviewWindow.OpenTextDocument(small, TextPreviewLoader.TryLoad(small))!;
        Assert.Equal(text, whole.Text);
        Assert.True(whole.Wrap);
        Assert.Null(whole.Reader);

        // Over it: the first lines, unwrapped, and a reader for the rest.
        var large = Path.Combine(temp.Path, "large.log");
        var longer = Lines(0, 20_000);
        Assert.True(longer.Length > PreviewWindow.TextDisplayLimit);
        File.WriteAllText(large, longer);
        var first = PreviewWindow.OpenTextDocument(large, TextPreviewLoader.TryLoad(large))!;
        Assert.NotNull(first.Reader);
        Assert.False(first.Wrap);
        Assert.InRange(first.Text.Length, TextFileView.HeadBytes - 100, TextFileView.HeadBytes);
        Assert.StartsWith(first.Text, longer, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing the first", first.Text, StringComparison.Ordinal);

        // One line too long to lay out keeps the cut and its notice.
        var single = Path.Combine(temp.Path, "single.map");
        File.WriteAllText(single, new string('x', PreviewWindow.LongLineDisplayLimit + 10));
        var cut = PreviewWindow.OpenTextDocument(single, TextPreviewLoader.TryLoad(single))!;
        Assert.Null(cut.Reader);
        Assert.Contains("Showing the first", cut.Text, StringComparison.Ordinal);
    }

    [Fact]
    public Task InTheWindowALongLogIsReadThroughAndEndIsItsLastLine() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "service.log");
        var text = Lines(0, 90_000);
        File.WriteAllText(path, text);
        var dpi = (uint)Math.Round(PreviewWindow.ReadMonitor(IntPtr.Zero).Scale * 96);
        var window = new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(ParkedPixels, dpi) };
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Fields.Get<bool>(window, "_loadFinished")
                && window.PreviewContent.Content is TextBox
                && Fields.Get<object?>(window, "_textFile") is TextFileView);
            var box = (TextBox)window.PreviewContent.Content;
            var view = (TextFileView)Fields.Get<object?>(window, "_textFile")!;
            Assert.Equal(TextWrapping.NoWrap, box.TextWrapping);

            // Three megabytes: at rest the box holds its budget from the
            // start, and not the file.
            await Settle(view);
            Assert.Equal(0, view.Range.Start);
            Assert.InRange(view.Characters, TextFileView.HeldCharacters, TextFileView.HeldCharacters + TextFileView.PieceBytes);
            Assert.Equal(Slice(text, view.Range), box.Text);

            // Home, End and the page keys are the window's on a text file,
            // and End is the file's last line, which the box did not hold.
            Assert.True(window.ScrollTextByKey(Key.End, ModifierKeys.None));
            await Settle(view);
            Assert.EndsWith("line 0089999 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);
            Assert.Equal(text.Length, view.Range.End);
            var scroll = Scroll(box);
            Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 1, scroll.ScrollableHeight + 1);
            Assert.True(window.ScrollTextByKey(Key.PageUp, ModifierKeys.None));
            box.UpdateLayout();
            Assert.True(scroll.VerticalOffset < scroll.ScrollableHeight - 100);
            Assert.True(window.ScrollTextByKey(Key.Home, ModifierKeys.Control));
            await Settle(view);
            Assert.Equal(0, scroll.VerticalOffset);
            Assert.StartsWith("line 0000000 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);
            Assert.True(window.ScrollTextByKey(Key.PageDown, ModifierKeys.None));
            box.UpdateLayout();
            Assert.True(scroll.VerticalOffset > 100);

            // With Shift they select, and stay the text box's own.
            Assert.False(window.ScrollTextByKey(Key.End, ModifierKeys.Shift));
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public Task InTheWindowAShortLogIsWhatItAlwaysWas() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "short.log");
        var text = Lines(0, 1000);
        File.WriteAllText(path, text);
        var dpi = (uint)Math.Round(PreviewWindow.ReadMonitor(IntPtr.Zero).Scale * 96);
        var window = new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(ParkedPixels, dpi) };
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Fields.Get<bool>(window, "_loadFinished") && window.PreviewContent.Content is TextBox);
            var box = (TextBox)window.PreviewContent.Content;

            Assert.Equal(text, box.Text);
            Assert.Equal(TextWrapping.Wrap, box.TextWrapping);
            Assert.Null(Fields.Get<object?>(window, "_textFile"));

            // End and Home still scroll it.
            Assert.True(window.ScrollTextByKey(Key.End, ModifierKeys.None));
            box.UpdateLayout();
            var scroll = Scroll(box);
            Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 1, scroll.ScrollableHeight + 1);
            Assert.True(window.ScrollTextByKey(Key.Home, ModifierKeys.None));
            box.UpdateLayout();
            Assert.Equal(0, scroll.VerticalOffset);
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    private static string Lines(int first, int count)
    {
        var text = new StringBuilder(count * 36);
        for (var i = first; i < first + count; i++)
        {
            text.Append("line ").Append(i.ToString("0000000")).Append(" the quick brown fox\r\n");
        }

        return text.ToString();
    }

    private static string WideLines(int first, int count)
    {
        var text = new StringBuilder(count * 3000);
        for (var i = first; i < first + count; i++)
        {
            text.Append("wide ").Append(i.ToString("0000000")).Append(' ').Append('w', 2985).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>How the line at the top of the box begins.</summary>
    private static string TopLine(TextBox box)
    {
        var line = box.GetLineText(box.GetFirstVisibleLineIndex());
        return line[..Math.Min(line.Length, 12)];
    }

    private static (string Path, string Text) Write(TempDirectory temp, string text)
    {
        var path = Path.Combine(temp.Path, "long.log");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return (path, text);
    }

    // The files here are ASCII, so a byte of the file is a character of it.
    private static string Slice(string text, (long Start, long End) range) =>
        text[(int)range.Start..(int)range.End];

    /// <summary>The view in a parked window of its own, with small pieces and a small budget.</summary>
    private static (Window Window, TextBox Box, TextFileView View) Show(string path)
    {
        var reader = TextFileReader.TryOpen(path)!;
        var head = TextFileView.TryReadHead(reader, Head)!;
        var box = new TextBox
        {
            Text = head.Text,
            IsReadOnly = true,
            IsUndoEnabled = false,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13
        };
        var view = new TextFileView(box, reader, head, Head, Piece, Budget);
        var window = new Window
        {
            Content = box,
            Left = ParkedPixels.Left,
            Top = 0,
            Width = 900,
            Height = 600,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        window.Show();
        return (window, box, view);
    }

    private static ScrollViewer Scroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer found)
            {
                return found;
            }

            try
            {
                return Scroll(child);
            }
            catch (InvalidOperationException)
            {
                // Not under this child.
            }
        }

        throw new InvalidOperationException("no scroll viewer");
    }

    /// <summary>
    /// Waits until the view has come to rest: nothing under way and nothing
    /// it holds changing, through ten idle passes of the dispatcher. It
    /// fills a piece at a time with the thread free in between, so one
    /// quiet moment is not rest.
    /// </summary>
    private static async Task Settle(TextFileView view)
    {
        var quiet = 0;
        var held = (view.Characters, view.Range);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (quiet < 10)
        {
            Assert.True(DateTime.UtcNow < deadline, "The text view never came to rest.");
            await view.Box.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(15);
            var now = (view.Characters, view.Range);
            quiet = view.Busy || now != held ? 0 : quiet + 1;
            held = now;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the window.");
            await Task.Delay(10);
        }
    }
}
