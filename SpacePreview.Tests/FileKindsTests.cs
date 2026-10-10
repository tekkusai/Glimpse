using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class FileKindsTests
{
    [Theory]
    [InlineData("events.jsonl")]
    [InlineData("events.ndjson")]
    [InlineData("analysis.ipynb")]
    public void CommonJsonVariantsAreText(string fileName)
    {
        Assert.True(FileKinds.IsText(fileName));
    }

    [Theory]
    [InlineData("request.http")]
    [InlineData("request.rest")]
    [InlineData("values.psv")]
    [InlineData(".env.local")]
    [InlineData("Containerfile")]
    public void CommonDeveloperConfigFilesAreText(string fileName)
    {
        Assert.True(FileKinds.IsText(fileName));
    }

    [Theory]
    [InlineData("renamed-file.bin")]
    [InlineData("no-extension")]
    public void UnknownNamesCanUseSignatureRouting(string fileName)
    {
        Assert.True(FileKinds.CanUseSignatureRouting(fileName));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("movie.mp4")]
    [InlineData("archive.zip")]
    [InlineData("deck.pptx")]
    public void KnownPreviewKindsKeepTheirDeclaredLane(string fileName)
    {
        Assert.False(FileKinds.CanUseSignatureRouting(fileName));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("notes.markdown")]
    public void MarkdownFilesAreMarkdownAndText(string fileName)
    {
        Assert.True(FileKinds.IsMarkdown(fileName));
        Assert.True(FileKinds.IsText(fileName));
    }

    [Theory]
    [InlineData("library.jar")]
    [InlineData("package.nupkg")]
    [InlineData("extension.vsix")]
    [InlineData("browser.xpi")]
    public void ZipDerivedPackagesAreArchives(string fileName)
    {
        Assert.True(FileKinds.IsArchive(fileName));
    }

    [Fact]
    public void PsbIsNotClaimedAsPhotoshopCompositeWithoutPsbDecoder()
    {
        Assert.False(FileKinds.IsPhotoshop("large-format.psb"));
        Assert.True(FileKinds.IsPhotoshop("layered.psd"));
    }

    [Fact]
    public void WebpCanUseBrowserImageFallback()
    {
        Assert.True(FileKinds.IsImage("photo.webp"));
        Assert.True(FileKinds.IsBrowserImage("photo.webp"));
    }

    [Fact]
    public void XlsbIsAnOfficeSpreadsheet()
    {
        Assert.True(FileKinds.IsSpreadsheet("budget.xlsb"));
        Assert.True(FileKinds.IsOffice("budget.xlsb"));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("notes.text")]
    [InlineData("events.log")]
    [InlineData("rows.csv")]
    [InlineData("rows.tsv")]
    public void PlainDataTextUsesNativeTextPreview(string fileName)
    {
        Assert.True(FileKinds.UsesNativeTextPreview(fileName));
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("script.ps1")]
    [InlineData("build.yml")]
    public void StructuredDeveloperFilesCanUseGeneratedHtml(string fileName)
    {
        Assert.False(FileKinds.UsesNativeTextPreview(fileName));
    }

    [Fact]
    public void Utf16TextIsNotRejectedAsBinary()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "script.ps1");
        File.WriteAllText(path, "Write-Output 'hello'", System.Text.Encoding.Unicode);

        var preview = TextPreviewLoader.TryLoad(path);

        Assert.NotNull(preview);
        Assert.Contains("Write-Output", preview!.Text);
    }
}
