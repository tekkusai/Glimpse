using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// A text file too long to lay out whole, read in the same text box a short
/// one gets. The box holds a run of the file around where the reader is.
/// It opens on the file's first lines, takes more while nothing else wants
/// the thread, and takes more again whenever the reader comes near either
/// end of what it holds. Past its budget it lets go of the far end, so a
/// file of any size is read through at the cost of what is on screen. End
/// goes to the file's real last line and Home back to its first. What a
/// very long line looks like is the reader's business: here every piece is
/// lines of text.
///
/// Two rules bound what the file can cost. With the reader at rest the box
/// reads its budget of the file and stops, whether or not that fills the
/// window: what reads on from there is the reader, by the wheel, a key or
/// the scroll bar. And the file may be on a share that has stopped
/// answering, so nothing here asks it anything on the window's thread
/// (every question goes through Task.Run), and one question is out at a
/// time: a second waits for the first to come back.
/// </summary>
internal sealed class TextFileView
{
    /// <summary>What the box opens on: about 50 ms of layout, where the cut it replaces took 150.</summary>
    internal const int HeadBytes = 128 * 1024;

    /// <summary>What is added or let go at a time: about a frame's worth of layout.</summary>
    internal const int PieceBytes = 64 * 1024;

    /// <summary>
    /// The most the box holds. The text box lays out only the lines it is
    /// given, so this is a matter of memory and of how long select-all
    /// takes, not of how the file scrolls. Three screens of lines are held
    /// whatever they come to: lines of 30,000 characters fill a screen with
    /// more than this.
    /// </summary>
    internal const int HeldCharacters = 2_000_000;

    /// <summary>
    /// The most of the file the box spans, in the file's bytes. Ordinary
    /// text never comes near it: two million characters are two to six
    /// megabytes. It is what bounds a file of very long lines, where a
    /// line is two thousand characters in the box and megabytes of the
    /// file to read past. This is all the box reads with the reader at
    /// rest, the screen's own lines included. It fills a tall window with
    /// lines of a megabyte each, as a database dump has. Of longer lines
    /// it shows as many as this buys, and the reader scrolls for more.
    /// </summary>
    internal const long HeldBytes = 64 * 1024 * 1024;

    /// <summary>
    /// How near an end of what the box holds the reader comes before it
    /// reads on, in screens. The box holds this many screens of lines
    /// whatever the budget says.
    /// </summary>
    private const int Screens = 3;

    private static readonly TimeSpan EndRetry = TimeSpan.FromSeconds(1);

    private readonly record struct Held(long Start, long End, int Characters);

    private readonly TextBox _box;
    private readonly TextFileReader _reader;
    private readonly int _headBytes;
    private readonly int _pieceBytes;
    private readonly int _heldCharacters;
    private readonly long _heldBytes;
    private readonly LinkedList<Held> _held = new();
    private ScrollViewer? _scroll;
    private int _characters;
    private int _turn;
    private bool _busy;
    private bool _left;

    // The jump asked for while a read was out: to the end, or to the
    // start. Only the newest waits.
    private bool? _jump;

    // Which way the reader last moved, until a look has taken note of it:
    // below zero toward the start, above toward the end.
    private int _moved;
    private long? _nothingAfterAt;
    private long? _nothingBeforeAt;

    public TextFileView(
        TextBox box,
        TextFileReader reader,
        TextPiece head,
        int headBytes = HeadBytes,
        int pieceBytes = PieceBytes,
        int heldCharacters = HeldCharacters,
        long heldBytes = HeldBytes)
    {
        _box = box;
        _reader = reader;
        _headBytes = headBytes;
        _pieceBytes = pieceBytes;
        _heldCharacters = heldCharacters;
        _heldBytes = heldBytes;
        Hold(head);

        // Staged content joins the window collapsed, and a collapsed box
        // has no scroll viewer yet. Every layout is watched until it has
        // one with a size: with none, nothing would ever say where the
        // reader is, and the box would stay at its first lines.
        box.Loaded += (_, _) =>
        {
            _left = false;
            box.LayoutUpdated -= OnLayout;
            box.LayoutUpdated += OnLayout;

            // A box already laid out has no layout coming to say so.
            box.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => OnLayout(null, EventArgs.Empty));
        };
        box.Unloaded += (_, _) =>
        {
            _left = true;
            _jump = null;
            box.LayoutUpdated -= OnLayout;
        };

