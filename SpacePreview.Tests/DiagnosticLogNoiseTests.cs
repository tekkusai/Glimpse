namespace SpacePreview.Tests;

public sealed class DiagnosticLogNoiseTests
{
    [Fact]
    public void OrdinaryUnsupportedSpacePressesDoNotWriteHotPathTriplets()
    {
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var selection = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs");

        Assert.DoesNotContain("space keydown previewVisible=", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("space -> not a supported selection surface", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("foreground hwnd=", selection, StringComparison.Ordinal);
    }
}
