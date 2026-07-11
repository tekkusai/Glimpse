using System.IO.Compression;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class FileSignaturesTests
{
    [Theory]
    [InlineData(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' }, (int)FileSignatureKind.Pdf)]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, (int)FileSignatureKind.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, (int)FileSignatureKind.Jpeg)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, (int)FileSignatureKind.Gif)]
    [InlineData(new byte[] { (byte)'B', (byte)'M' }, (int)FileSignatureKind.Bmp)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, (int)FileSignatureKind.Zip)]
    public void DetectsCommonHeaders(byte[] bytes, int expected)
    {
        Assert.Equal((FileSignatureKind)expected, FileSignatures.Detect(bytes));
    }

    [Fact]
    public void DetectsHeifIsoBrand()
    {
        var bytes = new byte[]
        {
            0x00, 0x00, 0x00, 0x18,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'h', (byte)'e', (byte)'i', (byte)'c',
            0x00, 0x00, 0x00, 0x00
        };

        Assert.Equal(FileSignatureKind.Heif, FileSignatures.Detect(bytes));
        Assert.True(FileSignatures.HasHevcCodedHeifBrand(bytes));
        Assert.Contains("heic", FileSignatures.HeifBrandText(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void GenericHeifBrandDoesNotClaimHevc()
    {
        var bytes = new byte[]
        {
            0x00, 0x00, 0x00, 0x18,
            (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'m', (byte)'i', (byte)'f', (byte)'1',
            0x00, 0x00, 0x00, 0x00
        };

        Assert.Equal(FileSignatureKind.Heif, FileSignatures.Detect(bytes));
        Assert.False(FileSignatures.HasHevcCodedHeifBrand(bytes));
        Assert.Contains("mif1", FileSignatures.HeifBrandText(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void DetectsOpenXmlPackageByEntries()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "renamed-file.bin");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "[Content_Types].xml", "<Types/>");
            WriteEntry(archive, "ppt/slides/slide1.xml", "<p:sld/>");
        }

        Assert.Equal(ZipPackageKind.OfficePowerPoint, FileSignatures.DetectZipPackageKind(path));
    }

    [Fact]
    public void DetectsEpubPackageByMimeType()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "renamed-book.bin");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "mimetype", "application/epub+zip");
            WriteEntry(archive, "OPS/content.opf", "<package/>");
        }

        Assert.Equal(ZipPackageKind.Epub, FileSignatures.DetectZipPackageKind(path));
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }
}
