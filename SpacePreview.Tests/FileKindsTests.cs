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

    // Saturday, October 10, 2026, late afternoon.
    private static readonly DateTime Now = new(2026, 10, 10, 17, 0, 0);

    [Theory]
    [InlineData("2026-10-10 16:35", "Today 4:35 PM")]
    [InlineData("2026-10-09 09:12", "Yesterday 9:12 AM")]
    [InlineData("2026-10-07 18:05", "Wednesday 6:05 PM")]
    [InlineData("2026-10-04 08:00", "Sunday 8:00 AM")]
    [InlineData("2026-10-03 08:00", "Oct 3")]
    [InlineData("2026-08-27 11:00", "Aug 27")]
    [InlineData("2025-08-27 11:00", "Aug 27, 2025")]
    public void TimesReadShortAndExactToTheMinute(string when, string expected)
    {
        var time = DateTime.ParseExact(when, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, FileKinds.DescribeWhen(time, Now, new System.Globalization.CultureInfo("en-US")));
    }

    // The words stay the interface's English in every region; only the clock
    // is the user's. Calendars and scripts never leak into the line.
    [Theory]
    [InlineData("de-DE", "Today 16:35", "Wednesday 18:05")]
    [InlineData("ja-JP", "Today 16:35", "Wednesday 18:05")]
    [InlineData("ar-EG", "Today 4:35 PM", "Wednesday 6:05 PM")]
    [InlineData("th-TH", "Today 16:35", "Wednesday 18:05")]
    [InlineData("fa-IR", "Today 16:35", "Wednesday 18:05")]
    public void TimesUseTheUsersClockAndTheInterfacesWords(string name, string today, string thisWeek)
    {
        var culture = new System.Globalization.CultureInfo(name);

        Assert.Equal(today, FileKinds.DescribeWhen(new DateTime(2026, 10, 10, 16, 35, 0), Now, culture));
        Assert.Equal(thisWeek, FileKinds.DescribeWhen(new DateTime(2026, 10, 7, 18, 5, 0), Now, culture));
        Assert.Equal("Aug 27", FileKinds.DescribeWhen(new DateTime(2026, 8, 27, 11, 0, 0), Now, culture));
        Assert.Equal("Aug 27, 2025", FileKinds.DescribeWhen(new DateTime(2025, 8, 27, 11, 0, 0), Now, culture));
    }

    // A one-letter clock would be read as a standard format, which throws
    // for H and h and means the whole short time for t.
    [Theory]
    [InlineData("H", "Today 16")]
    [InlineData("h", "Today 4")]
    [InlineData("t", "Today P")]
    public void AOneLetterClockStillFormats(string pattern, string expected)
    {
        using var temp = new TempDirectory();
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.GetCultureInfo("en-US").Clone();
        culture.DateTimeFormat.ShortTimePattern = pattern;
        var at = new DateTime(2026, 10, 10, 16, 35, 0);
        var notes = Path.Combine(temp.Path, "notes.md");
        File.WriteAllBytes(notes, new byte[5427]);
        File.SetLastWriteTime(notes, at);

        Assert.Equal(expected, FileKinds.DescribeWhen(at, Now, culture));
        Assert.Equal($"5.3 KB · {expected}", FileKinds.Describe(new FileInfo(notes), null, Now, culture));
    }

    [Fact]
    public void DetailsNameAKindOnlyWhereTheNameCannot()
    {
        using var temp = new TempDirectory();
        var culture = new System.Globalization.CultureInfo("en-US");
        var notes = Path.Combine(temp.Path, "notes.md");
        File.WriteAllBytes(notes, new byte[5427]);
        var makefile = Path.Combine(temp.Path, "Makefile");
        File.WriteAllBytes(makefile, new byte[300]);
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "photos.zip"));
        var at = new DateTime(2026, 10, 10, 16, 35, 0);
        File.SetLastWriteTime(notes, at);
        File.SetLastWriteTime(makefile, at);
        Directory.SetLastWriteTime(folder.FullName, at);

        Assert.Equal("5.3 KB · Today 4:35 PM", FileKinds.Describe(new FileInfo(notes), null, Now, culture));
        Assert.Equal("5.3 KB · 81 pages · Today 4:35 PM", FileKinds.Describe(new FileInfo(notes), "81 pages", Now, culture));
        Assert.Equal("File · 300 B · Today 4:35 PM", FileKinds.Describe(new FileInfo(makefile), null, Now, culture));
        Assert.Equal("Folder · Today 4:35 PM", FileKinds.Describe(new DirectoryInfo(folder.FullName), null, Now, culture));
    }
}
