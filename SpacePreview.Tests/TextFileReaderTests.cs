using System.Text;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// A text file read a piece at a time comes out as the same text the
/// whole-file loader decodes, in every encoding it knows, whatever size the
/// pieces are and whichever way they are read.
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
        Assert.All(pieces, piece => Assert.False(piece.SplitsALine));
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
    public void ALineLongerThanAReadIsCutBetweenCharactersNeverInsideOne(string encoding)
    {
        using var temp = new TempDirectory();

        // One line of characters that take four bytes in UTF-8 and two
        // units in UTF-16, between two ordinary lines.
        var line = string.Concat(Enumerable.Repeat("a\U0001F600é日", 1500));
        var (path, expected) = Write(temp, encoding, "first\r\n" + line + "\r\nlast\r\n");
        var reader = TextFileReader.TryOpen(path)!;

        // The repeat is ten bytes in either encoding, and a read of 1003
        // is not a whole number of them: the cuts fall everywhere in turn.
        var forward = Forward(reader, 1003);
        Assert.Equal(expected, string.Concat(forward.Select(piece => piece.Text)));
        Assert.Contains(forward, piece => piece.SplitsALine);
        Assert.DoesNotContain('�', string.Concat(forward.Select(piece => piece.Text)));

        var backward = Backward(reader, 1003);
        Assert.Equal(expected, string.Concat(backward.Select(piece => piece.Text)));
        Assert.Contains(backward, piece => piece.SplitsALine);
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
        Assert.Equal(200L * 1024 * 1024, middle.Start);

        // Bytes of zero are not characters, here as in the loader.
        Assert.Equal(string.Empty, middle.Text);
        Assert.Equal(reader.Length, last.End);

        // Three reads of 64 KB: their bytes and their text, and nothing
        // that grows with the 300 MB behind them.
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
