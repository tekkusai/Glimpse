using System.Formats.Tar;
using System.IO.Compression;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Archive listings as the user reads them: every entry of a compressed
/// tarball, an honest floor when the walk stops, and counts in words.
/// </summary>
public sealed class CardsArchiveListingTests
{
    [Theory]
    [InlineData("src.tar.gz")]
    [InlineData("src.tgz")]
    [InlineData("release.gz")]
    public void ACompressedTarballListsEveryEntry(string name)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, name);
        WriteTarGz(path);

        var listing = ArchivePreviewLoader.TryLoad(path);

        Assert.NotNull(listing);
        Assert.Equal(3, listing!.TotalEntries);
        Assert.False(listing.Stopped);
        var src = Assert.Single(listing.Roots);
        Assert.Equal("src", src.Name);
        // Largest first, folders and files alike, as a folder ranks its rows.
        Assert.Equal(["b.md", "sub", "a.txt"], src.Children.Select(child => child.Name));
        Assert.Equal("3 files · 26 B uncompressed", ArchivePreviewLoader.Describe(listing));
    }

    [Fact]
    public void ATarballWalkOutOfTimeIsAFloor()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "src.tar.gz");
        WriteTarGz(path);

        var listing = ArchivePreviewLoader.TryLoad(path, TimeSpan.Zero, CancellationToken.None);

        Assert.NotNull(listing);
        Assert.True(listing!.Stopped);
        Assert.True(listing.Truncated);
        Assert.StartsWith("At least ", ArchivePreviewLoader.Describe(listing), StringComparison.Ordinal);
        Assert.DoesNotContain("Partial list", ArchivePreviewLoader.Describe(listing), StringComparison.Ordinal);
    }

    // Moving past an entry inflates all of it; the clock and the user stop
    // that inside the entry instead of after four gigabytes.
    [Fact]
    public async Task ATarballWalkOutOfTimeStopsInsideAHugeEntry()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "huge.tar.gz");
        WriteTarGzWithHugeFirstEntry(path);

        var listing = await Task.Run(() => ArchivePreviewLoader.TryLoad(path, TimeSpan.FromMilliseconds(50), CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(listing);
        Assert.True(listing!.Stopped);
        Assert.StartsWith("At least ", ArchivePreviewLoader.Describe(listing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledTarballWalkStopsInsideAHugeEntry()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "huge.tar.gz");
        WriteTarGzWithHugeFirstEntry(path);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Run(() => ArchivePreviewLoader.TryLoad(path, TimeSpan.FromMinutes(5), cancel.Token)).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ASingleCompressedFileStillListsAsOneFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "notes.txt.gz");
        using (var gzip = new GZipStream(File.Create(path), CompressionLevel.Fastest))
        {
            gzip.Write("note"u8);
        }

        var listing = ArchivePreviewLoader.TryLoad(path);

        Assert.NotNull(listing);
        Assert.Equal(1, listing!.TotalEntries);
        Assert.StartsWith("1 file · ", ArchivePreviewLoader.Describe(listing), StringComparison.Ordinal);
    }

    [Fact]
    public void OneFileZipSaysOneFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "one.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("only.txt").Open());
            writer.Write("four");
        }

        var listing = ArchivePreviewLoader.TryLoad(path);

        Assert.Equal("1 file · 4 B uncompressed", ArchivePreviewLoader.Describe(listing!));
    }

    [Fact]
    public void AnEmptyArchiveSaysEmpty()
    {
        var listing = new ArchiveListing(0, 0, [], Truncated: false);

        Assert.Equal("Empty archive", ArchivePreviewLoader.Describe(listing));
    }

    [Theory]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, "GZ")]
    [InlineData(new byte[] { (byte)'B', (byte)'Z', (byte)'h', (byte)'9' }, "BZ2")]
    [InlineData(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, "XZ")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, null)]
    public void OuterCompressionIsReadFromTheMagicNumber(byte[] header, string? expected)
    {
        Assert.Equal(expected, ArchivePreviewLoader.CompressionSuffix(header));
    }

    /// <summary>src/a.txt (6 B), src/b.md (12 B), src/sub/c.cs (8 B).</summary>
    private static void WriteTarGz(string path)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip, TarEntryFormat.Ustar);
        tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "src/"));
        AddFile(tar, "src/a.txt", "alpha\n");
        AddFile(tar, "src/b.md", "bravo bravo\n");
        tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "src/sub/"));
        AddFile(tar, "src/sub/c.cs", "charlie\n");
    }

    private static void AddFile(TarWriter tar, string name, string text)
    {
        tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text))
        });
    }

    /// <summary>
    /// big.bin, four gigabytes of zeros, then small.txt. The deflate stream
    /// is written by hand with fixed codes, one 258-zero repeat per 13 bits,
    /// so the file is 27 MB and quick to make. No walk reaches the gzip
    /// trailer, so its checksum stays zero.
    /// </summary>
    private static void WriteTarGzWithHugeFirstEntry(string path)
    {
        const long size = 4L << 30;
        using var file = new BufferedStream(File.Create(path), 1 << 16);
        file.Write([0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 0xFF]);
        var bits = new DeflateBits(file);
        bits.Write(1, 1); // the final block
        bits.Write(1, 2); // fixed codes
        bits.Literals(TarHeader("big.bin", size));
        bits.Literals([0]);
        var left = size - 1;
        for (; left >= 258; left -= 258)
        {
            bits.Write(0xA3, 13); // length 258 (code 285), distance 1 (code 0)
        }

        bits.Literals(new byte[left]);
        bits.Literals(TarHeader("small.txt", 5));
        bits.Literals("tail\n"u8);
        bits.Literals(new byte[507 + 1024]); // the block's padding, then the two empty end blocks
        bits.Write(0, 7); // end of block
        bits.Flush();
        file.Write(new byte[8]);
    }

    private static byte[] TarHeader(string name, long size)
    {
        var header = new byte[512];
        void Put(int at, string text) => System.Text.Encoding.ASCII.GetBytes(text).CopyTo(header, at);
        Put(0, name);
        Put(100, "0000644\0");
        Put(108, "0000000\0");
        Put(116, "0000000\0");
        Put(124, Convert.ToString(size, 8).PadLeft(11, '0') + "\0");
        Put(136, "00000000000\0");
        Put(148, "        ");
        header[156] = (byte)'0';
        Put(257, "ustar\0");
        Put(263, "00");
        Put(148, Convert.ToString(header.Sum(b => b), 8).PadLeft(6, '0') + "\0 ");
        return header;
    }

    /// <summary>Deflate's bit order: fields low bit first, Huffman codes high bit first.</summary>
    private sealed class DeflateBits(Stream output)
    {
        private ulong _bits;
        private int _count;

        public void Write(uint value, int length)
        {
            _bits |= (ulong)value << _count;
            _count += length;
            while (_count >= 8)
            {
                output.WriteByte((byte)_bits);
                _bits >>= 8;
                _count -= 8;
            }
        }

        /// <summary>Each byte as a fixed-code literal.</summary>
        public void Literals(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                var (code, length) = value < 144 ? (0x30u + value, 8) : (0x190u + value - 144u, 9);
                uint reversed = 0;
                for (var i = 0; i < length; i++)
                {
                    reversed |= ((code >> i) & 1) << (length - 1 - i);
                }

                Write(reversed, length);
            }
        }

        public void Flush()
        {
            if (_count > 0)
            {
                output.WriteByte((byte)_bits);
            }

            _bits = 0;
            _count = 0;
        }
    }
}
