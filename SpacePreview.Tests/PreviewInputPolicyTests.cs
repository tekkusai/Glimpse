using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class PreviewInputPolicyTests
{
    [Theory]
    [InlineData("CabinetWClass")]
    [InlineData("ExploreWClass")]
    [InlineData("XamlExplorerHostIslandWindow")]
    public void SupportedExplorerRootsResolveToExplorerSelection(string className)
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(
            new IntPtr(0x1000),
            className,
            ignoreTextEditing: true,
            rootThreadHasTextEditingFocus: false);

        Assert.NotNull(context);
        Assert.Equal(SelectionSource.Explorer, context!.Source);
    }

    [Fact]
    public void DesktopContextIsSuppressedWhileTextEditingWhenSpaceWouldBeCaptured()
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(
            new IntPtr(0x1000),
            "WorkerW",
            ignoreTextEditing: true,
            rootThreadHasTextEditingFocus: true);

        Assert.Null(context);
    }

    [Fact]
    public void DesktopContextStillWorksWhenTextEditingIsNotBeingIgnored()
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(
            new IntPtr(0x1000),
            "WorkerW",
            ignoreTextEditing: false,
            rootThreadHasTextEditingFocus: true);

        Assert.NotNull(context);
        Assert.Equal(SelectionSource.Desktop, context!.Source);
    }

    [Fact]
    public void CommonFileDialogRequiresVerifiedShellViewFocus()
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(
            new IntPtr(0x2000),
            "#32770",
            ignoreTextEditing: true,
            rootThreadHasTextEditingFocus: false,
            commonFileDialogListHasFocus: true);

        Assert.NotNull(context);
        Assert.True(context!.IsFileDialog);
        Assert.Equal(SelectionSource.CommonFileDialog, context.Source);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void CommonFileDialogYieldsToButtonsAndTextFields(
        bool rootThreadHasTextEditingFocus,
        bool commonFileDialogListHasFocus)
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(
            new IntPtr(0x2000),
            "#32770",
            ignoreTextEditing: true,
            rootThreadHasTextEditingFocus,
            commonFileDialogListHasFocus);

        Assert.Null(context);
    }

    [Fact]
    public void OrdinaryDialogNeverClaimsSpaceByClassNameAlone()
    {
        var context = PreviewInputPolicy.ResolveForegroundContext(
            new IntPtr(0x2000),
            "#32770",
            ignoreTextEditing: true,
            rootThreadHasTextEditingFocus: false,
            commonFileDialogListHasFocus: false);

        Assert.Null(context);
    }

    [Theory]
    [InlineData("CabinetWClass", true)]
    [InlineData("ExploreWClass", true)]
    [InlineData("XamlExplorerHostIslandWindow", true)]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("#32770", true)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("Notepad", false)]
    [InlineData("UnrealWindow", false)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheKeyboardHookServesOnlySurfacesThatOfferASelection(string? className, bool served)
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
    [InlineData("Windows.UI.Xaml.Controls.TextBox")]
    public void KnownTextEditingClassesAreSuppressed(string className)
    {
        Assert.True(PreviewInputPolicy.IsTextEditingClass(className));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EditorShellWindow")]
    [InlineData("DirectUIHWND")]
    public void UnrelatedClassesAreNotSuppressed(string? className)
    {
        Assert.False(PreviewInputPolicy.IsTextEditingClass(className));
    }
}
