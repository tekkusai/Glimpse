using Microsoft.Web.WebView2.Core;

namespace SpacePreview.Tests;

/// <summary>
/// The mouse wheel over a real PDF viewer: no click, no key, and no change
/// to the viewer's address, so nothing in the window learns of it. A test
/// that turns it may not ask the viewer whether it moved, because asking is
/// what the test is about. The viewer's picture says so instead.
/// </summary>
internal static class PdfViewerWheel
{
    /// <summary>One turn of the wheel over the pages.</summary>
    public static Task TurnAsync(CoreWebView2 viewer, int distance) =>
        viewer.CallDevToolsProtocolMethodAsync(
            "Input.dispatchMouseEvent",
            $"{{\"type\":\"mouseWheel\",\"x\":450,\"y\":350,\"deltaX\":0,\"deltaY\":{distance}}}");

    /// <summary>
    /// Turns the wheel until the pages move, then waits for them to come to
    /// rest. A viewer still loading its document swallows the wheel.
    /// </summary>
    public static async Task ScrollAsync(CoreWebView2 viewer, int distance)
    {
        var before = await StillPictureAsync(viewer);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        byte[] after;
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The viewer never took the wheel.");
            await TurnAsync(viewer, distance);
            after = await StillPictureAsync(viewer);
        }
        while (after.AsSpan().SequenceEqual(before));
    }

    /// <summary>The viewer's picture once two in a row are the same.</summary>
    private static async Task<byte[]> StillPictureAsync(CoreWebView2 viewer)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var last = await PictureAsync(viewer);
        while (true)
        {
            await Task.Delay(150);
            var next = await PictureAsync(viewer);
            if (next.AsSpan().SequenceEqual(last))
            {
                return next;
            }

            Assert.True(DateTime.UtcNow < deadline, "The viewer never came to rest.");
            last = next;
        }
    }

    private static async Task<byte[]> PictureAsync(CoreWebView2 viewer)
    {
        using var picture = new MemoryStream();
        await viewer.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, picture);
        return picture.ToArray();
    }
}
