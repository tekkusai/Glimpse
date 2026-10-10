using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class CommonFileDialogSelectionTests
{
    [Fact]
    public void SelectedElementPathOutranksTheDialogEditFallback()
    {
        using var fixture = new TempDirectory();
        var selected = fixture.CreateFile("selected.txt");
        var staleEdit = fixture.CreateFile("stale.txt");
        var candidates = new[]
        {
            new CommonFileDialogSelectionService.SelectionCandidate(
                "selected.txt",
                [$"Selected item: {selected}"])
        };

        var resolved = CommonFileDialogSelectionService.ResolveExistingEntries(
            fixture.Path,
            candidates,
            [staleEdit]);

        Assert.Equal(selected, resolved[0], ignoreCase: true);
    }

    [Fact]
    public void DisplayNameResolvesAgainstTheVerifiedAddressFolder()
    {
        using var fixture = new TempDirectory();
        var selected = fixture.CreateFile("upload.bin");
        var candidates = new[]
        {
            new CommonFileDialogSelectionService.SelectionCandidate("upload.bin", [])
        };

        var resolved = CommonFileDialogSelectionService.ResolveExistingEntries(
            fixture.Path,
            candidates);

        Assert.Single(resolved);
        Assert.Equal(selected, resolved[0], ignoreCase: true);
    }

    [Fact]
    public void QuotedMultiSelectionEditValuePreservesDialogOrder()
    {
        using var fixture = new TempDirectory();
        var first = fixture.CreateFile("first image.png");
        var second = fixture.CreateFile("second image.png");

        var resolved = CommonFileDialogSelectionService.ResolveExistingEntries(
            fixture.Path,
            [],
            ["\"first image.png\" \"second image.png\""]);

        Assert.Equal([first, second], resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingAndVirtualSelectionsFailClosed()
    {
        using var fixture = new TempDirectory();
        var candidates = new[]
        {
            new CommonFileDialogSelectionService.SelectionCandidate("Cloud placeholder", ["shell:Downloads"]),
            new CommonFileDialogSelectionService.SelectionCandidate("missing.xyz", [])
        };

        var resolved = CommonFileDialogSelectionService.ResolveExistingEntries(
            fixture.Path,
            candidates);

        Assert.Empty(resolved);
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
    [InlineData("one.txt", new[] { "one.txt" })]
    [InlineData("\"one.txt\" \"two words.txt\"", new[] { "one.txt", "two words.txt" })]
    [InlineData("", new string[0])]
    public void DialogFileNameParserHandlesSingleAndQuotedSets(string value, string[] expected)
    {
        Assert.Equal(expected, CommonFileDialogSelectionService.SplitDialogFileNames(value));
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
            [new CommonFileDialogSelectionService.SelectionCandidate("Nested", [])]);

        Assert.Equal([nested], resolved, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void HookUsesOnlyThePrevalidatedCacheAndFailsOpenWhenItIsMissing()
    {
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var hookStart = controller.IndexOf("private bool OnKeyDown", StringComparison.Ordinal);
        var hookEnd = controller.IndexOf("private void OnKeyUp", hookStart, StringComparison.Ordinal);
        var hook = controller[hookStart..hookEnd];

        Assert.Contains("TryGetCachedSelection", hook, StringComparison.Ordinal);
        Assert.Contains("ArmPendingFileDialogPreview", hook, StringComparison.Ordinal);
        Assert.Contains("file dialog selection not ready, deferred and passed through", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("CommonFileDialogSelectionService.TryGetSelection", hook, StringComparison.Ordinal);
        var monitor = TestSource.Read(@"SpacePreview\Services\CommonFileDialogSelectionMonitor.cs");
        Assert.Contains("Task.Run", monitor, StringComparison.Ordinal);
        Assert.Contains("WaitAsync(TimeSpan.FromSeconds(2))", monitor, StringComparison.Ordinal);
        Assert.Contains("RefreshCompleted?.Invoke(dialogRoot, selection)", monitor, StringComparison.Ordinal);
        Assert.Contains("&& _inputHooksActive", controller, StringComparison.Ordinal);
        Assert.Contains("ShouldPassThroughInput()", controller, StringComparison.Ordinal);
        Assert.Contains("ClearPendingFileDialogPreview();", controller, StringComparison.Ordinal);
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
        Assert.Contains("file dialog lost foreground -> hide", controller, StringComparison.Ordinal);
        Assert.Contains("file dialog closed -> hide", controller, StringComparison.Ordinal);
        Assert.Contains("_lastSelectionSource != source", controller, StringComparison.Ordinal);
        Assert.Contains("_previewOwnerRootHwnd != ownerRoot", controller, StringComparison.Ordinal);
    }

}
