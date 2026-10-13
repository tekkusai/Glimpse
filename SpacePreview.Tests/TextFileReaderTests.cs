using System.Text;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// A text file read a piece at a time comes out as the same text the
/// whole-file loader decodes, in every encoding it knows, whatever size the
/// pieces are and whichever way they are read. A line too long to lay out
/// comes out as its first characters and how much more of it there is, the
/// same from either side.
/// </summary>
public sealed class TextFileReaderTests
{
    public static TheoryData<string, int> EncodingsAndPieces()
    {
        var data = new TheoryData<string, int>();
        foreach (var encoding in new[] { "utf8", "utf8-bom", "utf16", "utf16-be", "ansi" })
        {
            foreach (var piece in new[] { 257, 1000, 4096 })
            {
                data.Add(encoding, piece);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EncodingsAndPieces))]
    public void PiecesReadForwardAreTheWholeFile(string encoding, int pieceBytes)
    {
        using var temp = new TempDirectory();
        var (path, expected) = Write(temp, encoding, Lines(600));
        var reader = TextFileReader.TryOpen(path)!;

        var pieces = Forward(reader, pieceBytes);

        Assert.Equal(expected, string.Concat(pieces.Select(piece => piece.Text)));
        Assert.All(pieces, piece => Assert.InRange(piece.End - piece.Start, 1, pieceBytes));
        Assert.All(pieces.SkipLast(1), piece => Assert.EndsWith("\n", piece.Text, StringComparison.Ordinal));
        Assert.Equal(reader.Length, pieces[^1].End);
    }

