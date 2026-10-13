using System.IO;
using System.Text;

namespace SpacePreview.Utilities;

/// <summary>A run of a text file: its text, and the bytes of the file it was read from.</summary>
internal sealed record TextPiece(string Text, long Start, long End)
{
    /// <summary>
    /// The run stops at the reach, inside a line that has no end within
    /// it. The read after this one goes on with the same line.
    /// </summary>
    public bool StopsInALine { get; init; }
}

/// <summary>
/// A text file read a piece at a time, by position. The encoding is settled
/// once, from the same first bytes the whole-file loader reads, and every
/// piece after that is decoded on its own. A piece is whole lines. A line
/// too long to lay out is in it as its first characters and a line that
/// says how much more there is, so the lines after it are read like any
/// others. Nothing is held open between reads. A read takes the bytes it
/// was asked for, and for a long line looks on for where that ends, no
/// further than <see cref="FarthestLineEnd"/>. So a file's size costs
/// nothing until its text is wanted, and a log that is still being written
/// is read as it then is.
/// </summary>
internal sealed class TextFileReader
{
    /// <summary>
    /// A line of more bytes than this, twice as many in UTF-16, is not
    /// shown whole: one line of a megabyte stalls the text box.
    /// </summary>
    internal const int LongestLine = 32_768;

    /// <summary>How much of a longer line is shown, counted the same way: enough to know it by.</summary>
    internal const int ShownOfALongLine = 2_048;

    /// <summary>
    /// How far a read looks for where a line ends. A line longer than this
    /// is taken a run at a time, and each run is shown as a long line is.
    /// </summary>
    internal const int FarthestLineEnd = 16 * 1024 * 1024;

    private const int ScanBytes = 64 * 1024;
    private const int Utf16 = 1200;
    private const int Utf16BigEndian = 1201;

    private readonly string _path;
    private readonly Encoding _encoding;
    private readonly int _unit;
    private readonly bool _bigEndian;
    private readonly int _longest;
    private readonly int _shown;

    private TextFileReader(string path, Encoding encoding, int preamble)
    {
        _path = path;
        _encoding = encoding;
        _unit = encoding.CodePage is Utf16 or Utf16BigEndian ? 2 : 1;
        _bigEndian = encoding.CodePage == Utf16BigEndian;
        _longest = LongestLine * _unit;
        _shown = ShownOfALongLine * _unit;
        First = preamble;
    }

    /// <summary>Where the text begins: past a byte order mark.</summary>
    public long First { get; }

