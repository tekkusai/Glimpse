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

    /// <summary>
    /// Lines that fired per hook install, per drag step or per header build
    /// were most of the field log and diagnosed nothing; failures still log.
    /// </summary>
    [Theory]
    [InlineData(@"SpacePreview\Services\KeyboardHook.cs", "keyboard hook installed")]
    [InlineData(@"SpacePreview\Services\KeyboardHook.cs", "keyboard hook removed")]
    [InlineData(@"SpacePreview\Services\MouseHook.cs", "mouse hook installed")]
    [InlineData(@"SpacePreview\Services\MouseHook.cs", "mouse hook removed")]
    [InlineData(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "maximize capped")]
    [InlineData(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "openpill")]
    public void RoutineEventsStayOutOfTheLog(string file, string line)
    {
        Assert.DoesNotContain(line, TestSource.Read(file), StringComparison.Ordinal);
    }
}
