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

    [Fact]
    public void PreviewShortcutsDoNotBelongToUnrelatedForegroundApps()
    {
        var allowed = PreviewInputPolicy.IsPreviewShortcutForeground(
            foregroundRoot: new IntPtr(0x3000),
            previewHwnd: new IntPtr(0x1000),
            ownerRootHwnd: new IntPtr(0x2000));

        Assert.False(allowed);
    }

    [Fact]
    public void PreviewShortcutsBelongToPreviewOrOriginalExplorerRootOnly()
    {
        Assert.True(PreviewInputPolicy.IsPreviewShortcutForeground(
            foregroundRoot: new IntPtr(0x1000),
            previewHwnd: new IntPtr(0x1000),
            ownerRootHwnd: new IntPtr(0x2000)));

        Assert.True(PreviewInputPolicy.IsPreviewShortcutForeground(
            foregroundRoot: new IntPtr(0x2000),
            previewHwnd: new IntPtr(0x1000),
            ownerRootHwnd: new IntPtr(0x2000)));
    }

    [Fact]
    public void SpaceYieldsToRenameOnlyInThePreviewOwnerWindow()
    {
        var owner = new IntPtr(0x2000);

        Assert.True(PreviewInputPolicy.ShouldYieldSpaceToOwnerTextEditing(owner, owner, ownerIsTextEditing: true));
        Assert.False(PreviewInputPolicy.ShouldYieldSpaceToOwnerTextEditing(owner, owner, ownerIsTextEditing: false));
        Assert.False(PreviewInputPolicy.ShouldYieldSpaceToOwnerTextEditing(new IntPtr(0x3000), owner, ownerIsTextEditing: true));
    }

    [Fact]
    public void ControllerChecksRenameBeforeTakingTheSpaceLatch()
    {
        var method = TestSource.Method(@"SpacePreview\Services\SpacePreviewController.cs", "OnKeyDown");
        var spaceBranch = method[method.LastIndexOf("if (vk == NativeMethods.VK_SPACE", StringComparison.Ordinal)..];

        Assert.True(
            spaceBranch.IndexOf("ShouldYieldSpaceToOwnerTextEditing", StringComparison.Ordinal)
            < spaceBranch.IndexOf("_spaceKey.TryPress", StringComparison.Ordinal));
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
