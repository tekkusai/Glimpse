using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class CommonFileDialogSelectionTests
{
    // The row is whatever the shell shows for the file on this PC, with its
    // extension or without. The resolver has to find the file either way.
    [Theory]
    [InlineData("upload.bin")]
    [InlineData("notes.txt")]
    [InlineData("report.docx")]
    [InlineData("photo.jpg")]
    [InlineData("archive.tar.gz")]
    [InlineData("README")]
    public void DisplayNameResolvesAgainstTheVerifiedAddressFolder(string fileName)
    {
        using var fixture = new TempDirectory();
        var selected = fixture.CreateFile(fileName);
        var shown = ShellNames.Shown(selected);

        var resolved = CommonFileDialogSelectionService.ResolveExistingEntries(
            fixture.Path,
            [new CommonFileDialogSelectionService.SelectionCandidate(shown.Name, () => shown.Type)]);

        Assert.Equal([selected], resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingAndVirtualSelectionsFailClosed()
    {
        using var fixture = new TempDirectory();
        var candidates = new[]
        {
            new CommonFileDialogSelectionService.SelectionCandidate("Cloud placeholder", () => "File folder"),
            new CommonFileDialogSelectionService.SelectionCandidate("missing.xyz", () => null)
        };

        Assert.Empty(CommonFileDialogSelectionService.ResolveExistingEntries(fixture.Path, candidates));
        Assert.Empty(CommonFileDialogSelectionService.ResolveExistingEntries(null, candidates));
    }

    [Theory]
    [InlineData(FileAttributes.Normal, true)]
    [InlineData(FileAttributes.Archive, true)]
    [InlineData(FileAttributes.Directory, true)]
    [InlineData(FileAttributes.Offline, false)]
    [InlineData((FileAttributes)0x00040000, false)]
    [InlineData((FileAttributes)0x00400000, false)]
    public void OfflineAndCloudOnlyAttributesAreRejected(FileAttributes attributes, bool expected)
    {
        Assert.Equal(expected, CommonFileDialogSelectionService.IsAvailableLocalEntryAttributes(attributes));
    }

    [Theory]
    [InlineData(@"Address: C:\Users\Example\Downloads", @"C:\Users\Example\Downloads")]
    [InlineData(@"C:\Uploads", @"C:\Uploads")]
    [InlineData(@"Address: \\server\share\folder", @"\\server\share\folder")]
    [InlineData("Address: Downloads", "Downloads")]
    [InlineData("", "")]
    public void BreadcrumbTextPreservesDriveAndUncRoots(string value, string expected)
    {
        Assert.Equal(expected, CommonFileDialogSelectionService.NormalizeBreadcrumbFolderText(value));
    }

    [Fact]
    public void NativeBreadcrumbFallbackPrecedesAutomationAddressValues()
    {
        var source = TestSource.Read(@"SpacePreview\Services\CommonFileDialogSelectionService.cs");
        var breadcrumb = source.IndexOf(
            "TryGetBreadcrumbFolderPath(dialogRoot, out var breadcrumbState)",
            StringComparison.Ordinal);
        var automation = source.IndexOf("AddressAutomationIds);", StringComparison.Ordinal);

        Assert.True(breadcrumb >= 0);
        Assert.True(automation > breadcrumb);
        Assert.Contains("GetWindowMessageTextString(toolbar)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownFolderDisplayNameResolvesAfterPickerNavigation()
    {
        using var fixture = new TempDirectory();
        var downloads = fixture.CreateDirectory("Downloads");

        var resolved = CommonFileDialogSelectionService.ResolveBreadcrumbFolderText(
            "Address: Downloads",
            [downloads]);

        Assert.Equal(downloads, resolved, ignoreCase: true);
    }

    [Fact]
    public void SelectedLocalFolderResolvesForFolderCardPreview()
    {
        using var fixture = new TempDirectory();
        var nested = fixture.CreateDirectory("Nested");

        var resolved = CommonFileDialogSelectionService.ResolveExistingEntries(
            fixture.Path,
            [new CommonFileDialogSelectionService.SelectionCandidate("Nested", () => null)]);

        Assert.Equal([nested], resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PickerModeRemovesActionsThatCompeteWithTheModalDialog()
    {
        var window = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");

        Assert.Contains("OpenControl.Visibility = fileDialogMode ? Visibility.Collapsed", window, StringComparison.Ordinal);
        Assert.Contains("PinButton.Visibility = fileDialogMode ? Visibility.Collapsed", window, StringComparison.Ordinal);
        Assert.Contains("SetBrowseControlsVisible(!fileDialogMode && !IsPinned)", window, StringComparison.Ordinal);
        Assert.Contains("open suppressed while file dialog owns commit", window, StringComparison.Ordinal);
        Assert.Contains("if (_isFileDialogMode || _rotationSession is not", window, StringComparison.Ordinal);
        Assert.Equal(HeaderTool.None, PreviewWindow.ToolFor(@"C:\docs\form.pdf", isDirectory: false, fileDialogMode: true));
        Assert.Equal(HeaderTool.None, PreviewWindow.ToolFor(@"C:\docs\photo.jpg", isDirectory: false, fileDialogMode: true));
        Assert.Contains("_lastSelectionSource != source", controller, StringComparison.Ordinal);
        Assert.Contains("_previewOwnerRootHwnd != ownerRoot", controller, StringComparison.Ordinal);
    }

}