    /// <summary>The file's size now. Zero when it cannot be asked.</summary>
    public long Length
    {
        get
        {
            try
            {
                return new FileInfo(_path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    /// <summary>Null for a file that cannot be read or is not text.</summary>
    public static TextFileReader? TryOpen(string path)
    {
        try
        {
            byte[] sample;
            long length;
            using (var stream = Open(path))
            {
                length = stream.Length;
                sample = new byte[(int)Math.Min(length, TextPreviewLoader.MaxPreviewBytes)];
                var read = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
                if (read != sample.Length)
                {
                    Array.Resize(ref sample, read);
                }
            }

            var encoding = TextPreviewLoader.EncodingOf(sample, truncated: length > sample.Length, out var preamble);
            return encoding is null ? null : new TextFileReader(path, encoding, preamble);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What stands in a piece for the rest of a line too long to lay out.</summary>
    internal static string LineGoesOn(long bytes) =>
        $"[This line goes on for another {FileKinds.FormatSize(bytes)}. Open the file for the rest.]";

    /// <summary>
    /// The text from <paramref name="start"/> on, from at most
    /// <paramref name="maxBytes"/> of the file. Short of the file's end it
    /// stops after the last line that is whole in those bytes. A line
    /// longer than them is a piece of its own. Empty when the file has
    /// nothing there.
    /// </summary>
    public TextPiece ReadAfter(long start, int maxBytes)
    {
        var bytes = Read(start, Math.Max(maxBytes, 64), out var atEnd);
        var length = atEnd ? bytes.Length : LastLineEnd(bytes);
        if (length > 0 || bytes.Length == 0)
        {
            return new TextPiece(Lines(bytes.AsSpan(0, length), atEnd), start, start + length);
        }

        var end = LineEndAfter(start, start + bytes.Length - bytes.Length % _unit, out var ended, out var reached);
        return new TextPiece(Line(start, end, ended), start, end) { StopsInALine = reached };
    }

    /// <summary>
    /// The text that ends at <paramref name="end"/>, from at most
    /// <paramref name="maxBytes"/> of the file. Short of the file's start
    /// it begins at the first line that starts in those bytes. A line
    /// longer than them is a piece of its own.
    /// </summary>
    public TextPiece ReadBefore(long end, int maxBytes)
    {
        end = Math.Min(end, Length);
        var start = Math.Max(First, end - Math.Max(maxBytes, 64));
        start -= (start - First) % _unit;
        if (end <= start)
        {
            return new TextPiece(string.Empty, start, start);
        }

        var bytes = Read(start, (int)(end - start), out var atEnd);
        end = start + bytes.Length;
        var skip = 0;
        if (start > First)
        {
            // The first line here began before these bytes. A line end that
            // is the last thing in them ends the line they are the tail of.
            skip = FirstLineEnd(bytes.AsSpan(0, Math.Max(0, bytes.Length - _unit)));
            if (skip < 0)
            {
                var line = LineStartBefore(start, end);
                var ended = bytes.Length >= _unit && IsLineEnd(bytes, bytes.Length - _unit);
                return new TextPiece(Line(line, end, ended || !atEnd), line, end);
            }
        }

        return new TextPiece(Lines(bytes.AsSpan(skip), atEnd), start + skip, end);
    }

    /// <summary>The text of whole lines, each one too long to lay out shortened.</summary>
    private string Lines(ReadOnlySpan<byte> bytes, bool atEnd)
    {
        StringBuilder? text = null;
        var taken = 0;
        for (var line = 0; line < bytes.Length;)
        {
            var length = FirstLineEnd(bytes[line..]);
            var ended = length >= 0;
            if (!ended)
            {
                length = bytes.Length - line;
            }

            if (length > _longest)
            {
                text ??= new StringBuilder();
                text.Append(Decode(bytes[taken..line]));
                text.Append(Shortened(bytes.Slice(line, _shown), length, ended || !atEnd));
                taken = line + length;
            }

            line += length;
        }

        return text is null ? Decode(bytes) : text.Append(Decode(bytes[taken..])).ToString();
    }

    /// <summary>The text of one line that a read did not hold: the file's bytes from one position to another.</summary>
    private string Line(long start, long end, bool ended)
    {
        var whole = end - start <= _longest;
        var bytes = Read(start, whole ? (int)(end - start) : _shown, out _);
        return whole || bytes.Length < _shown ? Decode(bytes) : Shortened(bytes, end - start, ended);
    }

    /// <summary>
    /// A long line as it is shown: its first characters, and on a line of
    /// its own how much more of it there is. The line break after that is
    /// the line's own, so a file that ends in a long line ends there.
    /// </summary>
    private string Shortened(ReadOnlySpan<byte> first, long length, bool ended)
    {
        var shown = WholeCharacters(first);
        return Decode(first[..shown]) + "\r\n" + LineGoesOn(length - shown) + (ended ? "\r\n" : string.Empty);
    }

    /// <summary>
    /// Where the line that begins at <paramref name="line"/>, and has not
    /// ended by <paramref name="from"/>, does end: just past its line end,
    /// or at the file's end. A line longer than the reach is said to end
    /// at the reach, between two characters.
    /// </summary>
    private long LineEndAfter(long line, long from, out bool ended, out bool reached)
    {
        ended = true;
        reached = false;
        try
        {
            using var stream = Open(_path);
            var block = new byte[ScanBytes];
            var reach = line + FarthestLineEnd;
            while (from < reach)
            {
                var want = (int)Math.Min(block.Length, reach - from);
                stream.Position = from;
                var read = stream.ReadAtLeast(block.AsSpan(0, want), want, throwOnEndOfStream: false);
                var found = FirstLineEnd(block.AsSpan(0, read));
                if (found >= 0)
                {
                    return from + found;
                }

                from += read;
                if (read < want)
                {
                    ended = false;
                    return from;
                }

                if (from >= reach)
                {
                    reached = true;
                    return from - read + WholeCharacters(block.AsSpan(0, read));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ended = false;
        }

        return from;
    }

    /// <summary>
    /// Where the line that ends at <paramref name="end"/>, and began
    /// before <paramref name="before"/>, does begin: just past the line
    /// end ahead of it, or at the file's first character. A line longer
    /// than the reach is said to begin at the reach, between two characters.
    /// </summary>
    private long LineStartBefore(long before, long end)
    {
        var reach = Math.Max(First, end - FarthestLineEnd);
        reach -= (reach - First) % _unit;
        try
        {
            using var stream = Open(_path);
            var block = new byte[ScanBytes];
            while (before > reach)
            {
                var from = Math.Max(reach, before - block.Length);
                var want = (int)(before - from);
                stream.Position = from;
                var read = stream.ReadAtLeast(block.AsSpan(0, want), want, throwOnEndOfStream: false);
                if (read < want)
                {
                    break;
                }

                var found = LastLineEnd(block.AsSpan(0, read));
                if (found > 0)
                {
                    return from + found;
                }

                before = from;
                if (from > First && from == reach)
                {
                    return from + FirstCharacter(block.AsSpan(0, read));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The line begins where the reading stopped.
        }

        return before;
    }

    private byte[] Read(long start, int maxBytes, out bool atEnd)
    {
        atEnd = true;
        try
        {
            using var stream = Open(_path);
            var length = stream.Length;
            if (start < First || start >= length)
            {
                return [];
            }

            var count = (int)Math.Min(maxBytes, length - start);
            var bytes = new byte[count];
            stream.Position = start;
            var read = stream.ReadAtLeast(bytes, count, throwOnEndOfStream: false);
            if (read != count)
            {
                Array.Resize(ref bytes, read);
            }

            atEnd = start + read >= length;
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string Decode(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? string.Empty : _encoding.GetString(bytes).Replace("\0", string.Empty);

    private bool IsLineEnd(ReadOnlySpan<byte> bytes, int at) =>
        _unit == 1 ? bytes[at] == (byte)'\n'
        : _bigEndian ? bytes[at] == 0 && bytes[at + 1] == (byte)'\n'
        : bytes[at] == (byte)'\n' && bytes[at + 1] == 0;

    /// <summary>The length that ends just past the first line end, or -1 with none.</summary>
    private int FirstLineEnd(ReadOnlySpan<byte> bytes)
    {
        if (_unit == 1)
        {
            var found = bytes.IndexOf((byte)'\n');
            return found < 0 ? -1 : found + 1;
        }

        for (var at = 0; at + _unit <= bytes.Length; at += _unit)
        {
            if (IsLineEnd(bytes, at))
            {
                return at + _unit;
            }
        }

        return -1;
    }

    /// <summary>The length that ends just past the last line end, or 0 with none.</summary>
    private int LastLineEnd(ReadOnlySpan<byte> bytes)
    {
        if (_unit == 1)
        {
            return bytes.LastIndexOf((byte)'\n') + 1;
        }

        for (var at = bytes.Length / _unit * _unit - _unit; at >= 0; at -= _unit)
        {
            if (IsLineEnd(bytes, at))
            {
                return at + _unit;
            }
        }

        return 0;
    }

    /// <summary>The longest length that does not end inside a character.</summary>
    private int WholeCharacters(ReadOnlySpan<byte> bytes)
    {
        if (_unit == 2)
        {
            var length = bytes.Length & ~1;
            return length >= 2 && IsSurrogate(bytes, length - 2, high: true) ? length - 2 : length;
        }

        if (_encoding.CodePage == Encoding.UTF8.CodePage)
        {
            return bytes.Length - TextPreviewLoader.IncompleteUtf8Tail(bytes);
        }

        if (_encoding.IsSingleByte || bytes.IsEmpty)
        {
            return bytes.Length;
        }

        // A double-byte code page: a lead byte left waiting at the end is
        // half a character.
        var decoder = _encoding.GetDecoder();
        decoder.GetCharCount(bytes, flush: false);
        return decoder.GetCharCount([], flush: true) > 0 ? bytes.Length - 1 : bytes.Length;
    }

    /// <summary>The first index that does not start inside a character, as far as the bytes can say.</summary>
    private int FirstCharacter(ReadOnlySpan<byte> bytes)
    {
        if (_unit == 2)
        {
            return bytes.Length >= 2 && IsSurrogate(bytes, 0, high: false) ? 2 : 0;
        }

        if (_encoding.CodePage != Encoding.UTF8.CodePage)
        {
            return 0;
        }

        var at = 0;
        while (at < Math.Min(3, bytes.Length) && (bytes[at] & 0xC0) == 0x80)
        {
            at++;
        }

        return at;
    }

    private bool IsSurrogate(ReadOnlySpan<byte> bytes, int at, bool high)
    {
        var top = _bigEndian ? bytes[at] : bytes[at + 1];
        return high ? top is >= 0xD8 and <= 0xDB : top is >= 0xDC and <= 0xDF;
    }

    private static FileStream Open(string path) =>
        File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
