using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class PreviewMouseDismissalPolicyTests
{
    [Fact]
    public void ClickingPreviewOwnedPopupDoesNotHideThePreview()
    {
        Assert.False(PreviewMouseDismissalPolicy.ShouldHideActivePreview(
            previewVisible: true,
            hitOwnedWindow: false,
            hitOwnedPopup: true));
    }

    [Fact]
    public void ClickingOutsideThePreviewAndItsPopupsHidesThePreview()
    {
        Assert.True(PreviewMouseDismissalPolicy.ShouldHideActivePreview(
            previewVisible: true,
            hitOwnedWindow: false,
            hitOwnedPopup: false));
    }

    [Fact]
    public void HiddenPreviewStaysHiddenOnOutsideClicks()
    {
        Assert.False(PreviewMouseDismissalPolicy.ShouldHideActivePreview(
            previewVisible: false,
            hitOwnedWindow: false,
            hitOwnedPopup: false));
    }

    [Fact]
    public void SuppressedOutsideDismissKeepsShareUiClicksFromClosingPreview()
    {
        Assert.False(PreviewMouseDismissalPolicy.ShouldHideActivePreview(
            previewVisible: true,
            hitOwnedWindow: false,
            hitOwnedPopup: false,
            outsideDismissSuppressed: true));
    }
}
