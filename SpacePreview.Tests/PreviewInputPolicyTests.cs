using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class PreviewInputPolicyTests
{
    [Theory]
    [InlineData("CabinetWClass")]
    [InlineData("ExploreWClass")]
    public void SupportedExplorerRootsResolveToExplorerSelection(string className)
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(new IntPtr(0x1000), className);

        Assert.NotNull(context);
        Assert.Equal(SelectionSource.Explorer, context!.Source);
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    public void TheDesktopOffersItsSelection(string className)
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(new IntPtr(0x1000), className);

        Assert.NotNull(context);
        Assert.Equal(SelectionSource.Desktop, context!.Source);
    }

    /// <summary>
    /// XamlExplorerHostIslandWindow is Explorer's Task View and Alt+Tab
    /// window. 3.0.0 took it for a file window and claimed a bare Space there.
    /// </summary>
    [Fact]
    public void TaskViewOffersNothing()
    {
        Assert.Null(PreviewInputPolicy.ResolveForegroundContext(new IntPtr(0x1000), "XamlExplorerHostIslandWindow"));
    }

    [Fact]
    public void CommonFileDialogRequiresVerifiedShellViewFocus()
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(
            new IntPtr(0x2000),
            "#32770",
            commonFileDialogListHasFocus: true);

        Assert.NotNull(context);
        Assert.True(context!.IsFileDialog);
        Assert.Equal(SelectionSource.CommonFileDialog, context.Source);
    }

    [Fact]
    public void OrdinaryDialogNeverClaimsSpaceByClassNameAlone()
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(
            new IntPtr(0x2000),
            "#32770",
            commonFileDialogListHasFocus: false);

        Assert.Null(context);
    }

    [Theory]
    [InlineData("CabinetWClass", true)]
    [InlineData("ExploreWClass", true)]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    // A dialog is served once its windows show a file picker, never by its class.
    [InlineData("#32770", false)]
    [InlineData("XamlExplorerHostIslandWindow", false)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("Notepad", false)]
    [InlineData("UnrealWindow", false)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheKeyboardHookServesByClassOnlyExplorerAndTheDesktop(string? className, bool served)
    {
        // Everywhere else Glimpse removes its hook and leaves the input path.
        Assert.Equal(served, PreviewInputPolicy.IsServedSurfaceClass(className));
    }

    [Fact]
    public void MissingSelectionHidesInsteadOfLeavingStalePreview()
    {
        var decision = PreviewInputPolicy.DecideRefresh(null, @"C:\Users\PreviewUser\Downloads\old.png");

        Assert.Equal(RefreshSelectionAction.HidePreview, decision.Action);
    }

    [Theory]
    [InlineData(4, 5, 4, 0, false)] // Older than the newest issued input.
    [InlineData(5, 5, 4, 0, false)] // Released before its lookup started.
    [InlineData(5, 5, 5, 0, true)]  // Released during/after its own lookup.
    [InlineData(5, 5, 5, 5, false)] // A changed selection needs no verification.
    [InlineData(6, 6, 6, 5, true)]
    public void NavigationReleaseOnlyVerifiesTheNewestUnresolvedRequest(
        long verificationGeneration,
        long newestIssuedGeneration,
        long startedGenerationAtRelease,
        long lastChangedGeneration,
        bool expected)
    {
        Assert.Equal(
            expected,
            PreviewInputPolicy.ShouldVerifyNavigationRelease(
                verificationGeneration,
                newestIssuedGeneration,
                startedGenerationAtRelease,
                lastChangedGeneration));
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("RichEdit20W")]
    [InlineData("RICHEDIT50W")]
    public void ClassicTextBoxesAreTextEditingClasses(string className)
    {
        Assert.True(PreviewInputPolicy.IsTextEditingClass(className));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EditorShellWindow")]
    [InlineData("DirectUIHWND")]
    [InlineData("InputSiteWindowClass")]
    public void UnrelatedClassesAreNotSuppressed(string? className)
    {
        Assert.False(PreviewInputPolicy.IsTextEditingClass(className));
    }

    [Theory]
    [InlineData("Microsoft.UI.Content.DesktopChildSiteBridge", true)]
    [InlineData("InputSiteWindowClass", false)]
    [InlineData("DirectUIHWND", false)]
    [InlineData("SHELLDLL_DefView", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ExplorersHeaderIsKnownByTheWindowItLivesIn(string? className, bool header)
    {
        Assert.Equal(header, PreviewInputPolicy.IsHeaderIslandClass(className));
    }
}