    [Theory]
    [MemberData(nameof(EncodingsAndPieces))]
    public void PiecesReadBackwardFromTheEndAreTheWholeFile(string encoding, int pieceBytes)
    {
        using var temp = new TempDirectory();
        var (path, expected) = Write(temp, encoding, Lines(600));
        var reader = TextFileReader.TryOpen(path)!;

        var pieces = Backward(reader, pieceBytes);

        Assert.Equal(expected, string.Concat(pieces.Select(piece => piece.Text)));
        Assert.All(pieces, piece => Assert.InRange(piece.End - piece.Start, 1, pieceBytes));
        Assert.Equal(reader.First, pieces[0].Start);

        // Every piece but the first begins a line: the one before it ended one.
        Assert.All(pieces.SkipLast(1), piece => Assert.EndsWith("\n", piece.Text, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("utf16-be")]
    public void ALineLongerThanAReadIsAPieceOfItsOwn(string encoding)
    {
        using var temp = new TempDirectory();

        // One line of fifteen thousand bytes between two ordinary ones:
        // fifteen reads long, and not too long to lay out.
        var line = string.Concat(Enumerable.Repeat("a\U0001F600é日", 1500));
        var (path, expected) = Write(temp, encoding, "first\r\n" + line + "\r\nlast\r\n");
        var reader = TextFileReader.TryOpen(path)!;

        var forward = Forward(reader, 1003);
        Assert.Equal(expected, string.Concat(forward.Select(piece => piece.Text)));
        Assert.Contains(forward, piece => piece.Text == line + "\r\n");

        var backward = Backward(reader, 1003);
        Assert.Equal(expected, string.Concat(backward.Select(piece => piece.Text)));
        Assert.Contains(backward, piece => piece.Text == line + "\r\n");
    }

    public static TheoryData<string, int> EncodingsAndReads()
    {
        var data = new TheoryData<string, int>();
        foreach (var encoding in new[] { "utf8", "utf16", "utf16-be", "ansi" })
        {
            // Shorter than the line, about its size, and the whole file.
            foreach (var read in new[] { 1003, 64 * 1024, 1024 * 1024 })
            {
                data.Add(encoding, read);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EncodingsAndReads))]
    public void AVeryLongLineIsItsFirstCharactersAndHowMuchMoreThereIs(string encoding, int readBytes)
    {
        using var temp = new TempDirectory();

        // A line far too long to lay out, of characters that take one to
        // four bytes so the cut can fall inside one. It is there twice:
        // between ordinary lines, and as the file's last with no line end.
        // The ANSI file's long line is plain letters, because what its
        // other bytes mean depends on the PC.
        var unit = encoding.StartsWith("utf16", StringComparison.Ordinal) ? 2 : 1;
        var line = encoding == "ansi"
            ? string.Concat(Enumerable.Repeat("plain letters ", 9000))
            : string.Concat(Enumerable.Repeat("a\U0001F600é日", 20_000));
        var (path, whole) = Write(temp, encoding, "first café\r\n" + line + "\r\nlast\r\n" + line);
        var reader = TextFileReader.TryOpen(path)!;

        // What is shown of it: the whole characters that fit the bytes.
        var measure = unit == 2 ? Encoding.Unicode : Encoding.UTF8;
        var shown = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (measure.GetByteCount(line.AsSpan(0, shown + rune.Utf16SequenceLength)) > TextFileReader.ShownOfALongLine * unit)
            {
                break;
            }

            shown += rune.Utf16SequenceLength;
        }

        var more = measure.GetByteCount(line) - measure.GetByteCount(line.AsSpan(0, shown));
        Assert.InRange(shown, 500, TextFileReader.ShownOfALongLine);
        var expected = whole[..whole.IndexOf(line, StringComparison.Ordinal)]
            + line[..shown] + "\r\n" + TextFileReader.LineGoesOn(more + (2 * unit)) + "\r\n"
            + "last\r\n"
            + line[..shown] + "\r\n" + TextFileReader.LineGoesOn(more);

        var forward = Forward(reader, readBytes);
        Assert.Equal(expected, string.Concat(forward.Select(piece => piece.Text)));
        Assert.Equal(reader.Length, forward[^1].End);

        var backward = Backward(reader, readBytes);
        Assert.Equal(expected, string.Concat(backward.Select(piece => piece.Text)));
        Assert.Equal(reader.First, backward[0].Start);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    public void ALineIsLongByItsBytesAndTwiceAsManyInUtf16(string encoding)
    {
        using var temp = new TempDirectory();

        // Thirty thousand letters are a line the box lays out, in either
        // encoding. Forty thousand are not.
        var fits = new string('x', 30_000);
        var (path, expected) = Write(temp, encoding, "first\r\n" + fits + "\r\n" + new string('y', 40_000) + "\r\nlast\r\n");
        var reader = TextFileReader.TryOpen(path)!;

        var text = string.Concat(Forward(reader, 1024 * 1024).Select(piece => piece.Text));

        Assert.Contains(fits + "\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('y', TextFileReader.ShownOfALongLine + 1), text, StringComparison.Ordinal);
        Assert.Contains(new string('y', TextFileReader.ShownOfALongLine) + "\r\n[This line goes on for another ", text, StringComparison.Ordinal);
        Assert.NotEqual(expected, text);
    }

    [Theory]
    [InlineData("utf8", "last")]
    [InlineData("utf16", "end")]
    public void ALineLongerThanTheReachIsTakenARunAtATimeBetweenCharacters(string encoding, string after)
    {
        using var temp = new TempDirectory();

        // Eighteen megabytes with no line end, of characters the reach
        // falls inside from either side: three bytes each in UTF-8, pairs
        // of units in UTF-16 set off by one letter.
        var line = encoding == "utf8"
            ? new string('日', 6_000_000)
            : "a" + new StringBuilder().Insert(0, "\U0001F600", 4_500_000);
        var path = Path.Combine(temp.Path, "reach.log");
        var bytes = encoding == "utf8"
            ? new UTF8Encoding(false).GetBytes("first\r\n" + line + "\r\n" + after + "\r\n")
            : [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("first\r\n" + line + "\r\n" + after + "\r\n")];
        File.WriteAllBytes(path, bytes);
        var reader = TextFileReader.TryOpen(path)!;
        var measure = encoding == "utf8" ? Encoding.UTF8 : Encoding.Unicode;

        foreach (var pieces in new[] { Forward(reader, 64 * 1024), Backward(reader, 64 * 1024) })
        {
            // The two ordinary lines, and the long one in two runs: one as
            // long as the reach to within a character, and the rest.
            Assert.Equal(4, pieces.Count);
            Assert.Equal("first\r\n", pieces[0].Text);
            Assert.Equal(after + "\r\n", pieces[3].Text);
            Assert.Equal(reader.First, pieces[0].Start);
            Assert.Equal(reader.Length, pieces[3].End);
            Assert.Contains(pieces, piece => piece.End - piece.Start is > TextFileReader.FarthestLineEnd - 4 and <= TextFileReader.FarthestLineEnd);

            foreach (var run in pieces.Skip(1).Take(2))
            {
                // Each run is whole characters of the line, then how much
                // of the run is not shown.
                var first = run.Text[..run.Text.IndexOf('\r')];
                Assert.InRange(first.Length, 600, TextFileReader.ShownOfALongLine);
                Assert.DoesNotContain('�', first);
                Assert.Contains(first, line, StringComparison.Ordinal);
                Assert.Equal(
                    first + "\r\n" + TextFileReader.LineGoesOn(run.End - run.Start - measure.GetByteCount(first)) + "\r\n",
                    run.Text);
            }
        }
    }

    [Fact]
    public void AFileThatGrowsIsReadOnFromWhereItEnded()
    {
        using var temp = new TempDirectory();
        var (path, _) = Write(temp, "utf8", Lines(50));
        var reader = TextFileReader.TryOpen(path)!;
        var end = Forward(reader, 4096)[^1].End;
        Assert.Equal(string.Empty, reader.ReadAfter(end, 4096).Text);

        File.AppendAllText(path, "written later é\r\nand once more\r\n", new UTF8Encoding(false));

        var more = reader.ReadAfter(end, 4096);
        Assert.Equal("written later é\r\nand once more\r\n", more.Text);
        Assert.Equal(reader.Length, more.End);
        Assert.EndsWith("and once more\r\n", reader.ReadBefore(reader.Length, 64).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatShrinksOrGoesAwayReadsAsNothing()
    {
        using var temp = new TempDirectory();
        var (path, _) = Write(temp, "utf8", Lines(200));
        var reader = TextFileReader.TryOpen(path)!;
        var end = Forward(reader, 4096)[^1].End;

        File.WriteAllText(path, "short\r\n", new UTF8Encoding(false));
        Assert.Equal(string.Empty, reader.ReadAfter(end, 4096).Text);
        Assert.Equal("short\r\n", reader.ReadBefore(end, 4096).Text);

        File.Delete(path);
        Assert.Equal(0, reader.Length);
        Assert.Equal(string.Empty, reader.ReadAfter(0, 4096).Text);
        Assert.Equal(string.Empty, reader.ReadBefore(end, 4096).Text);
    }

    [Fact]
    public void WhatIsNotTextOrNotThereIsNotOpened()
    {
        using var temp = new TempDirectory();
        var binary = Path.Combine(temp.Path, "data.log");
        File.WriteAllBytes(binary, [1, 2, 0, 3, 4, 5, 6, 7]);

        Assert.Null(TextFileReader.TryOpen(binary));
        Assert.Null(TextFileReader.TryOpen(Path.Combine(temp.Path, "missing.log")));
    }

    [Fact]
    public void AReadCostsWhatWasAskedForWhateverTheFilesSize()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "huge.log");
        var head = Encoding.ASCII.GetBytes(Lines(40_000));
        using (var file = File.Create(path))
        {
            file.Write(head);

            // 300 MB more that the disk never writes: it reads as zeros.
            file.SetLength(head.Length + 300L * 1024 * 1024);
        }

        var reader = TextFileReader.TryOpen(path)!;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var first = reader.ReadAfter(reader.First, 64 * 1024);
        var middle = reader.ReadAfter(200L * 1024 * 1024, 64 * 1024);
        var last = reader.ReadBefore(reader.Length, 64 * 1024);
        var cost = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.StartsWith("line 0000000", first.Text, StringComparison.Ordinal);
        Assert.InRange(first.End - first.Start, 60 * 1024, 64 * 1024);

        // Zeros have no line end among them, so they are one line longer
        // than the reach, and a read takes a run of it and no more. Bytes
        // of zero are not characters, here as in the loader: nothing of
        // the run is shown but how long it is.
        var run = TextFileReader.LineGoesOn(TextFileReader.FarthestLineEnd - TextFileReader.ShownOfALongLine);
        Assert.Equal((200L * 1024 * 1024, (200L * 1024 * 1024) + TextFileReader.FarthestLineEnd), (middle.Start, middle.End));
        Assert.Equal("\r\n" + run + "\r\n", middle.Text);
        Assert.Equal((reader.Length - TextFileReader.FarthestLineEnd, reader.Length), (last.Start, last.End));
        Assert.Equal("\r\n" + run, last.Text);

        // Three reads: their bytes and their text, and nothing that grows
        // with the 300 MB behind them or with how far two of them looked.
        Assert.InRange(cost, 0, 4 * 1024 * 1024);
    }

    private static List<TextPiece> Forward(TextFileReader reader, int pieceBytes)
    {
        var pieces = new List<TextPiece>();
        var at = reader.First;
        while (true)
        {
            var piece = reader.ReadAfter(at, pieceBytes);
            Assert.Equal(at, piece.Start);
            if (piece.End == piece.Start)
            {
                return pieces;
            }

            pieces.Add(piece);
            at = piece.End;
            Assert.InRange(pieces.Count, 1, 100_000);
        }
    }

    private static List<TextPiece> Backward(TextFileReader reader, int pieceBytes)
    {
        var pieces = new List<TextPiece>();
        var at = reader.Length;
        while (at > reader.First)
        {
            var piece = reader.ReadBefore(at, pieceBytes);
            Assert.Equal(at, piece.End);
            Assert.True(piece.Start < piece.End, "a read backward made no way");
            pieces.Insert(0, piece);
            at = piece.Start;
            Assert.InRange(pieces.Count, 1, 100_000);
        }

        return pieces;
    }

    private static string Lines(int count)
    {
        var text = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            text.Append("line ").Append(i.ToString("0000000")).Append(" café naïve ");
            text.Append(new string((char)('a' + i % 26), i % 61)).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Writes the text in an encoding and says what the whole-file loader
    /// makes of those bytes: that is the text the pieces must add up to.
    /// </summary>
    private static (string Path, string Expected) Write(TempDirectory temp, string encoding, string text)
    {
        byte[] bytes = encoding switch
        {
            "utf8" => new UTF8Encoding(false).GetBytes(text),
            "utf8-bom" => [.. Encoding.UTF8.GetPreamble(), .. new UTF8Encoding(false).GetBytes(text)],
            "utf16" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            "utf16-be" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],

            // Not valid UTF-8, so it is read in the PC's own code page.
            _ => Encoding.Latin1.GetBytes(text)
        };
        var path = Path.Combine(temp.Path, encoding + ".log");
        File.WriteAllBytes(path, bytes);
        return (path, TextPreviewLoader.Decode(bytes, truncated: false)!);
    }
}
