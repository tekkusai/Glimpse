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
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, "ZST")]
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
}
