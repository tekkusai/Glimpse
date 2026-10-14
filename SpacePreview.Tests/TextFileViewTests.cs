using System.Text;
using System.Text.RegularExpressions;
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

    // Three screens of the widest lines here are a third of this, as they
    // are a third of the real budget at the real sizes.
    private const long Span = 1024 * 1024;

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

    /// <summary>
    /// End and Home take the caret with the reader, as an editor does: an
    /// arrow key after End goes on from the last line, not from wherever
    /// the caret was left. Here the box holds the whole file, so neither
    /// jump reads anything.
    /// </summary>
    [Fact]
    public Task EndAndHomeTakeTheCaretWithThem() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, text) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path, characters: 1_000_000);
        try
        {
            await Settle(view);
            Assert.Equal(text.Length, view.Characters);
            box.CaretIndex = 5000;

            view.GoToStart();
            await Settle(view);
            Assert.Equal(0, box.CaretIndex);

            view.GoToEnd();
            await Settle(view);
            Assert.Equal(view.Characters, box.CaretIndex);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// A box with the keyboard scrolls to its caret once the caret has
    /// moved, and every change here moves the caret with the text it
    /// stands in. That scroll comes after the change has put the reader
    /// back, and is taken back in turn, until the dispatcher is idle. The
    /// reader's own scrolls are never taken back. The scrolls here stand
    /// in for the box's: a test cannot give a window the keyboard without
    /// taking it from the person at the PC.
    /// </summary>
    [Fact]
    public Task AScrollTheBoxMakesOfItselfAfterAChangeIsTakenBack() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, _) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);
            var scroll = Scroll(box);
            var place = scroll.VerticalOffset;
            void Change() => Fields.Call(view, "KeepingPlace", true, (Action)(() => { }));
            void ScrollTo(double offset)
            {
                scroll.ScrollToVerticalOffset(offset);
                box.UpdateLayout();
                box.UpdateLayout();
            }

            Change();
            ScrollTo(place + 300);
            Assert.Equal(place, scroll.VerticalOffset, 0.5);

            // The wheel is the reader's.
            Change();
            Wheel(box, down: true);
            ScrollTo(place + 300);
            Assert.Equal(place + 300, scroll.VerticalOffset, 0.5);

            // So is a press, on the text or on the bar.
            Change();
            box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            ScrollTo(place + 600);
            Assert.Equal(place + 600, scroll.VerticalOffset, 0.5);

            // And any key.
            Change();
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            ScrollTo(place + 900);
            Assert.Equal(place + 900, scroll.VerticalOffset, 0.5);

            // A change made while the reader holds the mouse on the bar or
            // a selection holds nothing: they are moving the box.
            Assert.True(box.CaptureMouse());
            try
            {
                Change();
                ScrollTo(place + 1200);
                Assert.Equal(place + 1200, scroll.VerticalOffset, 0.5);
            }
            finally
            {
                box.ReleaseMouseCapture();
            }

            // And nothing is held once the dispatcher has been idle.
            Change();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ScrollTo(place + 1500);
            Assert.Equal(place + 1500, scroll.VerticalOffset, 0.5);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// End and Home are the reader's own moves. A place held for a change
    /// a moment before never holds them back, whether they read the file
    /// or not.
    /// </summary>
    [Fact]
    public Task EndAndHomeAreNotHeldBackByAChangeJustBefore() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, _) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);
            var scroll = Scroll(box);
            void Change() => Fields.Call(view, "KeepingPlace", true, (Action)(() => { }));
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
            box.UpdateLayout();

            Change();
            view.GoToEnd();
            await Landed(view);
            box.UpdateLayout();
            Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 1, scroll.ScrollableHeight + 1);

            Change();
            view.GoToStart();
            await Landed(view);
            box.UpdateLayout();
            Assert.Equal(0, scroll.VerticalOffset);

            // With the start already held, Home reads nothing.
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
            box.UpdateLayout();
            Change();
            view.GoToStart();
            await Landed(view);
            box.UpdateLayout();
            Assert.Equal(0, scroll.VerticalOffset);
        }
        finally
        {
            window.Close();
        }

        // Until the jump is done, and not one idle turn of the dispatcher
        // later: a turn at this priority always waits, so idle never comes.
        static async Task Landed(TextFileView view)
        {
            while (view.Busy)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
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
    public Task AFileThatGoesAwayIsNotAskedAgainAndAgain() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, _) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            view.GoToEnd();
            await Settle(view);
            Assert.True(view.Range.Start > 0);
            var held = box.Text;

            // Deleted with its last lines on screen, and the reader goes
            // back up to the top of what the box holds.
            File.Delete(path);
            box.ScrollToHome();
            await Settle(view);

            // There is nothing to read, so nothing is: the box keeps what
            // it had, and the view is at rest and not asking in a loop.
            Assert.Equal(held, box.Text);
            var asked = view.Turns;
            await Task.Delay(300);
            Assert.Equal(asked, view.Turns);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// The file may be on a share that has stopped answering, and a
    /// question put to it on the window's thread stops the window with it.
    /// Every use of the reader in the view is the one number it already
    /// has, or sits inside a Task.Run.
    /// </summary>
    [Fact]
    public void TheViewAsksTheFileNothingOnTheWindowsThread()
    {
        var source = TestSource.Read(@"SpacePreview\Preview\TextFileView.cs");
        var background = new List<(int Start, int End)>();
        for (var at = source.IndexOf("Task.Run(", StringComparison.Ordinal); at >= 0; at = source.IndexOf("Task.Run(", at + 1, StringComparison.Ordinal))
        {
            var depth = 0;
            var end = at + "Task.Run".Length;
            do
            {
                depth += source[end] == '(' ? 1 : source[end] == ')' ? -1 : 0;
                end++;
            }
            while (depth > 0);
            background.Add((at, end));
        }

        var asked = Regex.Matches(source, @"_reader\.(?!First\b)\w+");
        Assert.NotEmpty(background);
        Assert.NotEmpty(asked);
        Assert.All(asked, use => Assert.True(
            background.Any(run => use.Index > run.Start && use.Index < run.End),
            $"{use.Value} on line {source[..use.Index].Count(c => c == (char)10) + 1} is asked on the window's thread"));
    }

    [Fact]
    public Task AVeryLongLineIsSteppedOver() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var before = Lines(0, 300);
        var after = Lines(300, 300);
        var (path, text) = Write(temp, before + new string('x', 100_000) + "\r\n" + after);
        var shown = before
            + new string('x', TextFileReader.ShownOfALongLine) + "\r\n"
            + TextFileReader.LineGoesOn(100_002 - TextFileReader.ShownOfALongLine) + "\r\n"
            + after;
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);

            // Read forward: the lines before it, the first of it, how much
            // more of it there is, and the lines after it to the file's end.
            Assert.Equal(shown, box.Text);
            Assert.Equal((0L, (long)text.Length), view.Range);

            // And the same read back from the end.
            view.GoToEnd();
            await Settle(view);
            Assert.EndsWith("line 0000599 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);
            Assert.Equal(shown, box.Text);
            Assert.Equal((0L, (long)text.Length), view.Range);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(1024 * 1024)]
    [InlineData(100 * 1024)]
    public Task AFileOfVeryLongLinesIsReadThroughToItsLast(int span) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();

        // Three hundred records of 40,000 characters, as a file of JSON
        // lines can be. The box may span a megabyte of the file, which is
        // more than a screen of them, or a tenth of that, which is less
        // than one.
        var records = new StringBuilder();
        for (var i = 0; i < 300; i++)
        {
            records.Append("record ").Append(i.ToString("0000000")).Append(' ').Append('j', 40_000).Append("\r\n");
        }

        var (path, text) = Write(temp, records.ToString());
        var (window, box, view) = Show(path, span: span);
        try
        {
            await Settle(view);
            Assert.Equal(0, view.Range.Start);
            Assert.StartsWith("record 0000000 jjjj", box.Text, StringComparison.Ordinal);

            // Left at the top, it has read its span of the file and
            // stopped, whether that fills the window or not. Nothing a
            // file holds makes it read more than that with the reader at
            // rest.
            const int record = 40_017;
            var scroll = Scroll(box);
            var fills = span >= 1024 * 1024;
            Assert.InRange(view.Range.End, 1, span + (2 * record));
            Assert.Equal(fills, scroll.ExtentHeight >= scroll.ViewportHeight);

            // What reads on from there is the reader: the wheel, turned
            // at the end of what the box holds, brings more each time.
            // Once there is a screen of it, what is behind is let go.
            var rows = (int)(scroll.ViewportHeight / (scroll.ExtentHeight / box.LineCount)) + 1;
            var most = Math.Max(span, ((rows / 2) + 2) * record) + (4 * record);
            for (var step = 0; step < 12; step++)
            {
                var held = view.Range.End;
                box.ScrollToEnd();
                Wheel(box, down: true);
                await Settle(view);
                Assert.InRange(view.Range.End, held + record, text.Length);
                Assert.InRange(view.Range.End - view.Range.Start, 1, most);
            }

            // A page key says the same as the wheel, to the box when it
            // has the keyboard and to the window when it has not.
            for (var step = 0; step < 3; step++)
            {
                var held = view.Range.End;
                box.ScrollToEnd();
                box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.PageDown)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                });
                await Settle(view);
                Assert.InRange(view.Range.End, held + record, text.Length);
            }

            var keys = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.Text.cs", "ScrollTextByKey");
            Assert.Contains("box.PageDown();\r\n                file?.ReaderMoved(1);", keys, StringComparison.Ordinal);
            Assert.Contains("box.PageUp();\r\n                file?.ReaderMoved(-1);", keys, StringComparison.Ordinal);

            // End is the last record, read back for a span and no more.
            // Every record is two lines: its first characters, and how
            // much more of it there is.
            view.GoToEnd();
            await Settle(view);
            Assert.Equal(text.Length, view.Range.End);
            Assert.InRange(view.Range.End - view.Range.Start, 1, span + (2 * record));
            Assert.Equal(fills, scroll.ExtentHeight >= scroll.ViewportHeight);

            // And the wheel turned the other way reads back from there.
            var first = view.Range.Start;
            box.ScrollToHome();
            Wheel(box, down: false);
            await Settle(view);
            Assert.InRange(view.Range.Start, 0, first - record);
            view.GoToEnd();
            await Settle(view);
            var lines = box.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.StartsWith("record 0000299 jjjj", lines[^2], StringComparison.Ordinal);
            Assert.Equal(TextFileReader.ShownOfALongLine, lines[^2].Length);
            Assert.Equal(TextFileReader.LineGoesOn(40_017 - TextFileReader.ShownOfALongLine), lines[^1]);
            Assert.All(lines, line => Assert.InRange(line.Length, 1, TextFileReader.ShownOfALongLine));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task LinesThatOutgrowTheSpanAreReadToTheLastByTheWheelAlone() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();

        // Forty records of 40,000 characters and a span of two and a half
        // of them: the window is never full, there is never anything to
        // scroll, and every record is still reached, one turn of the
        // wheel after another.
        var records = new StringBuilder();
        for (var i = 0; i < 40; i++)
        {
            records.Append("record ").Append(i.ToString("0000000")).Append(' ').Append('j', 40_000).Append("\r\n");
        }

        var (path, text) = Write(temp, records.ToString());
        var (window, box, view) = Show(path, span: 100 * 1024);
        try
        {
            await Settle(view);
            var seen = new HashSet<string>();
            for (var step = 0; step < 120 && view.Range.End < text.Length; step++)
            {
                seen.UnionWith(box.Text.Split("\r\n").Where(line => line.StartsWith("record ", StringComparison.Ordinal)).Select(line => line[..14]));
                box.ScrollToEnd();
                Wheel(box, down: true);
                await Settle(view);
            }

            seen.UnionWith(box.Text.Split("\r\n").Where(line => line.StartsWith("record ", StringComparison.Ordinal)).Select(line => line[..14]));
            Assert.Equal(text.Length, view.Range.End);
            Assert.Equal(Enumerable.Range(0, 40).Select(i => $"record {i:0000000}"), seen.Order());
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task EndHeldDownPutsOneQuestionToTheFileAtATime() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var (path, _) = Write(temp, Lines(0, 6500));
        var (window, box, view) = Show(path);
        try
        {
            await Settle(view);
            var asked = view.Turns;

            // Thirty repeats of End, as a held key sends them, before the
            // first has its answer. On a share that has stopped answering
            // no answer comes, and each question put to it is a thread
            // that waits: so one is put, and the rest wait for it.
            for (var i = 0; i < 30; i++)
            {
                view.GoToEnd();
            }

            Assert.Equal(asked + 1, view.Turns);

            // What is asked for last is what happens next.
            view.GoToStart();
            Assert.Equal(asked + 1, view.Turns);
            await Settle(view);
            Assert.Equal(0, view.Range.Start);
            Assert.Equal(0, Scroll(box).VerticalOffset);
            Assert.StartsWith("line 0000000 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);

            // And End alone, however long it is held, is the last line.
            for (var i = 0; i < 30; i++)
            {
                view.GoToEnd();
            }

            await Settle(view);
            Assert.EndsWith("line 0006499 the quick brown fox\r\n", box.Text, StringComparison.Ordinal);
            var scroll = Scroll(box);
            Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 1, scroll.ScrollableHeight + 1);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AFileOfLongLinesIsReadNoFurtherAheadThanItsBudgetOfTheFile() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();

        // Forty megabytes in four hundred lines, at the sizes the window
        // uses. Each line is two thousand characters in the box, so the
        // budget of characters is never reached: all four hundred are
        // under half of it.
        var path = Path.Combine(temp.Path, "records.log");
        var line = Encoding.ASCII.GetBytes(new string('j', 100_000 - 1) + "\n");
        using (var file = File.Create(path))
        {
            for (var i = 0; i < 400; i++)
            {
                file.Write(line);
            }
        }

        // The box may span sixteen megabytes of it: under half the file.
        const long span = 16 * 1024 * 1024;
        var (window, box, view) = Show(path, TextFileView.HeadBytes, TextFileView.PieceBytes, TextFileView.HeldCharacters, span);
        try
        {
            // Left at its first line, it reads its budget of the file
            // and stops, with screens of lines in hand below the reader.
            await Settle(view);
            var scroll = Scroll(box);
            Assert.Equal(0, view.Range.Start);
            Assert.InRange(view.Range.End, span, span + 200_000);
            Assert.InRange(scroll.ExtentHeight, scroll.ViewportHeight * 3, double.MaxValue);

            // Scrolled, it reads on and lets go behind, and never spans
            // more of the file than that.
            for (var step = 0; step < 60 && view.Range.End < 40_000_000; step++)
            {
                box.ScrollToEnd();
                await Settle(view);
                Assert.InRange(view.Range.End - view.Range.Start, 1, span + 400_000);
            }

            Assert.Equal(40_000_000, view.Range.End);
            Assert.EndsWith(TextFileReader.LineGoesOn(100_000 - TextFileReader.ShownOfALongLine) + "\r\n", box.Text, StringComparison.Ordinal);
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

        // A file that is one long line keeps the cut and its notice, as
        // it always did: the cut shows more of it, and there is nothing
        // after it to read on to.
        foreach (var length in new[] { PreviewWindow.LongLineDisplayLimit + 10, 600_000 })
        {
            var single = Path.Combine(temp.Path, $"single{length}.map");
            File.WriteAllText(single, new string('x', length));
            var cut = PreviewWindow.OpenTextDocument(single, TextPreviewLoader.TryLoad(single))!;
            Assert.Null(cut.Reader);
            Assert.StartsWith(new string('x', PreviewWindow.LongLineDisplayLimit) + "\n\nShowing the first", cut.Text, StringComparison.Ordinal);
        }

        // A long line with lines after it is read a piece at a time, like
        // any other file, wherever the line is.
        var record = Path.Combine(temp.Path, "records.log");
        File.WriteAllText(record, new string('x', 300_000) + "\r\n" + longer);
        var stepped = PreviewWindow.OpenTextDocument(record, TextPreviewLoader.TryLoad(record))!;
        Assert.NotNull(stepped.Reader);
        Assert.Equal(
            new string('x', TextFileReader.ShownOfALongLine) + "\r\n" + TextFileReader.LineGoesOn(300_002 - TextFileReader.ShownOfALongLine) + "\r\n",
            stepped.Text);

        // A file that opens on a line with no end within the reader's
        // reach keeps the cut too. Read on, it would cost the reach for
        // every two lines on screen and show a sample of one line each time.
        var endless = Path.Combine(temp.Path, "endless.log");
        using (var file = File.Create(endless))
        {
            file.Write(Encoding.ASCII.GetBytes(new string('x', TextFileReader.FarthestLineEnd + 1_000_000)));
            file.Write(Encoding.ASCII.GetBytes("\r\n" + longer));
        }

        var kept = PreviewWindow.OpenTextDocument(endless, TextPreviewLoader.TryLoad(endless))!;
        Assert.Null(kept.Reader);
        Assert.StartsWith(new string('x', PreviewWindow.LongLineDisplayLimit) + "\n\nShowing the first", kept.Text, StringComparison.Ordinal);
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

    /// <summary>One notch of the wheel over the box, as the window is told of it.</summary>
    private static void Wheel(TextBox box, bool down) =>
        box.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, down ? -120 : 120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        });

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
    private static (Window Window, TextBox Box, TextFileView View) Show(
        string path,
        int headBytes = Head,
        int pieceBytes = Piece,
        int characters = Budget,
        long span = Span)
    {
        var reader = TextFileReader.TryOpen(path)!;
        var head = TextFileView.TryReadHead(reader, headBytes)!;
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
        var view = new TextFileView(box, reader, head, headBytes, pieceBytes, characters, span);
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
