using SpacePreview.Native;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class PreviewZoomShortcutTests
{
    [Theory]
    [InlineData(NativeMethods.VK_ADD)]
    [InlineData(NativeMethods.VK_OEM_PLUS)]
    public void PlusKeysZoomIn(int vk)
    {
        Assert.True(PreviewWindow.IsZoomInKey(vk));
        Assert.False(PreviewWindow.IsZoomOutKey(vk));
        Assert.False(PreviewWindow.IsZoomResetKey(vk));
    }

    [Theory]
    [InlineData(NativeMethods.VK_SUBTRACT)]
    [InlineData(NativeMethods.VK_OEM_MINUS)]
    public void MinusKeysZoomOut(int vk)
    {
        Assert.False(PreviewWindow.IsZoomInKey(vk));
        Assert.True(PreviewWindow.IsZoomOutKey(vk));
        Assert.False(PreviewWindow.IsZoomResetKey(vk));
    }

    [Theory]
    [InlineData(NativeMethods.VK_0)]
    [InlineData(NativeMethods.VK_NUMPAD0)]
    public void ZeroKeysResetZoom(int vk)
    {
        Assert.False(PreviewWindow.IsZoomInKey(vk));
        Assert.False(PreviewWindow.IsZoomOutKey(vk));
        Assert.True(PreviewWindow.IsZoomResetKey(vk));
    }
}