        // The wheel and the keys say where the reader wants to go even
        // when there is nowhere left to scroll, which is exactly when it
        // matters: at the edge of what the box holds.
        box.PreviewMouseWheel += (_, e) => ReaderMoved(e.Delta < 0 ? 1 : -1);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.PageDown or Key.Up or Key.PageUp)
            {
                ReaderMoved(e.Key is Key.Down or Key.PageDown ? 1 : -1);
            }
        };
    }

    /// <summary>The bytes of the file the box holds now.</summary>
    internal (long Start, long End) Range => (_held.First!.Value.Start, _held.Last!.Value.End);

    /// <summary>How many characters the box holds now.</summary>
    internal int Characters => _characters;

    /// <summary>True while a read or a jump is under way.</summary>
    internal bool Busy => _busy;

    /// <summary>How many reads and jumps there have been.</summary>
    internal int Turns => _turn;

    /// <summary>The text box this reads into.</summary>
    internal TextBox Box => _box;

    /// <summary>
    /// The opening text for a file read this way: its first lines. Null
    /// when they are the whole file, or when the file opens on a line with
    /// no end within the reader's reach. Of a file that is one long line
    /// the loader's cut shows more, and reading it on a run at a time
    /// would cost the reach for every two lines on screen.
    /// </summary>
    public static TextPiece? TryReadHead(TextFileReader reader, int headBytes = HeadBytes)
    {
        var head = reader.ReadAfter(reader.First, headBytes);
        return head.Text.Length == 0 || head.StopsInALine || head.End >= reader.Length ? null : head;
    }

    /// <summary>To the file's last line, as the file is now.</summary>
    public void GoToEnd() => Jump(toEnd: true);

    /// <summary>Back to the file's first line.</summary>
    public void GoToStart() => Jump(toEnd: false);

    /// <summary>
    /// The reader has moved, or tried to: toward the end when
    /// <paramref name="direction"/> is above zero, toward the start below.
    /// </summary>
    public void ReaderMoved(int direction)
    {
        _moved = direction;
        Look();
    }

    /// <summary>
    /// One question is put to the file at a time. A jump asked for while a
    /// read is out waits for it, and only the newest one waits. A key held
    /// down on a share that has stopped answering leaves one read out
    /// there, not one for every repeat of the key.
    /// </summary>
    private void Jump(bool toEnd)
    {
        if (_busy)
        {
            _jump = toEnd;
        }
        else if (toEnd)
        {
            ToEnd();
        }
        else
        {
            ToStart();
        }
    }

    private async void ToEnd()
    {
        Begin();
        try
        {
            // Far from the end, the box lets go of what it holds and takes
            // the file's last lines. Near it there is no tail to take: the
            // box reads on from where it is.
            var held = Range.End;
            var tail = await Task.Run(() => _reader.Length - held <= _headBytes ? null : _reader.ReadBefore(_reader.Length, _headBytes));
            if (_left)
            {
                return;
            }

            var near = tail is null or { Text.Length: > 0 };
            if (tail is { Text.Length: > 0 })
            {
                _box.Text = tail.Text;
                Hold(tail);
            }

            // The last of a file whose end the box had nearly reached, and
            // whatever was written since. A few pieces, never the file.
            for (var pieces = 0; near && pieces < 8; pieces++)
            {
                var end = Range.End;
                var more = await Task.Run(() => _reader.ReadAfter(end, _pieceBytes));
                if (_left)
                {
                    return;
                }

                if (more.Text.Length == 0)
                {
                    break;
                }

                TakeAfter(more);
            }

            _box.UpdateLayout();
            _box.ScrollToEnd();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"text end failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Finish();
        }
    }

    private async void ToStart()
    {
        Begin();
        try
        {
            if (Range.Start > _reader.First)
            {
                var head = await Task.Run(() => _reader.ReadAfter(_reader.First, _headBytes));
                if (_left)
                {
                    return;
                }

                if (head.Text.Length > 0)
                {
                    _box.Text = head.Text;
                    Hold(head);
                }
            }

            _box.UpdateLayout();
            _box.ScrollToHome();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"text start failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Finish();
        }
    }

    private void OnLayout(object? sender, EventArgs e)
    {
        if (FindScroll(_box) is not { ViewportHeight: > 0 } found)
        {
            return;
        }

        _box.LayoutUpdated -= OnLayout;
        if (!ReferenceEquals(found, _scroll))
        {
            if (_scroll is not null)
            {
                _scroll.ScrollChanged -= OnScrolled;
            }

            _scroll = found;
            found.ScrollChanged += OnScrolled;
        }

        LookLater(DispatcherPriority.ApplicationIdle);
    }

    private void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        // The box's own growing and shrinking scroll it too. Only where the
        // reader has gone is a reason to read, and a window made taller
        // is room to fill, not a move.
        if (e.VerticalChange != 0)
        {
            ReaderMoved(e.VerticalChange > 0 ? 1 : -1);
        }
        else if (e.ViewportHeightChange != 0)
        {
            Look();
        }
    }

    private void LookLater(DispatcherPriority priority) =>
        _box.Dispatcher.BeginInvoke(priority, Look);

    /// <summary>
    /// Reads toward the reader when an end of what the box holds is within
    /// a few screens. Otherwise, with room in the budget, it fills on from
    /// the end the box is not anchored to, a piece at a time while idle.
    /// A full box takes more at one end only by letting go at the other,
    /// and only of what the reader is further from (see MayDrop). A reader
    /// who moves toward the very edge of what a full box holds is read to
    /// all the same, a piece for each move. So what the box reads with the
    /// reader at rest is its budget, and what it reads past that is what
    /// the reader went to.
    /// </summary>
    private void Look()
    {
        // A box that has no size yet has nowhere the reader could be. A
        // move made while a read is out is kept for the look after it.
        if (_scroll is null || _busy || _left || _scroll.ViewportHeight <= 0)
        {
            return;
        }

        var moved = _moved;
        _moved = 0;
        var screens = _scroll.ViewportHeight * Screens;
        var above = _scroll.VerticalOffset;
        var below = _scroll.ExtentHeight - above - _scroll.ViewportHeight;
        var full = Range.End - Range.Start >= _heldBytes
            || (_characters >= _heldCharacters && _scroll.ExtentHeight >= screens);
        if (below <= screens && MayReadAfter && (!full || (moved > 0 && below < 1) || MayDrop(first: true)))
        {
            Extend(after: true);
        }
        else if (above <= screens && MayReadBefore && (!full || (moved < 0 && above < 1) || MayDrop(first: false)))
        {
            Extend(after: false);
        }
        else if (!full && (MayReadAfter || MayReadBefore))
        {
            Extend(after: MayReadAfter);
        }
    }

    /// <summary>
    /// Whether the piece at one end may be let go: it is off the screen,
    /// and with it gone that end is still further from the reader than the
    /// other. That is what keeps a box of very wide lines still. Three
    /// screens each way do not fit the budget there, and without this a
    /// read at one end would drop the lines on screen at the other, and
    /// the next read would bring them back.
    /// </summary>
    private bool MayDrop(bool first)
    {
        if (_scroll is null || _held.Count <= 2 || _box.LineCount <= 0)
        {
            return false;
        }

        // Every line of the box is as tall as the next.
        var lines = first
            ? _box.GetLineIndexFromCharacterIndex(_held.First!.Value.Characters)
            : _box.LineCount - _box.GetLineIndexFromCharacterIndex(_characters - _held.Last!.Value.Characters);
        var height = _scroll.ExtentHeight * lines / _box.LineCount;
        var above = _scroll.VerticalOffset;
        var below = Math.Max(0, _scroll.ExtentHeight - above - _scroll.ViewportHeight);
        return first ? above - height > below : below - height > above;
    }

    /// <summary>
    /// More of the file than the box may span, or more characters than its
    /// budget and more than the screens it holds whatever they come to.
    /// </summary>
    private bool Over =>
        Range.End - Range.Start > _heldBytes
        || (_characters > _heldCharacters
            && _scroll is { } scroll
            && scroll.ExtentHeight > scroll.ViewportHeight * Screens);

    private bool MayReadAfter => Waited(_nothingAfterAt);

    private bool MayReadBefore => Range.Start > _reader.First && Waited(_nothingBeforeAt);

    /// <summary>A read that came back empty is not asked for again within the second.</summary>
    private static bool Waited(long? since) =>
        since is not { } at || Environment.TickCount64 - at >= EndRetry.TotalMilliseconds;

    private async void Extend(bool after)
    {
        Begin();
        try
        {
            var (start, end) = Range;
            var piece = await Task.Run(() => after ? _reader.ReadAfter(end, _pieceBytes) : _reader.ReadBefore(start, _pieceBytes));
            if (_left)
            {
                return;
            }

            if (piece.Text.Length == 0)
            {
                // The end of the file, or a file that has gone away. A log
                // may grow and a drive may come back: ask again later, not
                // at every notch of the wheel and not in a loop.
                if (after)
                {
                    _nothingAfterAt = Environment.TickCount64;
                }
                else
                {
                    _nothingBeforeAt = Environment.TickCount64;
                }

                return;
            }

            if (after)
            {
                TakeAfter(piece);
                while (Over && MayDrop(first: true))
                {
                    KeepingPlace(above: true, DropFirst);
                }
            }
            else
            {
                TakeBefore(piece);
                while (Over && MayDrop(first: false))
                {
                    KeepingPlace(above: false, DropLast);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"text read failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Finish();
        }
    }

    private void TakeAfter(TextPiece piece)
    {
        KeepingPlace(above: false, () => _box.AppendText(piece.Text));
        _held.AddLast(new Held(piece.Start, piece.End, piece.Text.Length));
        _characters += piece.Text.Length;
    }

    private void TakeBefore(TextPiece piece)
    {
        KeepingPlace(above: true, () => Replace(0, 0, piece.Text));
        _held.AddFirst(new Held(piece.Start, piece.End, piece.Text.Length));
        _characters += piece.Text.Length;
    }

    private void DropFirst()
    {
        var first = _held.First!.Value;
        Replace(0, first.Characters, string.Empty);
        _held.RemoveFirst();
        _characters -= first.Characters;
    }

    private void DropLast()
    {
        var last = _held.Last!.Value;
        Replace(_characters - last.Characters, last.Characters, string.Empty);
        _held.RemoveLast();
        _characters -= last.Characters;
    }

    /// <summary>The box holds this piece and nothing else.</summary>
    private void Hold(TextPiece piece)
    {
        _held.Clear();
        _held.AddLast(new Held(piece.Start, piece.End, piece.Text.Length));
        _characters = piece.Text.Length;
        _nothingAfterAt = null;
        _nothingBeforeAt = null;
    }

    /// <summary>
    /// Changes a run of the box's text and leaves the reader's selection on
    /// the text it was on. The box is read-only to the reader, not to this.
    /// </summary>
    private void Replace(int index, int length, string text)
    {
        var start = _box.SelectionStart;
        var end = start + _box.SelectionLength;
        _box.Select(index, length);
        _box.SelectedText = text;

        int Moved(int at) =>
            at < index ? at
            : at >= index + length ? at + text.Length - length
            : index;
        _box.Select(Moved(start), Moved(end) - Moved(start));
    }

    /// <summary>
    /// Changes the box's text without moving what the reader is looking at.
    /// A change above them moves everything by its height, and the scroll
    /// position goes with it.
    /// </summary>
    private void KeepingPlace(bool above, Action change)
    {
        if (_scroll is null)
        {
            change();
            return;
        }

        var extent = _scroll.ExtentHeight;
        var vertical = _scroll.VerticalOffset;
        var horizontal = _scroll.HorizontalOffset;
        change();
        _box.UpdateLayout();
        _scroll.ScrollToVerticalOffset(Math.Max(0, above ? vertical + _scroll.ExtentHeight - extent : vertical));
        _scroll.ScrollToHorizontalOffset(horizontal);

        // A scroll viewer moves at its next layout. The change after this
        // one reads where it is, so it moves now.
        _scroll.UpdateLayout();
    }

    private void Begin()
    {
        _busy = true;
        _turn++;
    }

    /// <summary>
    /// The read is back. A jump that waited for it goes now. Otherwise the
    /// box looks again once input and drawing have had the thread: it may
    /// still be near an end, or have room left to fill.
    /// </summary>
    private void Finish()
    {
        _busy = false;
        if (_left)
        {
            return;
        }

        if (_jump is { } toEnd)
        {
            _jump = null;
            Jump(toEnd);
        }
        else
        {
            LookLater(DispatcherPriority.ApplicationIdle);
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer found)
            {
                return found;
            }

            if (FindScroll(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
