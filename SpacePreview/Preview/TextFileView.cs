using System.Windows;
using System.Windows.Controls;
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
/// lines of text. The file may be on a share that has stopped answering,
/// so nothing here asks the reader anything on the window's thread: every
/// question goes through Task.Run.
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
    private readonly LinkedList<Held> _held = new();
    private ScrollViewer? _scroll;
    private int _characters;
    private int _turn;
    private bool _busy;
    private bool _left;
    private long? _nothingAfterAt;
    private long? _nothingBeforeAt;

    public TextFileView(
        TextBox box,
        TextFileReader reader,
        TextPiece head,
        int headBytes = HeadBytes,
        int pieceBytes = PieceBytes,
        int heldCharacters = HeldCharacters)
    {
        _box = box;
        _reader = reader;
        _headBytes = headBytes;
        _pieceBytes = pieceBytes;
        _heldCharacters = heldCharacters;
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
            box.LayoutUpdated -= OnLayout;
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
    /// when they are the whole file. There is nothing to read on to then,
    /// and of a file that is one long line the loader's cut shows more.
    /// </summary>
    public static TextPiece? TryReadHead(TextFileReader reader, int headBytes = HeadBytes)
    {
        var head = reader.ReadAfter(reader.First, headBytes);
        return head.Text.Length == 0 || head.End >= reader.Length ? null : head;
    }

    /// <summary>To the file's last line, as the file is now.</summary>
    public async void GoToEnd()
    {
        var turn = Begin();
        try
        {
            // Far from the end, the box lets go of what it holds and takes
            // the file's last lines. Near it there is no tail to take: the
            // box reads on from where it is.
            var held = Range.End;
            var tail = await Task.Run(() => _reader.Length - held <= _headBytes ? null : _reader.ReadBefore(_reader.Length, _headBytes));
            if (turn != _turn || _left)
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
                if (turn != _turn || _left)
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
            if (Finish(turn) && !_left)
            {
                LookLater(DispatcherPriority.ApplicationIdle);
            }
        }
    }

    /// <summary>Back to the file's first line.</summary>
    public async void GoToStart()
    {
        var turn = Begin();
        try
        {
            if (Range.Start > _reader.First)
            {
                var head = await Task.Run(() => _reader.ReadAfter(_reader.First, _headBytes));
                if (turn != _turn || _left)
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
            if (Finish(turn) && !_left)
            {
                LookLater(DispatcherPriority.ApplicationIdle);
            }
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
        // reader has gone is a reason to read.
        if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
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
    /// at the very edge of what it holds is read to all the same.
    /// </summary>
    private void Look()
    {
        // A box that has no size yet has nowhere the reader could be.
        if (_scroll is null || _busy || _left || _scroll.ViewportHeight <= 0)
        {
            return;
        }

        var screens = _scroll.ViewportHeight * Screens;
        var above = _scroll.VerticalOffset;
        var below = _scroll.ExtentHeight - above - _scroll.ViewportHeight;
        var full = _characters >= _heldCharacters && _scroll.ExtentHeight >= screens;
        if (below <= screens && MayReadAfter && (!full || below < 1 || MayDrop(first: true)))
        {
            Extend(after: true);
        }
        else if (above <= screens && MayReadBefore && (!full || above < 1 || MayDrop(first: false)))
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

    /// <summary>More than the budget, and more than the screens the box holds whatever they cost.</summary>
    private bool Over =>
        _characters > _heldCharacters
        && _scroll is { } scroll
        && scroll.ExtentHeight > scroll.ViewportHeight * Screens;

    private bool MayReadAfter => Waited(_nothingAfterAt);

    private bool MayReadBefore => Range.Start > _reader.First && Waited(_nothingBeforeAt);

    /// <summary>A read that came back empty is not asked for again within the second.</summary>
    private static bool Waited(long? since) =>
        since is not { } at || Environment.TickCount64 - at >= EndRetry.TotalMilliseconds;

    private async void Extend(bool after)
    {
        var turn = Begin();
        try
        {
            var (start, end) = Range;
            var piece = await Task.Run(() => after ? _reader.ReadAfter(end, _pieceBytes) : _reader.ReadBefore(start, _pieceBytes));
            if (turn != _turn || _left)
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
            if (Finish(turn) && !_left)
            {
                // Still near an end, or room left to fill: after input and
                // drawing have had the thread.
                LookLater(DispatcherPriority.ApplicationIdle);
            }
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

    private int Begin()
    {
        _busy = true;
        return ++_turn;
    }

    /// <summary>True when this turn was still the one under way.</summary>
    private bool Finish(int turn)
    {
        if (turn != _turn)
        {
            return false;
        }

        _busy = false;
        return true;
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
