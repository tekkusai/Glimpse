using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>The embedded picture of page one: read fast, capped, never fetched.</summary>
public sealed class OfficeThumbnailTests
{
    [Fact]
    public void ReadsTheThumbnailAnOfficePackageDeclares()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deck.pptx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Entry(archive, "_rels/.rels", Rels("docProps/thumbnail.jpeg"));
            Entry(archive, "docProps/thumbnail.jpeg", Encode(new JpegBitmapEncoder(), 64, 36));
        }

        var thumbnail = OfficeThumbnail.TryRead(path);

        Assert.NotNull(thumbnail);
        Assert.Equal(64, thumbnail.PixelWidth);
        Assert.Equal(36, thumbnail.PixelHeight);
        Assert.True(thumbnail.IsFrozen);
    }

    [Fact]
    public void ReadsTheThumbnailAnOpenDocumentCarries()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "talk.odp");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Entry(archive, "Thumbnails/thumbnail.png", Encode(new PngBitmapEncoder(), 40, 30));
        }

        Assert.Equal(40, OfficeThumbnail.TryRead(path)?.PixelWidth);
    }

    [Fact]
    public void AnExternalThumbnailTargetIsNeverFollowed()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "linked.docx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Entry(archive, "_rels/.rels", Rels("http://127.0.0.1/thumb.png", external: true));
        }

        Assert.Null(OfficeThumbnail.TryRead(path));
    }

    [Fact]
    public void AGiantCanvasIsRefusedBeforeItIsDecoded()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "bomb.docx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            // Two-color pixels compress to almost nothing at any size.
            var huge = BitmapSource.Create(5000, 5000, 96, 96, PixelFormats.BlackWhite, null, new byte[625 * 5000], 625);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(huge));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            Entry(archive, "docProps/thumbnail.png", stream.ToArray());
        }

        Assert.Null(OfficeThumbnail.TryRead(path));
    }

    [Theory]
    [InlineData("docProps/thumbnail.wmf")]
    [InlineData("docProps/thumbnail.jpeg")]
    public void BytesThatAreNotPngOrJpegAreIgnored(string entry)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "odd.docx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Entry(archive, "_rels/.rels", Rels(entry));
            Entry(archive, entry, [0xD7, 0xCD, 0xC6, 0x9A, 0, 0, 0, 0]);
        }

        Assert.Null(OfficeThumbnail.TryRead(path));
    }

    [Fact]
    public void AFileThatIsNotAPackageHasNoThumbnail()
    {
        using var temp = new TempDirectory();
        Assert.Null(OfficeThumbnail.TryRead(temp.CreateFile("old.doc", "binary")));
    }

    private static string Rels(string target, bool external = false) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId9" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/thumbnail" Target="{target}"{(external ? " TargetMode=\"External\"" : string.Empty)}/>
        </Relationships>
        """;

    private static byte[] Encode(BitmapEncoder encoder, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)200);
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Entry(ZipArchive archive, string name, string text) =>
        Entry(archive, name, System.Text.Encoding.UTF8.GetBytes(text));

    private static void Entry(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(bytes);
    }
}
