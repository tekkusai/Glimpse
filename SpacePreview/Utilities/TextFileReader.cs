using System.IO;
using System.Text;

namespace SpacePreview.Utilities;

/// <summary>A run of a text file: its text, and the bytes of the file it was read from.</summary>
internal sealed record TextPiece(string Text, long Start, long End)
{
    /// <summary>
    /// The run stops inside a line: no line ended within the bytes asked
    /// for. Only a line longer than a whole read does that.
    /// </summary>
    public bool SplitsALine { get; init; }
}

/// <summary>
/// A text file read a piece at a time, by position. The encoding is settled
/// once, from the same first bytes the whole-file loader reads, and every
/// piece after that is decoded on its own: it starts and ends on a line, or
/// on a character when a line is longer than the read. Nothing is held open
/// between reads and no read is larger than what was asked for. So a file's
/// size costs nothing until its text is wanted, and a log that is still
/// being written is read as it then is.
/// </summary>
internal sealed class TextFileReader
{
    private const int Utf16 = 1200;
    private const int Utf16BigEndian = 1201;

    private readonly string _path;
    private readonly Encoding _encoding;
    private readonly int _unit;
    private readonly bool _bigEndian;

    private TextFileReader(string path, Encoding encoding, int preamble)
    {
        _path = path;
        _encoding = encoding;
        _unit = encoding.CodePage is Utf16 or Utf16BigEndian ? 2 : 1;
        _bigEndian = encoding.CodePage == Utf16BigEndian;
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

    /// <summary>
    /// The text from <paramref name="start"/> on, from at most
    /// <paramref name="maxBytes"/> of the file. Short of the file's end it
    /// stops after the last line that is whole in those bytes. Empty when
    /// the file has nothing there.
    /// </summary>
    public TextPiece ReadAfter(long start, int maxBytes)
    {
        var bytes = Read(start, Math.Max(maxBytes, 64), out var atEnd);
        var length = bytes.Length;
        var split = false;
        if (!atEnd)
        {
            var line = LastLineEnd(bytes);
            split = line == 0;
            length = split ? WholeCharacters(bytes) : line;
        }

        return new TextPiece(Decode(bytes, 0, length), start, start + length) { SplitsALine = split };
    }

    /// <summary>
    /// The text that ends at <paramref name="end"/>, from at most
    /// <paramref name="maxBytes"/> of the file. Short of the file's start
    /// it begins at the first line that starts in those bytes.
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

        var bytes = Read(start, (int)(end - start), out _);
        var skip = 0;
        var split = false;
        if (start > First)
        {
            var line = FirstLineStart(bytes);
            split = line < 0 || line >= bytes.Length;
            skip = split ? FirstCharacter(bytes) : line;
        }

        return new TextPiece(Decode(bytes, skip, bytes.Length - skip), start + skip, start + bytes.Length) { SplitsALine = split };
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

    private string Decode(byte[] bytes, int index, int count) =>
        count <= 0 ? string.Empty : _encoding.GetString(bytes, index, count).Replace("\0", string.Empty);

    private bool IsLineEnd(byte[] bytes, int at) =>
        _unit == 1 ? bytes[at] == (byte)'\n'
        : _bigEndian ? bytes[at] == 0 && bytes[at + 1] == (byte)'\n'
        : bytes[at] == (byte)'\n' && bytes[at + 1] == 0;

    /// <summary>The length that ends just past the last line end, or 0 with none.</summary>
    private int LastLineEnd(byte[] bytes)
    {
        for (var at = bytes.Length / _unit * _unit - _unit; at >= 0; at -= _unit)
        {
            if (IsLineEnd(bytes, at))
            {
                return at + _unit;
            }
        }

        return 0;
    }

    /// <summary>The index just past the first line end, or -1 with none.</summary>
    private int FirstLineStart(byte[] bytes)
    {
        for (var at = 0; at + _unit <= bytes.Length; at += _unit)
        {
            if (IsLineEnd(bytes, at))
            {
                return at + _unit;
            }
        }

        return -1;
    }

    /// <summary>The longest length that does not end inside a character.</summary>
    private int WholeCharacters(byte[] bytes)
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

        if (_encoding.IsSingleByte || bytes.Length == 0)
        {
            return bytes.Length;
        }

        // A double-byte code page: a lead byte left waiting at the end is
        // half a character.
        var decoder = _encoding.GetDecoder();
        decoder.GetCharCount(bytes, 0, bytes.Length, flush: false);
        return decoder.GetCharCount([], 0, 0, flush: true) > 0 ? bytes.Length - 1 : bytes.Length;
    }

    /// <summary>The first index that does not start inside a character, as far as the bytes can say.</summary>
    private int FirstCharacter(byte[] bytes)
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

    private bool IsSurrogate(byte[] bytes, int at, bool high)
    {
        var top = _bigEndian ? bytes[at] : bytes[at + 1];
        return high ? top is >= 0xD8 and <= 0xDB : top is >= 0xDC and <= 0xDF;
    }

    private static FileStream Open(string path) =>
        File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
