using System.IO.Compression;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class OfficeOpenXmlPreviewLoaderTests
{
    [Fact]
    public void MediaCacheSeparatesSameNamedDocumentsFromDifferentFolders()
    {
        using var temp = new TempDirectory();
        var aDir = Directory.CreateDirectory(Path.Combine(temp.Path, "a"));
        var bDir = Directory.CreateDirectory(Path.Combine(temp.Path, "b"));
        var aPath = Path.Combine(aDir.FullName, "deck.docx");
        var bPath = Path.Combine(bDir.FullName, "deck.docx");
        var cacheRoot = Path.Combine(temp.Path, "OfficeMedia");
        var fixedTime = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        var aImage = new byte[] { 1, 2, 3, 4, 5 };
        var bImage = new byte[] { 9, 8, 7, 6, 5 };

        CreateDocx(aPath, aImage);
        CreateDocx(bPath, bImage);
        File.SetLastWriteTimeUtc(aPath, fixedTime);
        File.SetLastWriteTimeUtc(bPath, fixedTime);

        OfficeOpenXmlPreviewLoader.MediaCacheRootOverride = cacheRoot;
        try
        {
            var aPreview = OfficeOpenXmlPreviewLoader.TryLoad(aPath);
            var bPreview = OfficeOpenXmlPreviewLoader.TryLoad(bPath);

            Assert.NotNull(aPreview);
            Assert.NotNull(bPreview);
            var aExtracted = Assert.Single(aPreview!.ImagePaths);
            var bExtracted = Assert.Single(bPreview!.ImagePaths);
            Assert.StartsWith(cacheRoot, aExtracted, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(cacheRoot, bExtracted, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(aExtracted, bExtracted);
            Assert.Equal(aImage, File.ReadAllBytes(aExtracted));
            Assert.Equal(bImage, File.ReadAllBytes(bExtracted));
        }
        finally
        {
            OfficeOpenXmlPreviewLoader.MediaCacheRootOverride = null;
        }
    }

    [Fact]
    public void ForcedOpenXmlTypePreviewsRenamedDocuments()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "renamed-file.bin");
        CreateDocx(path, [1, 2, 3]);

        var preview = OfficeOpenXmlPreviewLoader.TryLoad(path, OfficePreviewType.Word);

        Assert.NotNull(preview);
        Assert.Equal(OfficePreviewType.Word, preview!.Type);
        Assert.Contains("Hello", preview.Text, StringComparison.Ordinal);
    }

    private static void CreateDocx(string path, byte[] imageBytes)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        var document = archive.CreateEntry("word/document.xml");
        using (var writer = new StreamWriter(document.Open()))
        {
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:p><w:r><w:t>Hello</w:t></w:r></w:p></w:body>
                </w:document>
                """);
        }

        var media = archive.CreateEntry("word/media/image1.png");
        using var mediaStream = media.Open();
        mediaStream.Write(imageBytes);
    }

}
