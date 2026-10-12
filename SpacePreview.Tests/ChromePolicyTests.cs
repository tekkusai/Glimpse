using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The header's decisions that need no window: which tool a kind brings,
/// what Open does, and what Copy diagnostics hands over.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ChromePolicyTests
{
    [Theory]
    [InlineData(@"C:\photos\beach.jpg", nameof(HeaderTool.Rotate))]
    [InlineData(@"C:\photos\scan.PNG", nameof(HeaderTool.Rotate))]
    [InlineData(@"C:\docs\form.pdf", nameof(HeaderTool.Sign))]
    [InlineData(@"C:\docs\notes.txt", nameof(HeaderTool.None))]
    [InlineData(@"C:\music\song.mp3", nameof(HeaderTool.None))]
    public void TheKindDecidesTheToolBeforeAnythingLoads(string path, string expected)
    {
        Assert.Equal(Enum.Parse<HeaderTool>(expected), PreviewWindow.ToolFor(path, isDirectory: false, fileDialogMode: false));
    }

    [Fact]
    public void FoldersAndPickersBringNoWritingTools()
    {
        Assert.Equal(HeaderTool.None, PreviewWindow.ToolFor(@"C:\photos\holiday.jpg", isDirectory: true, fileDialogMode: false));
        Assert.Equal(HeaderTool.None, PreviewWindow.ToolFor(@"C:\photos\beach.jpg", isDirectory: false, fileDialogMode: true));
        Assert.Equal(HeaderTool.None, PreviewWindow.ToolFor(@"C:\docs\form.pdf", isDirectory: false, fileDialogMode: true));
    }

    [Fact]
    public void SpreadsheetsZoomOnlyWhenTheyConvert()
    {
        var expected = OfficeDocumentPdfConverter.IsAvailable ? HeaderTool.Zoom : HeaderTool.None;
        Assert.Equal(expected, PreviewWindow.ToolFor(@"C:\docs\budget.xlsx", isDirectory: false, fileDialogMode: false));
        Assert.Equal(expected, PreviewWindow.ToolFor(@"C:\docs\budget.xlsx", isDirectory: false, fileDialogMode: true));
    }

    [Fact]
    public void FoldersOpenInFileExplorer()
    {
        using var temp = new TempDirectory();
        var target = PreviewWindow.ResolveOpenTarget(temp.Path, isDirectory: true);

        Assert.Equal("File Explorer", target.AppName);
        Assert.EndsWith("explorer.exe", target.IconSource, StringComparison.OrdinalIgnoreCase);
        Assert.False(target.AsksForApp);
    }

    [Fact]
    public void ProgramsAndShortcutsRunThemselves()
    {
        using var temp = new TempDirectory();
        foreach (var name in new[] { "setup.exe", "tool.lnk" })
        {
            var path = temp.CreateFile(name);
            var target = PreviewWindow.ResolveOpenTarget(path, isDirectory: false);
            Assert.Null(target.AppName);
            Assert.Equal(path, target.IconSource);
            Assert.False(target.AsksForApp);
        }
    }

    [Fact]
    public void AnUnassociatedTypeAsksWindowsToPick()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("sample.glimpse-unassociated-kind");

        Assert.True(PreviewWindow.ResolveOpenTarget(path, isDirectory: false).AsksForApp);
    }

    [Fact]
    public void DiagnosticsCarryTheRecentLogWithPathsRedacted()
    {
        var text = SettingsWindow.BuildDiagnostics(
            "9.9.9",
            "LibreOffice",
            ["2026-10-10 12:00:00.000 opened C:\\Users\\someone\\Secret plans.docx", "2026-10-10 12:00:01.000 reveal +80ms"]);

        Assert.Contains("Version: 9.9.9", text, StringComparison.Ordinal);
        Assert.Contains("Documents: LibreOffice", text, StringComparison.Ordinal);
        Assert.Contains("reveal +80ms", text, StringComparison.Ordinal);
        Assert.Contains("<path:*.docx:", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\someone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret plans", text, StringComparison.Ordinal);
    }

    // Copy diagnostics is pasted into public issues: a previewed file's
    // name must not travel with it, whatever follows the path on the line
    // and whichever build wrote the line.
    [Theory]
    [InlineData(@"browse +1 -> C:\Users\someone\Secret plans.docx (2 of 14)", "<path:*.docx:")]
    [InlineData(@"epub preview failed \\nas\books\Secret plans.v2.epub: XmlException: boom", "<path:*.epub:")]
    [InlineData(@"opened C:\Users\someone\Secret plans", "<path:*:")]
    [InlineData("pdf preview <path:Secret plans.pdf:1a2b3c4d>", "<path:*.pdf:1a2b3c4d>")]
    [InlineData("browse +1 -> <path:Secret plans.docx (2 of 14):1a2b3c4d>", "<path:*.docx:1a2b3c4d>")]
    // A path straight after a colon was once written whole.
    [InlineData(@"pdf rescale not applicable <path:C:\Users\someone\Secret plans.ai>", "<path:*.ai:")]
    public void DiagnosticsNeverCarryAFileName(string line, string expected)
    {
        var text = SettingsWindow.BuildDiagnostics("9.9.9", "LibreOffice", [line]);

        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogTailIsTheLastLinesAndReadsBesideAWriter()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "Glimpse.log");
        File.WriteAllLines(path, Enumerable.Range(1, 250).Select(i => $"line {i}"));
        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var tail = SettingsWindow.ReadLogTail(path, SettingsWindow.DiagnosticLogLines);

        Assert.Equal(SettingsWindow.DiagnosticLogLines, tail.Count);
        Assert.Equal("line 51", tail[0]);
        Assert.Equal("line 250", tail[^1]);
        Assert.Empty(SettingsWindow.ReadLogTail(Path.Combine(temp.Path, "missing.log"), 10));
    }
}
